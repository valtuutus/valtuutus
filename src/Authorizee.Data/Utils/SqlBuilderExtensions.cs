using Authorizee.Core.Data;
using Dapper;

namespace Authorizee.Data.Utils;

public static class SqlBuilderExtensions
{
    public static SqlBuilder FilterRelations(this SqlBuilder builder, RelationFilter filter)
    {
        builder = builder.Where("entity_type = @EntityType", filter);
        builder = builder.Where("entity_id = @EntityId", filter);
        builder = builder.Where("relation = @Relation", filter);

        if (!string.IsNullOrEmpty(filter.SubjectId))
            builder = builder.Where("subject_id = @SubjectId", filter);
        
        if (!string.IsNullOrEmpty(filter.SubjectRelation))
            builder = builder.Where("subject_id = @SubjectRelation", filter);
        
        if (!string.IsNullOrEmpty(filter.SubjectType))
            builder = builder.Where("subject_id = @SubjectType", filter);
        
        return builder;
    }
    
    public static SqlBuilder FilterAttributes(this SqlBuilder builder, AttributeFilter filter)
    {
        builder = builder.Where("entity_type = @EntityType", filter);
        builder = builder.Where("entity_id = @EntityId", filter);
        builder = builder.Where("attribute = @Attribute", filter);
        
        return builder;
    }
}