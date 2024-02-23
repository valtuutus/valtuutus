using System.Diagnostics;
using System.Text.Json;
using Authorizee.Core;
using Authorizee.Core.Data;
using Authorizee.Core.Observability;
using Authorizee.Data.Configuration;
using Authorizee.Data.Utils;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Authorizee.Data;

public class RelationTupleReader(DbConnectionFactory connectionFactory, ILogger<RelationTupleReader> logger)
    : IRelationTupleReader
{
    public async Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter)
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

        logger.LogDebug("Querying relations tuples with filter: {filter}", JsonSerializer.Serialize(tupleFilter));

        var stopwatch = new Stopwatch();
        stopwatch.Start();;
        var res = (await connection.QueryAsync<RelationTuple>(queryTemplate.RawSql, queryTemplate.Parameters))
            .ToList();
        stopwatch.Stop();
        logger.LogDebug("Queried relations in {}ms", stopwatch.ElapsedMilliseconds);
        return res;
    }
    
    public async Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entitiesIds, string? subjectRelation = null)
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

        logger.LogDebug("Querying relations tuples with filter {sql}, with params: {params}", queryTemplate.RawSql, JsonSerializer.Serialize(new {entityRelationFilter, subjectType, entitiesIds, subjectRelation}));

        var stopwatch = new Stopwatch();
        stopwatch.Start();;
        var res = (await connection.QueryAsync<RelationTuple>(queryTemplate.RawSql, queryTemplate.Parameters))
            .ToList();
        stopwatch.Stop();
        logger.LogDebug("Queried relations in {}ms, returned {} items", stopwatch.ElapsedMilliseconds, res.Count);

        return res;
    }
    
    public async Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityFilter, IEnumerable<SubjectFilter> subjectsFilters)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterRelations(entityFilter, subjectsFilters)
            .AddTemplate(@"SELECT 
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples /**where**/");

        logger.LogDebug("Querying relations tuples with filter {sql}, with params: {params}", queryTemplate.RawSql, JsonSerializer.Serialize(new {entityFilter, subjectsFilters}));

        var stopwatch = new Stopwatch();
        stopwatch.Start();;
        var res = (await connection.QueryAsync<RelationTuple>(queryTemplate.RawSql, queryTemplate.Parameters))
            .ToList();
        stopwatch.Stop();
        logger.LogDebug("Queried relations in {}ms, returned {} items", stopwatch.ElapsedMilliseconds, res.Count);

        return res;
    }
}