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

    private static RelationTuple ReadRelationTuple(DbDataReader reader, KeyDecoder keys) =>
        new(keys.Read(reader, 0), keys.Read(reader, 1), keys.Read(reader, 2),
            keys.Read(reader, 3), keys.Read(reader, 4), keys.Read(reader, 5));

    private static AttributeTuple ReadAttributeTuple(DbDataReader reader, KeyDecoder keys) =>
        new(keys.Read(reader, 0), keys.Read(reader, 1), keys.Read(reader, 2),
            keys.ParseValue(reader.GetString(3)));

    // EXISTS and boolean combinations of it come back as integers.
    private static async Task<bool> ExecuteBoolean(DbCommand command, CancellationToken cancellationToken) =>
        Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));

    private static async Task<PooledList<RelationTuple>> ReadRelations(DbCommand command, CancellationToken cancellationToken)
    {
        var pooled = PooledList<RelationTuple>.Rent();
        var keys = KeyDecoder.Rent();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                pooled.Add(ReadRelationTuple(reader, keys));
            return pooled;
        }
        catch
        {
            pooled.Dispose();
            throw;
        }
        finally
        {
            KeyDecoder.Return(keys);
        }
    }

    // capacity is the expected row count; a read for given entities expects one row per entity, up
    // to a bound, so a read whose entities mostly have no row doesn't allocate for all of them.
    private static async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> ReadAttributeDictionary(
        DbCommand command, int capacity, CancellationToken cancellationToken)
    {
        var dict = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>(capacity);
        var keys = KeyDecoder.Rent();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var tuple = ReadAttributeTuple(reader, keys);
                dict[(tuple.Attribute, tuple.EntityId)] = tuple;
            }
            return dict;
        }
        finally
        {
            KeyDecoder.Return(keys);
        }
    }

    private static async Task<List<AttributeTuple>> ReadAttributeList(DbCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<AttributeTuple>();
        var keys = KeyDecoder.Rent();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(ReadAttributeTuple(reader, keys));
            return rows;
        }
        finally
        {
            KeyDecoder.Return(keys);
        }
    }

    private static async Task<List<string>> ReadKeys(DbCommand command, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var keys = KeyDecoder.Rent();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(keys.Read(reader, 0));
            return result;
        }
        finally
        {
            KeyDecoder.Return(keys);
        }
    }

    private static async Task<HashSet<string>> ReadKeySet(DbCommand command, int capacity, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(capacity, StringComparer.Ordinal);
        var keys = KeyDecoder.Rent();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(keys.Read(reader, 0));
            return result;
        }
        finally
        {
            KeyDecoder.Return(keys);
        }
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartRoundTrip(_dbSystem);
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _dataSource.CreateCommand(_q.GetLatestSnapToken.Sql);
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
            MySqlCheckReads.GetRelations(command.Parameters, _q, tupleFilter).ApplyTo(command);
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
            MySqlCheckReads.HasDirectRelation(command.Parameters, _q, tupleFilter, subjectId).ApplyTo(command);
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
            MySqlCheckReads.GetIndirectRelations(command.Parameters, _q, tupleFilter).ApplyTo(command);
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
            MySqlCheckReads.HasAnyDirectRelation(command.Parameters, _q, entityType, entityIds,
                relation, subjectId, snapToken).ApplyTo(command);
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
            MySqlCheckReads.HasAnyOfDirectRelations(command.Parameters, _q, entityType,
                entityId, relationNames, subjectId, snapToken).ApplyTo(command);
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
            MySqlCheckReads.HasAnyOfAttributes(command.Parameters, _q, entityType, entityId,
                attributeNames, snapToken).ApplyTo(command);
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
            RelationsWithEntityIds(command.Parameters, _q, entityRelationFilter.EntityType,
                entityRelationFilter.Relation, subjectType, AsCollection(entityIds), subjectRelation,
                entityRelationFilter.SnapToken).ApplyTo(command);
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
            RelationsWithSubjectIds(command.Parameters, _q, entityFilter.EntityType, entityFilter.Relation,
                subjectsIds, subjectType, subjectRelation, scope, entityFilter.SnapToken).ApplyTo(command);
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
            RelationsWithSubjectIdsMultiRelation(command.Parameters, _q, entityType,
                relationNames, subjectsIds, subjectType, subjectRelation, scope, snapToken).ApplyTo(command);
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
            RelationsWithEntityIdsMultiRelation(command.Parameters, _q, entityType,
                relationNames, subjectType, AsCollection(entityIds), subjectRelation, snapToken).ApplyTo(command);
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
            RelationsJoined(command.Parameters, _q, mainFilter.EntityType, mainFilter.Relation, subEntityType,
                subjectRelation, subRelation, subjectType, subjectId, scope, mainFilter.SnapToken).ApplyTo(command);
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
            RelationsJoinedByEntityIds(command.Parameters, _q, mainFilter.EntityType,
                mainFilter.Relation, AsCollection(entityIds), subEntityType, subjectRelation, subRelation, subjectType,
                mainFilter.SnapToken).ApplyTo(command);
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
            MySqlCheckReads.HasTupleToUserSetRelation(command.Parameters, _q, entityType, entityId,
                tupleSetRelation, subEntityType, computedRelation, subjectType, subjectId, snapToken).ApplyTo(command);
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
            MySqlCheckReads.HasUsersetJoinRelation(command.Parameters, _q, entityType, entityId,
                relation, subEntityType, computedRelation, subjectType, subjectId, snapToken).ApplyTo(command);
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
            MySqlCheckReads.HasFusedExpression(command.Parameters, _q, entityType, entityId,
                leaves, requireAll, subjectType, subjectId, snapToken).ApplyTo(command);
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
            MySqlCheckReads.HasTrueBoolAttribute(command.Parameters, _q, entityType, entityId,
                attribute, snapToken).ApplyTo(command);
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
            AttributesByAttribute(command.Parameters, _q, filter.EntityType, filter.Attribute,
                filter.EntityId, filter.SnapToken).ApplyTo(command);
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
            AttributesByAttributes(command.Parameters, _q, filter.EntityType, filter.Attributes,
                filter.EntityId, scope, filter.SnapToken).ApplyTo(command);
            return await ReadAttributeDictionary(command, 0, cancellationToken);
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
            MySqlCheckReads.AttributesSingleEntity(command.Parameters, _q, filter.EntityType,
                filter.EntityId ?? throw new ArgumentException("EntityId must be set.", nameof(filter)),
                filter.Attributes, filter.SnapToken).ApplyTo(command);

            var pooled = PooledList<AttributeTuple>.Rent();
            var keys = KeyDecoder.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    pooled.Add(ReadAttributeTuple(reader, keys));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
            finally
            {
                KeyDecoder.Return(keys);
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
            AttributeWithEntityIds(command.Parameters, _q, filter.EntityType, filter.Attribute,
                AsCollection(entitiesIds), filter.SnapToken).ApplyTo(command);
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
            var ids = AsCollection(entitiesIds);
            AttributesWithEntityIds(command.Parameters, _q, filter.EntityType,
                filter.Attributes, ids, filter.SnapToken).ApplyTo(command);
            return await ReadAttributeDictionary(command, Math.Min(ids.Count, 1024), cancellationToken);
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
            EntityIdsExcluding(command.Parameters, _q, entityType, excludeIds, snapToken).ApplyTo(command);
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
            SubjectIdsExcluding(command.Parameters, _q, subjectType, excludeIds, snapToken).ApplyTo(command);
            return await ReadKeys(command, cancellationToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }
}
