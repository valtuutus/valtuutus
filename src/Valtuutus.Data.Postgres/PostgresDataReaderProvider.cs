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
                FROM attributes /**where**/";

    private const string SelectRelations = @"SELECT 
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples /**where**/";

    private readonly DbConnectionFactory _connectionFactory;

    public PostgresDataReaderProvider(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options) : base(options)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var query = @"SELECT id FROM transactions ORDER BY created_at DESC LIMIT 1";

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
                .AddTemplate(SelectRelations);

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
                .AddTemplate(SelectRelations);
            
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
                .AddTemplate(SelectRelations);

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
                .AddTemplate($@"{SelectAttributes}
                LIMIT 1");
            
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
                .AddTemplate(SelectAttributes);
            
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
                .AddTemplate(SelectAttributes);
            
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
                .AddTemplate(SelectAttributes);
            
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
                .AddTemplate(SelectAttributes);
            
            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToDictionary(x => (x.Attribute, x.EntityId));
        }, cancellationToken);
    }
}