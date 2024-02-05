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

        logger.LogDebug("Querying relations tuples with filter: {filter}", tupleFilter);

        return (await connection.QueryAsync<RelationTuple>(queryTemplate.RawSql, queryTemplate.Parameters))
            .ToList();
    }
    
    public async Task<List<RelationTuple>> GetRelations(IEnumerable<EntityRelationFilter> filters, SubjectFilter? subjectFilter)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterRelations(filters, subjectFilter)
            .AddTemplate(@"SELECT 
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples /**where**/");

        logger.LogDebug("Querying relations tuples with filter {sql}, with params: {params}", queryTemplate.RawSql, queryTemplate.Parameters);

        return (await connection.QueryAsync<RelationTuple>(queryTemplate.RawSql, queryTemplate.Parameters))
            .ToList();
    }
}