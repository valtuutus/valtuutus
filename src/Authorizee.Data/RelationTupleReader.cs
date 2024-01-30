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
    public async Task<List<RelationTuple>> GetRelations(RelationFilter filter)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterRelations(filter)
            .AddTemplate(@"SELECT 
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM relation_tuples /**where**/");

        logger.LogDebug("Querying relations tuples with filter: {filter}", filter);

        return (await connection.QueryAsync<RelationTuple>(queryTemplate.RawSql, queryTemplate.Parameters))
            .ToList();
    }
}