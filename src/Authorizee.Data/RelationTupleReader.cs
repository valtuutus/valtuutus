using System.Data;
using Authorizee.Core;
using Authorizee.Core.Data;
using Authorizee.Data.Utils;
using Dapper;

namespace Authorizee.Data;

public class RelationTupleReader(IDbConnection connection) : IRelationTupleReader
{
    public async Task<List<RelationTuple>> GetRelations(RelationFilter filter)
    {
        var queryTemplate = new SqlBuilder()
            .FilterRelations(filter)
            .AddTemplate(@"SELECT 
                    entity_type as EntityType,
                    entity_id as EntityId,
                    relation as Relation,
                    subject_type as SubjectType,
                    subject_id as SubjectId, 
                    subject_relation as SubjectRelation 
                FROM relation_tuples /**where**/");
        
        return (await connection.QueryAsync<RelationTuple>(queryTemplate.RawSql, queryTemplate.Parameters))
            .ToList();
    }
}