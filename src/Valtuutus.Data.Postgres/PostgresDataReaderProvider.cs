using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Data.Postgres.Utils;
using Dapper;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

internal sealed class PostgresDataReaderProvider : RateLimiterExecuter, IDataReaderProvider
{
    private const string UnformattedSelectAttributes = @"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM {0}.{1} /**where**/";

    private const string UnformattedSelectRelations = @"SELECT
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id,
                    subject_relation
                FROM {0}.{1} /**where**/";

    private const string UnformattedExistsRelation = "SELECT EXISTS(SELECT 1 FROM {0}.{1} /**where**/)";

    private readonly DbConnectionFactory _connectionFactory;
    private static string? _formattedSelect1Attribute;
    private static string? _formattedGetLatestSnapTokenQuery;
    private static string? _formattedSelectAttributes;
    private static string? _formattedSelectRelations;
    private static string? _formattedExistsRelation;
    private static readonly object Lock = new();


    public PostgresDataReaderProvider(DbConnectionFactory connectionFactory,
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
                        "SELECT id FROM {0}.{1} ORDER BY created_at DESC LIMIT 1",
                        dbOptions.Schema, dbOptions.TransactionsTableName);
                    _formattedSelect1Attribute ??= $"{_formattedSelectAttributes!} LIMIT 1";
                    _formattedExistsRelation ??= string.Format(UnformattedExistsRelation, dbOptions.Schema,
                        dbOptions.RelationsTableName);
                }
            }
        }
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var connection = _connectionFactory();
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
            using var connection = _connectionFactory();
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
            using var connection = _connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterDirectRelation(tupleFilter, subjectId)
                .AddTemplate(_formattedExistsRelation!);

            return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: cancellationToken));
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
            using var connection = _connectionFactory();
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

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var connection = _connectionFactory();
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

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter, IList<string> subjectsIds, string subjectType, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var connection = _connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterRelations(entityFilter, subjectsIds, subjectType)
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

    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var connection = _connectionFactory();
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
            using var connection = _connectionFactory();
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
            using var connection = _connectionFactory();
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
            using var connection = _connectionFactory();
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
            using var connection = _connectionFactory();
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
}
