using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Data.Postgres.Utils;
using Dapper;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

internal sealed class PostgresDataReaderProvider : RateLimiterExecuter, IDataReaderProvider
{
    private const string SelectAttributes = @"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM {0}.{1} /**where**/";

    private const string SelectRelations = @"SELECT 
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM {0}.{1} /**where**/";

    private readonly DbConnectionFactory _connectionFactory;
    private readonly IValtuutusDbOptions _dbOptions;

    public PostgresDataReaderProvider(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        IValtuutusDbOptions dbOptions) : base(options)
    {
        _connectionFactory = connectionFactory;
        _dbOptions = dbOptions;
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var query = $"SELECT id FROM {_dbOptions.Schema}.{_dbOptions.TransactionsTableName} ORDER BY created_at DESC LIMIT 1";

            var res = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(query, cancellationToken: ct));
            return res != null ? new SnapToken(res) : (SnapToken?)null;
        }, cancellationToken);
    }

    public async Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var queryTemplate = new SqlBuilder()
                .FilterRelations(tupleFilter)
                .AddTemplate(string.Format(SelectRelations, _dbOptions.Schema, _dbOptions.RelationsTableName));

            var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToList();
            return res;
        }, cancellationToken);
    }
    
    public async Task<List<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();


        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var queryTemplate = new SqlBuilder()
                .FilterRelations(entityRelationFilter, subjectType, entityIds, subjectRelation)
                .AddTemplate(string.Format(SelectRelations, _dbOptions.Schema, _dbOptions.RelationsTableName));
            
            var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToList();
            
            return res;
        }, cancellationToken);
    }
    
    public async Task<List<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter, IList<string> subjectsIds, string subjectType, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var queryTemplate = new SqlBuilder()
                .FilterRelations(entityFilter, subjectsIds, subjectType)
                .AddTemplate(string.Format(SelectRelations, _dbOptions.Schema, _dbOptions.RelationsTableName));

            var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToList();
            
            return res;
        }, cancellationToken);
    }
    
    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(string.Format(SelectAttributes, _dbOptions.Schema, _dbOptions.AttributesTableName));
            
            return await connection.QuerySingleOrDefaultAsync<AttributeTuple>(new CommandDefinition(
                queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: ct));
        }, cancellationToken);

    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(string.Format(SelectAttributes, _dbOptions.Schema, _dbOptions.AttributesTableName));
            
            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToList();
        }, cancellationToken);

    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(string.Format(SelectAttributes, _dbOptions.Schema, _dbOptions.AttributesTableName));
            
            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToDictionary(x => (x.Attribute, x.EntityId));
        }, cancellationToken);
    }

    public async Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter, entitiesIds)
                .AddTemplate(string.Format(SelectAttributes, _dbOptions.Schema, _dbOptions.AttributesTableName));
            
            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToList();
        }, cancellationToken);
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(EntityAttributesFilter filter, IEnumerable<string> entitiesIds,
        CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter, entitiesIds)
                .AddTemplate(string.Format(SelectAttributes, _dbOptions.Schema, _dbOptions.AttributesTableName));
            
            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToDictionary(x => (x.Attribute, x.EntityId));
        }, cancellationToken);
    }
}