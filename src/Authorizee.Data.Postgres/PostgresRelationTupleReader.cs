using System.Diagnostics;
using System.Text.Json;
using Authorizee.Core;
using Authorizee.Core.Data;
using Authorizee.Core.Observability;
using Authorizee.Data.Configuration;
using Authorizee.Data.Postgres.Utils;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Authorizee.Data.Postgres;

public class PostgresRelationTupleReader(DbConnectionFactory connectionFactory, ILogger<PostgresRelationTupleReader> logger)
    : IRelationTupleReader
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
        logger.LogDebug("Queried relations in {}ms", Stopwatch.GetElapsedTime(start));
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
        logger.LogDebug("Queried relations in {}ms, returned {} items", Stopwatch.GetElapsedTime(start), res.Count);
#endif
        return res;
    }
    
    public async Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityFilter, IEnumerable<SubjectFilter> subjectsFilter, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterRelations(entityFilter, subjectsFilter)
            .AddTemplate(@"SELECT 
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples /**where**/");

#if DEBUG
        logger.LogDebug("Querying relations tuples with filter {sql}, with params: {params}", queryTemplate.RawSql, JsonSerializer.Serialize(new {entityFilter, subjectsFilter}));
        var start = Stopwatch.GetTimestamp();
#endif
        
        var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
        
#if DEBUG
        logger.LogDebug("Queried relations in {}ms, returned {} items", Stopwatch.GetElapsedTime(start), res.Count);
#endif

        return res;
    }
}