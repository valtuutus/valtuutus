using System.Data.Common;
using System.Text.Json.Nodes;
using MySqlConnector;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Data.Db;
using static Valtuutus.Data.MySql.MySqlSql;

namespace Valtuutus.Data.MySql;

public class MySqlDataReaderProvider : RateLimiterExecuter, IDataReaderProvider, IRelationalCheckOps
{
    private readonly MySqlQueries _q;
    private readonly string _dbSystem;
    private readonly MySqlDataSource _dataSource;

    public MySqlDataReaderProvider(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        ValtuutusMySqlOptions dbOptions) : base(options)
    {
        _q = MySqlQueries.Get(dbOptions);
        _dbSystem = MySqlConnections.DbSystem(dbOptions.ServerType);
        // MySqlConnector drops the password from ConnectionString once a connection opens, so the
        // string is read from a fresh connection that is never opened.
        using var probeConnection = (MySqlConnection)connectionFactory();
        _dataSource = MySqlConnections.GetOrCreateDataSource(probeConnection.ConnectionString);
    }

    private static RelationTuple ReadRelationTuple(DbDataReader reader, ref byte[] buffer) =>
        new(ReadKey(reader, 0, ref buffer), ReadKey(reader, 1, ref buffer), ReadKey(reader, 2, ref buffer),
            ReadKey(reader, 3, ref buffer), ReadKey(reader, 4, ref buffer), ReadKey(reader, 5, ref buffer));

    private static JsonValue ParseJsonValue(string rawJson)
    {
        return JsonNode.Parse(rawJson) as JsonValue
            ?? throw new InvalidOperationException("Attribute value must deserialize to a JsonValue.");
    }

    private static AttributeTuple ReadAttributeTuple(DbDataReader reader, ref byte[] buffer) =>
        new(ReadKey(reader, 0, ref buffer), ReadKey(reader, 1, ref buffer), ReadKey(reader, 2, ref buffer),
            ParseJsonValue(reader.GetString(3)));

    // EXISTS and boolean combinations of it come back as integers.
    private static async Task<bool> ExecuteBoolean(DbCommand command, CancellationToken cancellationToken) =>
        Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));

    private static async Task<PooledList<RelationTuple>> ReadRelations(DbCommand command, CancellationToken cancellationToken)
    {
        var pooled = PooledList<RelationTuple>.Rent();
        var buffer = RentKeyBuffer();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                pooled.Add(ReadRelationTuple(reader, ref buffer));
            return pooled;
        }
        catch
        {
            pooled.Dispose();
            throw;
        }
        finally
        {
            ReturnKeyBuffer(buffer);
        }
    }

    private static async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> ReadAttributeDictionary(
        DbCommand command, CancellationToken cancellationToken)
    {
        var dict = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
        var buffer = RentKeyBuffer();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var tuple = ReadAttributeTuple(reader, ref buffer);
                dict[(tuple.Attribute, tuple.EntityId)] = tuple;
            }
            return dict;
        }
        finally
        {
            ReturnKeyBuffer(buffer);
        }
    }

    private static async Task<List<AttributeTuple>> ReadAttributeList(DbCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<AttributeTuple>();
        var buffer = RentKeyBuffer();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(ReadAttributeTuple(reader, ref buffer));
            return rows;
        }
        finally
        {
            ReturnKeyBuffer(buffer);
        }
    }

    private static async Task<List<string>> ReadKeys(DbCommand command, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var buffer = RentKeyBuffer();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(ReadKey(reader, 0, ref buffer));
            return result;
        }
        finally
        {
            ReturnKeyBuffer(buffer);
        }
    }

    private static async Task<HashSet<string>> ReadKeySet(DbCommand command, int capacity, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(capacity, StringComparer.Ordinal);
        var buffer = RentKeyBuffer();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(ReadKey(reader, 0, ref buffer));
            return result;
        }
        finally
        {
            ReturnKeyBuffer(buffer);
        }
    }

    private static void AddScopeParameters(DbParameterCollection parameters, EntityScope? scope)
    {
        if (scope is not { } s)
            return;
        AddString(parameters, "@scope_relation", s.Relation, 64);
        AddString(parameters, "@scope_subject_type", s.SubjectType, 256);
        AddString(parameters, "@scope_subject_id", s.SubjectId, 64);
    }

    // One value binds to the equality form's scalar parameter, any other count to the JSON form's array.
    private static void AddOneOrMany(DbParameterCollection parameters, string scalarName, string arrayName,
        string[] values, int scalarSize)
    {
        if (values.Length == 1)
            AddString(parameters, scalarName, values[0], scalarSize);
        else
            AddJsonArray(parameters, arrayName, values);
    }

    private static string[] AsArray(IEnumerable<string> values) => values as string[] ?? values.ToArray();

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(_q.GetLatestSnapToken);
            var res = await command.ExecuteScalarAsync(cancellationToken) as string;
            return res is not null ? new SnapToken(res) : null;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            var noSubjectFilters = string.IsNullOrWhiteSpace(tupleFilter.SubjectId)
                && string.IsNullOrWhiteSpace(tupleFilter.SubjectRelation)
                && string.IsNullOrWhiteSpace(tupleFilter.SubjectType);
            await using var command = _dataSource.CreateCommand(
                noSubjectFilters ? _q.SelectRelationsByTupleFilterNoSubject : _q.SelectRelationsByTupleFilter);
            AddString(command.Parameters, "@entity_type", tupleFilter.EntityType, 256);
            AddString(command.Parameters, "@entity_id", tupleFilter.EntityId, 64);
            AddString(command.Parameters, "@relation", tupleFilter.Relation, 64);
            if (!noSubjectFilters)
            {
                AddNullableString(command.Parameters, "@subject_id", tupleFilter.SubjectId, 64);
                AddNullableString(command.Parameters, "@subject_relation", tupleFilter.SubjectRelation, 64);
                AddNullableString(command.Parameters, "@subject_type", tupleFilter.SubjectType, 256);
            }
            AddSnapToken(command.Parameters, tupleFilter.SnapToken);
            return await ReadRelations(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(_q.HasDirectRelation);
            AddString(command.Parameters, "@entity_type", tupleFilter.EntityType, 256);
            AddString(command.Parameters, "@entity_id", tupleFilter.EntityId, 64);
            AddString(command.Parameters, "@relation", tupleFilter.Relation, 64);
            AddString(command.Parameters, "@subject_id", subjectId, 64);
            AddSnapToken(command.Parameters, tupleFilter.SnapToken);
            return await ExecuteBoolean(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(_q.GetIndirectRelations);
            AddString(command.Parameters, "@entity_type", tupleFilter.EntityType, 256);
            AddString(command.Parameters, "@entity_id", tupleFilter.EntityId, 64);
            AddString(command.Parameters, "@relation", tupleFilter.Relation, 64);
            AddSnapToken(command.Parameters, tupleFilter.SnapToken);
            return await ReadRelations(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(
                entityIds.Length == 1 ? _q.HasDirectRelation : _q.HasAnyDirectRelationMany);
            AddString(command.Parameters, "@entity_type", entityType, 256);
            AddOneOrMany(command.Parameters, "@entity_id", "@entity_ids", entityIds, 64);
            AddString(command.Parameters, "@relation", relation, 64);
            AddString(command.Parameters, "@subject_id", subjectId, 64);
            AddSnapToken(command.Parameters, snapToken);
            return await ExecuteBoolean(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<HashSet<string>> HasAnyOfDirectRelations(string entityType, string entityId, string[] relationNames,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(
                relationNames.Length == 1 ? _q.HasAnyOfDirectRelationsOne : _q.HasAnyOfDirectRelationsMany);
            AddString(command.Parameters, "@entity_type", entityType, 256);
            AddString(command.Parameters, "@entity_id", entityId, 64);
            AddOneOrMany(command.Parameters, "@relation", "@relations", relationNames, 64);
            AddString(command.Parameters, "@subject_id", subjectId, 64);
            AddSnapToken(command.Parameters, snapToken);
            return await ReadKeySet(command, relationNames.Length, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<HashSet<string>> HasAnyOfAttributes(string entityType, string entityId, string[] attributeNames,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(
                attributeNames.Length == 1 ? _q.HasAnyOfAttributesOne : _q.HasAnyOfAttributesMany);
            AddString(command.Parameters, "@entity_type", entityType, 256);
            AddString(command.Parameters, "@entity_id", entityId, 64);
            AddOneOrMany(command.Parameters, "@attribute", "@attributes", attributeNames, 64);
            AddSnapToken(command.Parameters, snapToken);
            return await ReadKeySet(command, attributeNames.Length, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entityIds, string subjectRelation, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            var ids = AsArray(entityIds);
            await using var command = _dataSource.CreateCommand(
                ids.Length == 1 ? _q.RelationsWithEntityOne : _q.RelationsWithEntityMany);
            AddString(command.Parameters, "@subject_type", subjectType, 256);
            AddString(command.Parameters, "@entity_type", entityRelationFilter.EntityType, 256);
            AddString(command.Parameters, "@relation", entityRelationFilter.Relation, 64);
            AddOneOrMany(command.Parameters, "@entity_id", "@entity_ids", ids, 64);
            AddString(command.Parameters, "@subject_relation", subjectRelation, 64);
            AddSnapToken(command.Parameters, entityRelationFilter.SnapToken);
            return await ReadRelations(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter, string[] subjectsIds, string subjectType, string subjectRelation, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            var single = subjectsIds.Length == 1;
            var sql = scope is not null
                ? (single ? _q.RelationsWithSubjectOneScoped : _q.RelationsWithSubjectManyScoped)
                : (single ? _q.RelationsWithSubjectOne : _q.RelationsWithSubjectMany);
            await using var command = _dataSource.CreateCommand(sql);
            AddString(command.Parameters, "@entity_type", entityFilter.EntityType, 256);
            AddString(command.Parameters, "@relation", entityFilter.Relation, 64);
            AddString(command.Parameters, "@subject_type", subjectType, 256);
            AddString(command.Parameters, "@subject_relation", subjectRelation, 64);
            AddOneOrMany(command.Parameters, "@subject_id", "@subject_ids", subjectsIds, 64);
            AddScopeParameters(command.Parameters, scope);
            AddSnapToken(command.Parameters, entityFilter.SnapToken);
            return await ReadRelations(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIdsMultiRelation(
        string entityType, string[] relationNames, string[] subjectsIds, string subjectType, string subjectRelation,
        SnapToken snapToken, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            var single = subjectsIds.Length == 1;
            var sql = scope is not null
                ? (single ? _q.RelationsWithSubjectOneMultiRelationScoped : _q.RelationsWithSubjectManyMultiRelationScoped)
                : (single ? _q.RelationsWithSubjectOneMultiRelation : _q.RelationsWithSubjectManyMultiRelation);
            await using var command = _dataSource.CreateCommand(sql);
            AddString(command.Parameters, "@entity_type", entityType, 256);
            AddJsonArray(command.Parameters, "@relations", relationNames);
            AddString(command.Parameters, "@subject_type", subjectType, 256);
            AddString(command.Parameters, "@subject_relation", subjectRelation, 64);
            AddOneOrMany(command.Parameters, "@subject_id", "@subject_ids", subjectsIds, 64);
            AddScopeParameters(command.Parameters, scope);
            AddSnapToken(command.Parameters, snapToken);
            return await ReadRelations(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIdsMultiRelation(
        string entityType, string[] relationNames, string subjectType, IEnumerable<string> entityIds,
        string subjectRelation, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            var ids = AsArray(entityIds);
            await using var command = _dataSource.CreateCommand(
                ids.Length == 1 ? _q.RelationsWithEntityOneMultiRelation : _q.RelationsWithEntityManyMultiRelation);
            AddString(command.Parameters, "@entity_type", entityType, 256);
            AddJsonArray(command.Parameters, "@relations", relationNames);
            AddString(command.Parameters, "@subject_type", subjectType, 256);
            AddOneOrMany(command.Parameters, "@entity_id", "@entity_ids", ids, 64);
            AddString(command.Parameters, "@subject_relation", subjectRelation, 64);
            AddSnapToken(command.Parameters, snapToken);
            return await ReadRelations(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoined(
        EntityRelationFilter mainFilter, string subEntityType, string subjectRelation, string subRelation,
        string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(scope is not null ? _q.JoinedScoped : _q.Joined);
            AddSnapToken(command.Parameters, mainFilter.SnapToken);
            AddString(command.Parameters, "@entity_type", mainFilter.EntityType, 256);
            AddString(command.Parameters, "@relation", mainFilter.Relation, 64);
            AddString(command.Parameters, "@sub_entity_type", subEntityType, 256);
            AddString(command.Parameters, "@subject_relation", subjectRelation, 64);
            AddString(command.Parameters, "@sub_relation", subRelation, 64);
            AddString(command.Parameters, "@subject_type", subjectType, 256);
            AddString(command.Parameters, "@subject_id", subjectId, 64);
            AddScopeParameters(command.Parameters, scope);
            return await ReadRelations(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoinedByEntityIds(
        EntityRelationFilter mainFilter, IEnumerable<string> entityIds, string subEntityType, string subjectRelation,
        string subRelation, string subjectType, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            var ids = AsArray(entityIds);
            await using var command = _dataSource.CreateCommand(
                ids.Length == 1 ? _q.JoinedByEntityIdsOne : _q.JoinedByEntityIdsMany);
            AddSnapToken(command.Parameters, mainFilter.SnapToken);
            AddString(command.Parameters, "@entity_type", mainFilter.EntityType, 256);
            AddString(command.Parameters, "@relation", mainFilter.Relation, 64);
            AddOneOrMany(command.Parameters, "@entity_id", "@entity_ids", ids, 64);
            AddString(command.Parameters, "@sub_entity_type", subEntityType, 256);
            AddString(command.Parameters, "@subject_relation", subjectRelation, 64);
            AddString(command.Parameters, "@sub_relation", subRelation, 64);
            AddString(command.Parameters, "@subject_type", subjectType, 256);
            return await ReadRelations(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasTupleToUserSetRelation(
        string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(_q.HasTupleToUserSetRelation);
            AddSnapToken(command.Parameters, snapToken);
            AddString(command.Parameters, "@entity_type", entityType, 256);
            AddString(command.Parameters, "@entity_id", entityId, 64);
            AddString(command.Parameters, "@tuple_set_relation", tupleSetRelation, 64);
            AddString(command.Parameters, "@sub_entity_type", subEntityType, 256);
            AddString(command.Parameters, "@computed_relation", computedRelation, 64);
            AddString(command.Parameters, "@subject_type", subjectType, 256);
            AddString(command.Parameters, "@subject_id", subjectId, 64);
            return await ExecuteBoolean(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasUsersetJoinRelation(
        string entityType, string entityId, string relation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(_q.HasUsersetJoinRelation);
            AddSnapToken(command.Parameters, snapToken);
            AddString(command.Parameters, "@entity_type", entityType, 256);
            AddString(command.Parameters, "@entity_id", entityId, 64);
            AddString(command.Parameters, "@relation", relation, 64);
            AddString(command.Parameters, "@sub_entity_type", subEntityType, 256);
            AddString(command.Parameters, "@computed_relation", computedRelation, 64);
            AddString(command.Parameters, "@subject_type", subjectType, 256);
            AddString(command.Parameters, "@subject_id", subjectId, 64);
            return await ExecuteBoolean(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasFusedExpression(string entityType, string entityId, IReadOnlyList<FusedCheckLeaf> leaves,
        bool requireAll, string? subjectType, string? subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(FusedExpressionSql.BuildStatement(leaves, requireAll, _q));
            AddString(command.Parameters, "@entity_type", entityType, 256);
            AddString(command.Parameters, "@entity_id", entityId, 64);
            if (subjectType is not null) AddString(command.Parameters, "@subject_type", subjectType, 256);
            if (subjectId is not null) AddString(command.Parameters, "@subject_id", subjectId, 64);
            AddSnapToken(command.Parameters, snapToken);
            for (var i = 0; i < leaves.Count; i++)
                FusedExpressionSql.WriteLeafParameters(command.Parameters, leaves[i], i);
            return await ExecuteBoolean(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasTrueBoolAttribute(string entityType, string entityId, string attribute,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(_q.HasTrueBoolAttribute);
            AddString(command.Parameters, "@entity_type", entityType, 256);
            AddString(command.Parameters, "@entity_id", entityId, 64);
            AddString(command.Parameters, "@attribute", attribute, 64);
            AddSnapToken(command.Parameters, snapToken);
            return await ExecuteBoolean(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(_q.AttributesByAttribute);
            AddString(command.Parameters, "@entity_type", filter.EntityType, 256);
            AddString(command.Parameters, "@attribute", filter.Attribute, 64);
            AddNullableString(command.Parameters, "@entity_id", filter.EntityId, 64);
            AddSnapToken(command.Parameters, filter.SnapToken);
            return await ReadAttributeList(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            var single = filter.Attributes.Length == 1;
            var sql = scope is not null
                ? (single ? _q.AttributesScopedOne : _q.AttributesScopedMany)
                : (single ? _q.AttributesByAttributesOne : _q.AttributesByAttributesMany);
            await using var command = _dataSource.CreateCommand(sql);
            // The unscoped statement filters on entity_id; the scoped one reads its scope parameters instead.
            if (scope is null)
                AddNullableString(command.Parameters, "@entity_id", filter.EntityId, 64);
            AddString(command.Parameters, "@entity_type", filter.EntityType, 256);
            AddOneOrMany(command.Parameters, "@attribute", "@attributes", filter.Attributes, 64);
            AddScopeParameters(command.Parameters, scope);
            AddSnapToken(command.Parameters, filter.SnapToken);
            return await ReadAttributeDictionary(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<AttributeTuple>> GetAttributesSingleEntity(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(
                filter.Attributes.Length == 1 ? _q.AttributesSingleEntityOne : _q.AttributesSingleEntityMany);
            AddString(command.Parameters, "@entity_id",
                filter.EntityId ?? throw new ArgumentException("EntityId must be set.", nameof(filter)), 64);
            AddString(command.Parameters, "@entity_type", filter.EntityType, 256);
            AddOneOrMany(command.Parameters, "@attribute", "@attributes", filter.Attributes, 64);
            AddSnapToken(command.Parameters, filter.SnapToken);

            var pooled = PooledList<AttributeTuple>.Rent();
            var buffer = RentKeyBuffer();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    pooled.Add(ReadAttributeTuple(reader, ref buffer));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
            finally
            {
                ReturnKeyBuffer(buffer);
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            var ids = AsArray(entitiesIds);
            await using var command = _dataSource.CreateCommand(
                ids.Length == 1 ? _q.AttributesWithEntityIdsOne : _q.AttributesWithEntityIdsMany);
            AddString(command.Parameters, "@entity_type", filter.EntityType, 256);
            AddString(command.Parameters, "@attribute", filter.Attribute, 64);
            AddOneOrMany(command.Parameters, "@entity_id", "@entity_ids", ids, 64);
            AddSnapToken(command.Parameters, filter.SnapToken);
            return await ReadAttributeList(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(EntityAttributesFilter filter, IEnumerable<string> entitiesIds,
        CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            var ids = AsArray(entitiesIds);
            await using var command = _dataSource.CreateCommand(
                ids.Length == 1 ? _q.AttributesWithEntityIdsMultiAttributeOne : _q.AttributesWithEntityIdsMultiAttributeMany);
            AddString(command.Parameters, "@entity_type", filter.EntityType, 256);
            AddJsonArray(command.Parameters, "@attributes", filter.Attributes);
            AddOneOrMany(command.Parameters, "@entity_id", "@entity_ids", ids, 64);
            AddSnapToken(command.Parameters, filter.SnapToken);
            return await ReadAttributeDictionary(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<string>> GetEntityIdsExcluding(string entityType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(_q.EntityIdsExcluding);
            AddString(command.Parameters, "@entity_type", entityType, 256);
            AddSnapToken(command.Parameters, snapToken);
            AddJsonArray(command.Parameters, "@exclude_ids", excludeIds);
            return await ReadKeys(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<string>> GetSubjectIdsExcluding(string subjectType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(_q.SubjectIdsExcluding);
            AddString(command.Parameters, "@subject_type", subjectType, 256);
            AddSnapToken(command.Parameters, snapToken);
            AddJsonArray(command.Parameters, "@exclude_ids", excludeIds);
            return await ReadKeys(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }
}
