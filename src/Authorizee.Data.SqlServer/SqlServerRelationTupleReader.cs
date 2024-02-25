using System.Diagnostics;
using System.Text.Json;
using Authorizee.Core;
using Authorizee.Core.Data;
using Authorizee.Core.Observability;
using Authorizee.Data.Configuration;
using Authorizee.Data.SqlServer.Utils;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Authorizee.Data.SqlServer;

public class SqlServerRelationTupleReader(DbConnectionFactory connectionFactory, ILogger<SqlServerRelationTupleReader> logger)
    : IRelationTupleReader
{
    public async Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        await using var connection = (SqlConnection)connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterRelations(tupleFilter)
            .AddTemplate(@"SELECT
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples with (NOLOCK) /**where**/");

#if DEBUG
        logger.LogDebug("Querying relations tuples with filter: {filter}", JsonSerializer.Serialize(tupleFilter));
        var start = Stopwatch.GetTimestamp();
#endif
        var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
#if DEBUG
        logger.LogDebug("Queried relations in {}ms", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
#endif
        return res;
    }
    
    public async Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entitiesIds, string? subjectRelation, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        await using var connection = (SqlConnection)connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterRelations(entityRelationFilter, subjectType, entitiesIds, subjectRelation)
            .AddTemplate(@"SELECT
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples with (NOLOCK) /**where**/");

#if DEBUG
        logger.LogDebug("Querying relations tuples with filter {sql}, with params: {params}", queryTemplate.RawSql, JsonSerializer.Serialize(new {entityRelationFilter, subjectType, entitiesIds, subjectRelation}));
        var start = Stopwatch.GetTimestamp();
#endif
        var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
        
        
#if DEBUG
        logger.LogDebug("Queried relations in {}ms, returned {} items", Stopwatch.GetElapsedTime(start).TotalMilliseconds, res.Count);
#endif
        return res;
    }
    
    public async Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityFilter,  IList<string> subjectsIds, string subjectType, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        await using var connection = (SqlConnection)connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterRelations(entityFilter, subjectsIds, subjectType)
            .AddTemplate(@"SELECT
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples with (NOLOCK) /**where**/");

#if DEBUG
        logger.LogDebug("Querying relations tuples with filter {sql}, with params: {params}", queryTemplate.RawSql, JsonSerializer.Serialize(new {entityFilter, subjectsIds}));
        var start = Stopwatch.GetTimestamp();
#endif
        
        var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
        
#if DEBUG
        logger.LogDebug("Queried relations in {}ms, returned {} items", Stopwatch.GetElapsedTime(start).TotalMilliseconds, res.Count);
#endif

        return res;
    }
}