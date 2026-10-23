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
using static Valtuutus.Data.MySql.MySqlLookupReads;

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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.GetRelations(command.Parameters, _q, tupleFilter);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.HasDirectRelation(command.Parameters, _q, tupleFilter, subjectId);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.GetIndirectRelations(command.Parameters, _q, tupleFilter);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.HasAnyDirectRelation(command.Parameters, _q, entityType, entityIds,
                relation, subjectId, snapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.HasAnyOfDirectRelations(command.Parameters, _q, entityType,
                entityId, relationNames, subjectId, snapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.HasAnyOfAttributes(command.Parameters, _q, entityType, entityId,
                attributeNames, snapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = RelationsWithEntityIds(command.Parameters, _q, entityRelationFilter.EntityType,
                entityRelationFilter.Relation, subjectType, AsArray(entityIds), subjectRelation,
                entityRelationFilter.SnapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = RelationsWithSubjectIds(command.Parameters, _q, entityFilter.EntityType,
                entityFilter.Relation, subjectsIds, subjectType, subjectRelation, scope, entityFilter.SnapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = RelationsWithSubjectIdsMultiRelation(command.Parameters, _q, entityType,
                relationNames, subjectsIds, subjectType, subjectRelation, scope, snapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = RelationsWithEntityIdsMultiRelation(command.Parameters, _q, entityType,
                relationNames, subjectType, AsArray(entityIds), subjectRelation, snapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = RelationsJoined(command.Parameters, _q, mainFilter.EntityType, mainFilter.Relation,
                subEntityType, subjectRelation, subRelation, subjectType, subjectId, scope, mainFilter.SnapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = RelationsJoinedByEntityIds(command.Parameters, _q, mainFilter.EntityType,
                mainFilter.Relation, AsArray(entityIds), subEntityType, subjectRelation, subRelation, subjectType,
                mainFilter.SnapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.HasTupleToUserSetRelation(command.Parameters, _q, entityType,
                entityId, tupleSetRelation, subEntityType, computedRelation, subjectType, subjectId, snapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.HasUsersetJoinRelation(command.Parameters, _q, entityType, entityId,
                relation, subEntityType, computedRelation, subjectType, subjectId, snapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.HasFusedExpression(command.Parameters, _q, entityType, entityId,
                leaves, requireAll, subjectType, subjectId, snapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.HasTrueBoolAttribute(command.Parameters, _q, entityType, entityId,
                attribute, snapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = AttributesByAttribute(command.Parameters, _q, filter.EntityType, filter.Attribute,
                filter.EntityId, filter.SnapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = AttributesByAttributes(command.Parameters, _q, filter.EntityType, filter.Attributes,
                filter.EntityId, scope, filter.SnapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = MySqlCheckReads.AttributesSingleEntity(command.Parameters, _q, filter.EntityType,
                filter.EntityId ?? throw new ArgumentException("EntityId must be set.", nameof(filter)),
                filter.Attributes, filter.SnapToken);

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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = AttributeWithEntityIds(command.Parameters, _q, filter.EntityType, filter.Attribute,
                AsArray(entitiesIds), filter.SnapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = AttributesWithEntityIds(command.Parameters, _q, filter.EntityType,
                filter.Attributes, AsArray(entitiesIds), filter.SnapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = EntityIdsExcluding(command.Parameters, _q, entityType, excludeIds, snapToken);
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
            await using var command = _dataSource.CreateCommand();
            command.CommandText = SubjectIdsExcluding(command.Parameters, _q, subjectType, excludeIds, snapToken);
            return await ReadKeys(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }
}
