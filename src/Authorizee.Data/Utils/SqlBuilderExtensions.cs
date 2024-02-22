using System.Dynamic;
using Authorizee.Core.Data;
using Dapper;
using Jint.Runtime.Descriptors;

namespace Authorizee.Data.Utils;

public static class SqlBuilderExtensions
{
    public static SqlBuilder FilterRelations(this SqlBuilder builder, RelationTupleFilter tupleFilter)
    {
        builder = builder.Where("entity_type = @EntityType", tupleFilter);
        builder = builder.Where("entity_id = @EntityId", tupleFilter);
        builder = builder.Where("relation = @Relation", tupleFilter);

        if (!string.IsNullOrEmpty(tupleFilter.SubjectId))
            builder = builder.Where("subject_id = @SubjectId", tupleFilter);
        
        if (!string.IsNullOrEmpty(tupleFilter.SubjectRelation))
            builder = builder.Where("subject_id = @SubjectRelation", tupleFilter);
        
        if (!string.IsNullOrEmpty(tupleFilter.SubjectType))
            builder = builder.Where("subject_id = @SubjectType", tupleFilter);
        
        return builder;
    }
    
    public static SqlBuilder FilterRelations(this SqlBuilder builder, IEnumerable<EntityRelationFilter> filters, SubjectFilter? subjectFilter)
    {
        if (!string.IsNullOrEmpty(subjectFilter?.SubjectId))
            builder = builder.Where("subject_id = @SubjectId", new {subjectFilter.SubjectId});
        
        if (!string.IsNullOrEmpty(subjectFilter?.SubjectType))
            builder = builder.Where("subject_type = @SubjectType", new {subjectFilter.SubjectType});

        var entityRelationFilters = filters as EntityRelationFilter[] ?? filters.ToArray();
        for (var i = 0; i < entityRelationFilters.Length; i++)
        {
            var filter = entityRelationFilters[i];
            builder = builder.OrWhere($"(entity_type = @EntityType{i} AND relation = @Relation{i})", new Dictionary<string, object>
            {
                {$"@EntityType{i}", filter.EntityType},
                {$"@Relation{i}", filter.Relation},
            });
        }
        
        return builder;
    }
    
    public static SqlBuilder FilterRelations(this SqlBuilder builder, EntityRelationFilter entityFilter, IEnumerable<SubjectFilter> subjectsFilters)
    {
        if (!string.IsNullOrEmpty(entityFilter.EntityType))
            builder = builder.Where("entity_type = @EntityType", new {entityFilter.EntityType});
        
        if (!string.IsNullOrEmpty(entityFilter.Relation))
            builder = builder.Where("relation = @Relation", new {entityFilter.Relation});

        var entityRelationFilters = subjectsFilters as SubjectFilter[] ?? subjectsFilters.ToArray();
        for (var i = 0; i < entityRelationFilters.Length; i++)
        {
            var filter = entityRelationFilters[i];
            builder = builder.OrWhere($"(subject_type = @SubjectType{i} AND subject_id = @SubjectId{i})", new Dictionary<string, object>
            {
                {$"@SubjectType{i}", filter.SubjectType},
                {$"@SubjectId{i}", filter.SubjectId},
            });
        }
        
        return builder;
    }

    
    public static SqlBuilder FilterAttributes(this SqlBuilder builder, AttributeFilter filter)
    {
        builder = builder.Where("entity_type = @EntityType", filter);
        builder = builder.Where("attribute = @Attribute", filter);
        
        if (!string.IsNullOrWhiteSpace(filter.EntityId))
            builder = builder.Where("entity_id = @EntityId", filter);
        
        return builder;
    }
}