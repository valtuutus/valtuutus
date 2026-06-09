using System.Collections.Concurrent;
using System.Data;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Data.SqlServer.Utils;
using Valtuutus.Data.Db;
using Dapper;
using Microsoft.Data.SqlClient;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer;

internal sealed class SqlServerDataReaderProvider : RateLimiterExecuter, IDataReaderProvider
{
    private const string UnformattedSelectAttributes = @"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM [{0}].[{1}] /**where**/";

    private const string UnformattedSelectRelations = @"SELECT
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id,
                    subject_relation
                FROM [{0}].[{1}] /**where**/";

    private const string UnformattedExistsRelation = "SELECT CASE WHEN EXISTS(SELECT 1 FROM [{0}].[{1}] /**where**/) THEN 1 ELSE 0 END";

    private readonly DbConnectionFactory _connectionFactory;

    private static readonly ConcurrentDictionary<DbQueryCacheKey, ReaderQueries> QueryCache = new();
    private readonly ReaderQueries _q;

    public SqlServerDataReaderProvider(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        IValtuutusDbOptions dbOptions) : base(options)
    {
        _connectionFactory = connectionFactory;
        _q = QueryCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static key => BuildQueries(key));
    }

    private static ReaderQueries BuildQueries(DbQueryCacheKey key)
    {
        var relationsTable = $"[{key.Schema}].[{key.RelationsTable}]";
        var attributesTable = $"[{key.Schema}].[{key.AttributesTable}]";
        const string snapPredicate = "created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)";

        return new ReaderQueries
        {
            TvpListIdsTypeName = SqlBuilderExtensions.FormatTvpListIdsName(key.Schema),
            SelectAttributes = string.Format(UnformattedSelectAttributes, key.Schema, key.AttributesTable),
            SelectRelations = string.Format(UnformattedSelectRelations, key.Schema, key.RelationsTable),
            GetLatestSnapToken = string.Format(
                "SELECT TOP 1 id FROM [{0}].[{1}] ORDER BY created_at DESC", key.Schema, key.TransactionsTable),
            ExistsRelation = string.Format(UnformattedExistsRelation, key.Schema, key.RelationsTable),
            Select1Attribute = $"""
                                    SELECT TOP 1
                                    entity_type,
                                    entity_id,
                                    attribute,
                                    value
                                FROM {attributesTable} /**where**/
                                """,
            GetRelationsWithSingleSubjectSnap =
                $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken) AND entity_type = @EntityType AND relation = @Relation AND subject_type = @SubjectType AND subject_id = @SubjectId",
            GetRelationsJoined = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} AS r_main
                WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                  AND r_main.entity_type = @EntityType
                  AND r_main.relation = @Relation
                  AND r_main.subject_type = @SubEntityType
                  AND r_main.subject_id IN (
                      SELECT entity_id FROM {relationsTable}
                      WHERE created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)
                        AND entity_type = @SubEntityType
                        AND relation = @SubRelation
                        AND subject_type = @SubjectType
                        AND subject_id = @SubjectId
                  )
                """,
            HasTupleToUserSetRelation = $"""
                SELECT CASE WHEN EXISTS(
                    SELECT 1 FROM {relationsTable} r_main
                    INNER JOIN {relationsTable} r_dep
                        ON r_dep.entity_type = r_main.subject_type AND r_dep.entity_id = r_main.subject_id
                    WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                      AND r_main.entity_type = @EntityType
                      AND r_main.entity_id = @EntityId
                      AND r_main.relation = @TupleSetRelation
                      AND r_main.subject_relation = ''
                      AND r_dep.created_tx_id <= @SnapToken AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > @SnapToken)
                      AND r_dep.relation = @ComputedRelation
                      AND r_dep.subject_type = @SubjectType
                      AND r_dep.subject_id = @SubjectId
                      AND r_dep.subject_relation = ''
                ) THEN 1 ELSE 0 END
                """,
            GetRelationsWithSingleSubjectScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @ScopeRelation
                    AND r_scope.subject_type = @ScopeSubjectType
                    AND r_scope.subject_id = @ScopeSubjectId
                    AND r_scope.created_tx_id <= @SnapToken AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @SnapToken)
                WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                  AND r_main.entity_type = @EntityType
                  AND r_main.relation = @Relation
                  AND r_main.subject_type = @SubjectType
                  AND r_main.subject_id = @SubjectId
                """,
            GetRelationsJoinedScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} AS r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @ScopeRelation
                    AND r_scope.subject_type = @ScopeSubjectType
                    AND r_scope.subject_id = @ScopeSubjectId
                    AND r_scope.created_tx_id <= @SnapToken AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @SnapToken)
                WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                  AND r_main.entity_type = @EntityType
                  AND r_main.relation = @Relation
                  AND r_main.subject_type = @SubEntityType
                  AND r_main.subject_id IN (
                      SELECT entity_id FROM {relationsTable}
                      WHERE created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)
                        AND entity_type = @SubEntityType
                        AND relation = @SubRelation
                        AND subject_type = @SubjectType
                        AND subject_id = @SubjectId
                  )
                """,
            GetEntityIdsExcluding = $"""
                SELECT DISTINCT entity_id
                FROM (
                    SELECT entity_id FROM {relationsTable}
                    WHERE {snapPredicate} AND entity_type = @EntityType
                    UNION
                    SELECT entity_id FROM {attributesTable}
                    WHERE {snapPredicate} AND entity_type = @EntityType
                ) universe
                WHERE entity_id NOT IN (SELECT id FROM @ExcludeIds)
                """,
            GetSubjectIdsExcluding = $"""
                SELECT DISTINCT subject_id
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND subject_type = @SubjectType
                  AND subject_relation = ''
                  AND subject_id NOT IN (SELECT id FROM @ExcludeIds)
                """,
            GetAttributesDictScoped = $"""
                SELECT a.entity_type, a.entity_id, a.attribute, a.value
                FROM {attributesTable} a
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = a.entity_type
                    AND r_scope.entity_id = a.entity_id
                    AND r_scope.relation = @ScopeRelation
                    AND r_scope.subject_type = @ScopeSubjectType
                    AND r_scope.subject_id = @ScopeSubjectId
                    AND r_scope.created_tx_id <= @SnapToken AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @SnapToken)
                WHERE a.created_tx_id <= @SnapToken AND (a.deleted_tx_id IS NULL OR a.deleted_tx_id > @SnapToken)
                  AND a.entity_type = @EntityType
                  AND a.attribute IN @Attributes
                """,
        };
    }

    private sealed record ReaderQueries
    {
        public required string TvpListIdsTypeName { get; init; }
        public required string SelectAttributes { get; init; }
        public required string SelectRelations { get; init; }
        public required string GetLatestSnapToken { get; init; }
        public required string ExistsRelation { get; init; }
        public required string Select1Attribute { get; init; }
        public required string GetRelationsWithSingleSubjectSnap { get; init; }
        public required string GetRelationsJoined { get; init; }
        public required string HasTupleToUserSetRelation { get; init; }
        public required string GetRelationsWithSingleSubjectScoped { get; init; }
        public required string GetRelationsJoinedScoped { get; init; }
        public required string GetAttributesDictScoped { get; init; }
        public required string GetEntityIdsExcluding { get; init; }
        public required string GetSubjectIdsExcluding { get; init; }
    }

    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(_q.Select1Attribute);

            return await connection.QuerySingleOrDefaultAsync<AttributeTuple>(new CommandDefinition(
                queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: cancellationToken));
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(_q.SelectAttributes);

            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: cancellationToken)))
                .ToList();
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            if (scope is { } s)
            {
                return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(
                        _q.GetAttributesDictScoped,
                        new
                        {
                            EntityType = new DbString { Value = filter.EntityType, Length = 256 },
                            Attributes = filter.Attributes,
                            SnapToken = new DbString { Value = filter.SnapToken.Value, Length = 26, IsFixedLength = true },
                            ScopeRelation = new DbString { Value = s.Relation, Length = 64 },
                            ScopeSubjectType = new DbString { Value = s.SubjectType, Length = 256 },
                            ScopeSubjectId = new DbString { Value = s.SubjectId, Length = 64 },
                        },
                        cancellationToken: cancellationToken)))
                    .ToDictionary(x => (x.Attribute, x.EntityId));
            }

            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter, _q.TvpListIdsTypeName)
                .AddTemplate(_q.SelectAttributes);

            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: cancellationToken)))
                .ToDictionary(x => (x.Attribute, x.EntityId));
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<AttributeTuple>> GetAttributesSingleEntity(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter, _q.TvpListIdsTypeName)
                .AddTemplate(_q.SelectAttributes);

            var pooled = PooledList<AttributeTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: cancellationToken)));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter, entitiesIds, _q.TvpListIdsTypeName)
                .AddTemplate(_q.SelectAttributes);

            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: cancellationToken)))
                .ToList();
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(EntityAttributesFilter filter, IEnumerable<string> entitiesIds,
        CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter, entitiesIds, _q.TvpListIdsTypeName)
                .AddTemplate(_q.SelectAttributes);

            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: cancellationToken)))
                .ToDictionary(x => (x.Attribute, x.EntityId));
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var res = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(_q.GetLatestSnapToken, cancellationToken: cancellationToken));
            return res != null ? new SnapToken(res) : (SnapToken?)null;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterRelations(tupleFilter)
                .AddTemplate(_q.SelectRelations);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                        queryTemplate.Parameters, cancellationToken: cancellationToken)));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterDirectRelation(tupleFilter, subjectId)
                .AddTemplate(_q.ExistsRelation);

            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: cancellationToken)) == 1;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterIndirectRelations(tupleFilter)
                .AddTemplate(_q.SelectRelations);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                        queryTemplate.Parameters, cancellationToken: cancellationToken)));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterDirectRelationBatch(snapToken, entityType, entityIds, relation, subjectId)
                .AddTemplate(_q.ExistsRelation);

            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: cancellationToken)) == 1;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterRelations(entityRelationFilter, subjectType, entityIds, subjectRelation, _q.TvpListIdsTypeName)
                .AddTemplate(_q.SelectRelations);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                        queryTemplate.Parameters, cancellationToken: cancellationToken)));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter, string[] subjectsIds, string subjectType, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            if (subjectsIds.Length == 1)
            {
                await using var connection = (SqlConnection)_connectionFactory();
                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    if (scope is { } s)
                    {
                        pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                            _q.GetRelationsWithSingleSubjectScoped,
                            new
                            {
                                EntityType = new DbString { Value = entityFilter.EntityType, Length = 256 },
                                Relation = new DbString { Value = entityFilter.Relation, Length = 64 },
                                SubjectType = new DbString { Value = subjectType, Length = 256 },
                                SubjectId = new DbString { Value = subjectsIds[0], Length = 64 },
                                SnapToken = new DbString { Value = entityFilter.SnapToken.Value, Length = 26, IsFixedLength = true },
                                ScopeRelation = new DbString { Value = s.Relation, Length = 64 },
                                ScopeSubjectType = new DbString { Value = s.SubjectType, Length = 256 },
                                ScopeSubjectId = new DbString { Value = s.SubjectId, Length = 64 },
                            },
                            cancellationToken: cancellationToken)));
                    }
                    else
                    {
                        pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                            _q.GetRelationsWithSingleSubjectSnap,
                            new
                            {
                                EntityType = new DbString { Value = entityFilter.EntityType, Length = 256 },
                                Relation = new DbString { Value = entityFilter.Relation, Length = 64 },
                                SubjectType = new DbString { Value = subjectType, Length = 256 },
                                SubjectId = new DbString { Value = subjectsIds[0], Length = 64 },
                                SnapToken = new DbString { Value = entityFilter.SnapToken.Value, Length = 26, IsFixedLength = true }
                            },
                            cancellationToken: cancellationToken)));
                    }
                    return pooled;
                }
                catch
                {
                    pooled.Dispose();
                    throw;
                }
            }

            await using var fallbackConnection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterRelations(entityFilter, subjectsIds, subjectType, _q.TvpListIdsTypeName)
                .AddTemplate(_q.SelectRelations);

            var multi = PooledList<RelationTuple>.Rent();
            try
            {
                multi.AddRange(await fallbackConnection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                        queryTemplate.Parameters, cancellationToken: cancellationToken)));

                if (scope is { } ms)
                {
                    // For multi-subject scoped case, filter results in-memory by fetching which entity IDs
                    // belong to the scope. SqlBuilder-based path does not support JOIN, so we post-filter.
                    await using var scopeConnection = (SqlConnection)_connectionFactory();
                    var scopeQueryTemplate = new SqlBuilder()
                        .FilterRelations(
                            new EntityRelationFilter
                            {
                                EntityType = entityFilter.EntityType,
                                Relation = ms.Relation,
                                SnapToken = entityFilter.SnapToken
                            },
                            new[] { ms.SubjectId },
                            ms.SubjectType,
                            _q.TvpListIdsTypeName)
                        .AddTemplate(_q.SelectRelations);
                    var scopeRelations = (await scopeConnection.QueryAsync<RelationTuple>(new CommandDefinition(
                            scopeQueryTemplate.RawSql,
                            scopeQueryTemplate.Parameters,
                            cancellationToken: cancellationToken)))
                        .Select(r => r.EntityId)
                        .ToHashSet();

                    var filtered = PooledList<RelationTuple>.Rent();
                    try
                    {
                        foreach (var tuple in multi)
                        {
                            if (scopeRelations.Contains(tuple.EntityId))
                                filtered.Add(tuple);
                        }
                    }
                    catch
                    {
                        filtered.Dispose();
                        throw;
                    }
                    multi.Dispose();
                    return filtered;
                }

                return multi;
            }
            catch
            {
                multi.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoined(
        EntityRelationFilter mainFilter, string subEntityType, string subRelation,
        string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                if (scope is { } s)
                {
                    pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                        _q.GetRelationsJoinedScoped,
                        new
                        {
                            EntityType = new DbString { Value = mainFilter.EntityType, Length = 256 },
                            Relation = new DbString { Value = mainFilter.Relation, Length = 64 },
                            SubEntityType = new DbString { Value = subEntityType, Length = 256 },
                            SubRelation = new DbString { Value = subRelation, Length = 64 },
                            SubjectType = new DbString { Value = subjectType, Length = 256 },
                            SubjectId = new DbString { Value = subjectId, Length = 64 },
                            SnapToken = new DbString { Value = mainFilter.SnapToken.Value, Length = 26, IsFixedLength = true },
                            ScopeRelation = new DbString { Value = s.Relation, Length = 64 },
                            ScopeSubjectType = new DbString { Value = s.SubjectType, Length = 256 },
                            ScopeSubjectId = new DbString { Value = s.SubjectId, Length = 64 },
                        },
                        cancellationToken: cancellationToken)));
                }
                else
                {
                    pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                        _q.GetRelationsJoined,
                        new
                        {
                            EntityType = new DbString { Value = mainFilter.EntityType, Length = 256 },
                            Relation = new DbString { Value = mainFilter.Relation, Length = 64 },
                            SubEntityType = new DbString { Value = subEntityType, Length = 256 },
                            SubRelation = new DbString { Value = subRelation, Length = 64 },
                            SubjectType = new DbString { Value = subjectType, Length = 256 },
                            SubjectId = new DbString { Value = subjectId, Length = 64 },
                            SnapToken = new DbString { Value = mainFilter.SnapToken.Value, Length = 26, IsFixedLength = true }
                        },
                        cancellationToken: cancellationToken)));
                }
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
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
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var result = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                _q.HasTupleToUserSetRelation,
                new
                {
                    EntityType = new DbString { Value = entityType, Length = 256 },
                    EntityId = new DbString { Value = entityId, Length = 64 },
                    TupleSetRelation = new DbString { Value = tupleSetRelation, Length = 64 },
                    ComputedRelation = new DbString { Value = computedRelation, Length = 64 },
                    SubjectType = new DbString { Value = subjectType, Length = 256 },
                    SubjectId = new DbString { Value = subjectId, Length = 64 },
                    SnapToken = new DbString { Value = snapToken.Value, Length = 26, IsFixedLength = true }
                },
                cancellationToken: cancellationToken));
            return result == 1;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<string>> GetEntityIdsExcluding(string entityType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            var excludeIdsParam = TvpHelper.AsTvpParameter(excludeIds, _q.TvpListIdsTypeName);
            var rows = await connection.QueryAsync<string>(new CommandDefinition(
                _q.GetEntityIdsExcluding,
                new
                {
                    EntityType = new DbString { Value = entityType, Length = 256 },
                    SnapToken = new DbString { Value = snapToken.Value, Length = 26, IsFixedLength = true },
                    ExcludeIds = excludeIdsParam
                },
                cancellationToken: cancellationToken));
            return rows is List<string> list ? list : rows.ToList();
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<string>> GetSubjectIdsExcluding(string subjectType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            var excludeIdsParam = TvpHelper.AsTvpParameter(excludeIds, _q.TvpListIdsTypeName);
            var rows = await connection.QueryAsync<string>(new CommandDefinition(
                _q.GetSubjectIdsExcluding,
                new
                {
                    SubjectType = new DbString { Value = subjectType, Length = 256 },
                    SnapToken = new DbString { Value = snapToken.Value, Length = 26, IsFixedLength = true },
                    ExcludeIds = excludeIdsParam
                },
                cancellationToken: cancellationToken));
            return rows is List<string> list ? list : rows.ToList();
        }
        finally
        {
            Semaphore.Release();
        }
    }
}
