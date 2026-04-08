using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Data.SqlServer.Utils;
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
    private static string? _formattedSelect1Attribute;
    private static string? _formattedGetLatestSnapTokenQuery;
    private static string? _formattedSelectAttributes;
    private static string? _formattedSelectRelations;
    private static string? _formattedExistsRelation;
    private static string? _getRelationsWithSingleSubjectSnapSql;
    private static string? _getRelationsJoinedSql;
    private static readonly object Lock = new();

    public SqlServerDataReaderProvider(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        IValtuutusDbOptions dbOptions) : base(options)
    {
        _connectionFactory = connectionFactory;
        InitializeQueries(dbOptions);
    }

    private static void InitializeQueries(IValtuutusDbOptions dbOptions)
    {
        if (_formattedSelectAttributes == null || _formattedSelectRelations == null ||
            _formattedGetLatestSnapTokenQuery == null || _formattedSelect1Attribute == null)
        {
            lock (Lock)
            {
                if (_formattedSelectAttributes == null)
                {
                    _formattedSelectAttributes ??= string.Format(UnformattedSelectAttributes, dbOptions.Schema,
                        dbOptions.AttributesTableName);
                    _formattedSelectRelations ??= string.Format(UnformattedSelectRelations, dbOptions.Schema,
                        dbOptions.RelationsTableName);
                    _formattedGetLatestSnapTokenQuery ??= string.Format(
                        "SELECT TOP 1 id FROM [{0}].[{1}] ORDER BY created_at DESC",
                        dbOptions.Schema, dbOptions.TransactionsTableName);
                    _formattedExistsRelation ??= string.Format(UnformattedExistsRelation, dbOptions.Schema,
                        dbOptions.RelationsTableName);
                    _formattedSelect1Attribute ??= $"""
                                                        SELECT TOP 1
                                                        entity_type,
                                                        entity_id,
                                                        attribute,
                                                        value
                                                    FROM [{dbOptions.Schema}].[{dbOptions.AttributesTableName}] /**where**/
                                                    """;
                    var relationsTable = $"[{dbOptions.Schema}].[{dbOptions.RelationsTableName}]";
                    _getRelationsWithSingleSubjectSnapSql ??=
                        $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken) AND entity_type = @EntityType AND relation = @Relation AND subject_type = @SubjectType AND subject_id = @SubjectId";
                    _getRelationsJoinedSql ??= $"""
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
                        """;
                }
            }
        }
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
                .AddTemplate(_formattedSelect1Attribute);

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
                .AddTemplate(_formattedSelectAttributes!);

            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: cancellationToken)))
                .ToList();
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(_formattedSelectAttributes!);

            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: cancellationToken)))
                .ToDictionary(x => (x.Attribute, x.EntityId));
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
                .FilterAttributes(filter, entitiesIds)
                .AddTemplate(_formattedSelectAttributes!);

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
                .FilterAttributes(filter, entitiesIds)
                .AddTemplate(_formattedSelectAttributes!);

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
            var res = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(_formattedGetLatestSnapTokenQuery!, cancellationToken: cancellationToken));
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
                .AddTemplate(_formattedSelectRelations!);

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
                .AddTemplate(_formattedExistsRelation!);

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
                .AddTemplate(_formattedSelectRelations!);

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
                .AddTemplate(_formattedExistsRelation!);

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
                .FilterRelations(entityRelationFilter, subjectType, entityIds, subjectRelation)
                .AddTemplate(_formattedSelectRelations!);

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

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter, string[] subjectsIds, string subjectType, CancellationToken cancellationToken)
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
                    pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                        _getRelationsWithSingleSubjectSnapSql!,
                        new
                        {
                            EntityType = new DbString { Value = entityFilter.EntityType, Length = 256 },
                            Relation = new DbString { Value = entityFilter.Relation, Length = 64 },
                            SubjectType = new DbString { Value = subjectType, Length = 256 },
                            SubjectId = new DbString { Value = subjectsIds[0], Length = 64 },
                            SnapToken = new DbString { Value = entityFilter.SnapToken.Value, Length = 26, IsFixedLength = true }
                        },
                        cancellationToken: cancellationToken)));
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
                .FilterRelations(entityFilter, subjectsIds, subjectType)
                .AddTemplate(_formattedSelectRelations!);

            var multi = PooledList<RelationTuple>.Rent();
            try
            {
                multi.AddRange(await fallbackConnection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                        queryTemplate.Parameters, cancellationToken: cancellationToken)));
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
        string subjectType, string subjectId, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                    _getRelationsJoinedSql!,
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
}
