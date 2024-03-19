using System.Diagnostics;
using System.Text.Json;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Data.Configuration;
using Valtuutus.Data.Postgres.Utils;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Valtuutus.Data.Postgres;

internal sealed class PostgresDataReaderProvider(DbConnectionFactory connectionFactory, ILogger<IDataReaderProvider> logger)
    : IDataReaderProvider
{
    public async Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterRelations(tupleFilter)
            .AddTemplate(@"SELECT 
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples /**where**/");

#if DEBUG
        logger.LogDebug("Querying relations tuples with filter: {filter}", JsonSerializer.Serialize(tupleFilter));
        var start = Stopwatch.GetTimestamp();
#endif
        var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
#if DEBUG
        logger.LogDebug("Queried relations in {QueryDuration}ms, returned {QueryItemCount} items", Stopwatch.GetElapsedTime(start).TotalMilliseconds, res.Count);
#endif
        return res;
    }
    
    public async Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entitiesIds, string? subjectRelation, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterRelations(entityRelationFilter, subjectType, entitiesIds, subjectRelation)
            .AddTemplate(@"SELECT 
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples /**where**/");

#if DEBUG
        logger.LogDebug("Querying relations tuples with filter {sql}, with params: {params}", queryTemplate.RawSql, JsonSerializer.Serialize(new {entityRelationFilter, subjectType, entitiesIds, subjectRelation}));
        var start = Stopwatch.GetTimestamp();
#endif
        var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
        
        
#if DEBUG
        logger.LogDebug("Queried relations in {QueryDuration}ms, returned {QueryItemCount} items", Stopwatch.GetElapsedTime(start).TotalMilliseconds, res.Count);
#endif
        return res;
    }
    
    public async Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityFilter, IList<string> subjectsIds, string subjectType, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterRelations(entityFilter, subjectsIds, subjectType)
            .AddTemplate(@"SELECT 
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples /**where**/");

#if DEBUG
        logger.LogDebug("Querying relations tuples with filter {sql}, with params: {params}", queryTemplate.RawSql, JsonSerializer.Serialize(new {entityFilter, subjectsIds}));
        var start = Stopwatch.GetTimestamp();
#endif
        
        var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
        
#if DEBUG
        logger.LogDebug("Queried relations in {QueryDuration}ms, returned {QueryItemCount} items", Stopwatch.GetElapsedTime(start).TotalMilliseconds, res.Count);
#endif

        return res;
    }
    
    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterAttributes(filter)
            .AddTemplate(@"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM attributes /**where**/
                LIMIT 1");

        logger.LogDebug("Querying attributes tuples with filter: {filter}", filter);

        return await connection.QuerySingleOrDefaultAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
            queryTemplate.Parameters, cancellationToken: ct));
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterAttributes(filter)
            .AddTemplate(@"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM attributes /**where**/");

        logger.LogDebug("Querying attributes tuples with filter: {filter}", filter);

        return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
    }

    public async Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterAttributes(filter, entitiesIds)
            .AddTemplate(@"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM attributes /**where**/");

        logger.LogDebug("Querying attributes tuples with filter: {filter}", filter);

        return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
    }
}