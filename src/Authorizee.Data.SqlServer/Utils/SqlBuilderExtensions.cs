using Authorizee.Core.Data;
using Dapper;

namespace Authorizee.Data.SqlServer.Utils;

public static class SqlBuilderExtensions
{
    public static SqlBuilder FilterRelations(this SqlBuilder builder, RelationTupleFilter tupleFilter)
    {
        builder = builder.Where("entity_type = @EntityType", new {EntityType = new DbString()
        {
            Value = tupleFilter.EntityType,
            IsFixedLength = true,
            IsAnsi = true,
            Length = 256
        }});
        builder = builder.Where("entity_id = @EntityId", new {EntityId = new DbString()
        {
            Value = tupleFilter.EntityId,
            IsFixedLength = true,
            IsAnsi = true,
            Length = 64
        }});
        builder = builder.Where("relation = @Relation", new {Relation = new DbString()
        {
            Value = tupleFilter.Relation,
            IsFixedLength = true,
            IsAnsi = true,
            Length = 64
        }});

        if (!string.IsNullOrEmpty(tupleFilter.SubjectId))
            builder = builder.Where("subject_id = @SubjectId", new {SubjectId = new DbString()
            {
                Value = tupleFilter.SubjectId,
                IsFixedLength = true,
                IsAnsi = true,
                Length = 64
            }});
        
        if (!string.IsNullOrEmpty(tupleFilter.SubjectRelation))
            builder = builder.Where("subject_relation = @SubjectRelation", new {SubjectRelation =new DbString()
            {
                Value = tupleFilter.SubjectRelation,
                IsFixedLength = true,
                IsAnsi = true,
                Length = 64
            }});
        
        if (!string.IsNullOrEmpty(tupleFilter.SubjectType))
            builder = builder.Where("subject_type = @SubjectType", new {SubjectType = new DbString()
            {
                Value = tupleFilter.SubjectType,
                IsFixedLength = true,
                IsAnsi = true,
                Length = 256
            }});
        
        return builder;
    }
    
    public static SqlBuilder FilterRelations(this SqlBuilder builder, EntityRelationFilter entityRelationFilter,
        string subjectType, IEnumerable<string> entitiesIds, string? subjectRelation)
    {
        var entitiesIdsArr = entitiesIds as string[] ?? entitiesIds.ToArray();
        
        if (!string.IsNullOrEmpty(subjectType))
            builder = builder.Where("subject_type = @SubjectType", new {SubjectType = new DbString()
            {
                Value = subjectType,
                IsFixedLength = true,
                IsAnsi = true,
                Length = 256
            }});
        
        if (!string.IsNullOrEmpty(entityRelationFilter.EntityType))
            builder = builder.Where("entity_type = @EntityType", new {EntityType = new DbString()
            {
                Value = entityRelationFilter.EntityType,
                IsFixedLength = true,
                IsAnsi = true,
                Length = 256
            }});
        
        if (!string.IsNullOrEmpty(entityRelationFilter.Relation))
            builder = builder.Where("relation = @Relation", new {Relation = new DbString()
            {
                Value = entityRelationFilter.Relation,
                IsFixedLength = true,
                IsAnsi = true,
                Length = 64
            }});
        
        if (entitiesIdsArr.Length != 0)
            // TODO: certamente isso aqui está quebrado
            builder = builder.Where("entity_id = ANY(@entitiesIds)", new {entitiesIds = entitiesIdsArr});
        
        if (!string.IsNullOrEmpty(subjectRelation))
            builder = builder.Where("subject_relation = @subjectRelation", new {subjectRelation = new DbString()
            {
                Value = subjectRelation,
                IsFixedLength = true,
                IsAnsi = true,
                Length = 64
            }});
        
        return builder;
    }
    
    public static SqlBuilder FilterRelations(this SqlBuilder builder, EntityRelationFilter entityFilter, IList<string> subjectsIds, string subjectType)
    {
        if (!string.IsNullOrEmpty(entityFilter.EntityType))
            builder = builder.Where("entity_type = @EntityType", new {EntityType = new DbString()
            {
                Value = entityFilter.EntityType,
                IsFixedLength = true,
                IsAnsi = true,
                Length = 256
            }});
        
        if (!string.IsNullOrEmpty(entityFilter.Relation))
            builder = builder.Where("relation = @Relation", new {Relation = new DbString()
            {
                Value = entityFilter.Relation,
                IsFixedLength = true,
                IsAnsi = true,
                Length = 64
            }});
        
        builder.Where("subject_type = @SubjectType", new {SubjectType = new DbString()
        {
            Value = subjectType,
            IsFixedLength = true,
            IsAnsi = true,
            Length = 256
        }});

        for (var i = 0; i < subjectsIds.Count; i++)
        {
            builder = builder.OrWhere($"(subject_id = @SubjectId{i})", new Dictionary<string, object>
            {
                {$"@SubjectId{i}", new DbString()
                {
                    Value = subjectsIds[i],
                    IsFixedLength = true,
                    IsAnsi = true,
                    Length = 256
                }},
            });
        }
        
        return builder;
    }

    
    public static SqlBuilder FilterAttributes(this SqlBuilder builder, EntityAttributeFilter filter)
    {
        builder = builder.Where("entity_type = @EntityType", new {EntityType = new DbString()
        {
            Value = filter.EntityType,
            IsFixedLength = true,
            IsAnsi = true,
            Length = 256
        }});
        builder = builder.Where("attribute = @Attribute", new {Attribute =new DbString()
        {
            Value = filter.Attribute,
            IsFixedLength = true,
            IsAnsi = true,
            Length = 64
        }});
        
        if (!string.IsNullOrWhiteSpace(filter.EntityId))
            builder = builder.Where("entity_id = @EntityId", new {EntityId = new DbString()
            {
                Value = filter.EntityType,
                IsFixedLength = true,
                IsAnsi = true,
                Length = 64
            }});
        
        return builder;
    }
    
    public static SqlBuilder FilterAttributes(this SqlBuilder builder, AttributeFilter filter, IEnumerable<string> entitiesIds)
    {
        var entitiesIdsArr = entitiesIds as string[] ?? entitiesIds.ToArray();
        
        builder = builder.Where("entity_type = @EntityType", new {EntityType = new DbString()
        {
            Value = filter.EntityType,
            IsFixedLength = true,
            IsAnsi = true,
            Length = 256
        }});
        builder = builder.Where("attribute = @Attribute", new {Attribute = new DbString()
        {
            Value = filter.Attribute,
            IsFixedLength = true,
            IsAnsi = true,
            Length = 64
        }});
        
        if (entitiesIdsArr.Length != 0)
            // TODO: Certamente está quebrado
            builder = builder.Where("entity_id = ANY(@entitiesIds)", new {entitiesIds = entitiesIdsArr});
        
        return builder;
    }
}