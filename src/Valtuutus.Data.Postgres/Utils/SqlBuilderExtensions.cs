using System.Diagnostics;
using Valtuutus.Core.Data;
using Dapper;

namespace Valtuutus.Data.Postgres.Utils;

internal static class SqlBuilderExtensions
{
    private const string SnapTokenFilter = "created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)";
    private const string EntityTypeFilter = "entity_type = @EntityType";
    private const string SubjectTypeFilter = "subject_type = @SubjectType";
    private const string RelationFilter = "relation = @Relation";
    private const string SubjectRelationFilter = "subject_relation = @SubjectRelation";
    private const string EntityIdFilter = "entity_id = @EntityId";

    public static SqlBuilder FilterRelations(this SqlBuilder builder, RelationTupleFilter filter)
    {
        if (filter.SnapToken != null)
        {
            var parameters = new { SnapToken = new DbString()
            {
                Value = filter.SnapToken.Value.Value,
                Length = 26,
                IsFixedLength = true,
            }};
            builder = builder.Where(
                SnapTokenFilter,
                parameters);
        }

        builder = builder.Where(EntityTypeFilter, new {EntityType = new DbString()
        {
            Value = filter.EntityType,
            Length = 256
        }});
        builder = builder.Where(EntityIdFilter, new {EntityId = new DbString()
        {
            Value = filter.EntityId,
            Length = 64
        }});
        builder = builder.Where(RelationFilter, new {Relation = new DbString()
        {
            Value = filter.Relation,
            Length = 64
        }});

        if (!string.IsNullOrEmpty(filter.SubjectId))
            builder = builder.Where("subject_id = @SubjectId", new {SubjectId = new DbString()
            {
                Value = filter.SubjectId,
                Length = 64
            }});
        
        if (!string.IsNullOrEmpty(filter.SubjectRelation))
            builder = builder.Where(SubjectRelationFilter, new {SubjectRelation =new DbString()
            {
                Value = filter.SubjectRelation,
                Length = 64
            }});
        
        if (!string.IsNullOrEmpty(filter.SubjectType))
            builder = builder.Where(SubjectTypeFilter, new {SubjectType = new DbString()
            {
                Value = filter.SubjectType,
                Length = 256
            }});
        
        return builder;
    }
    
    public static SqlBuilder FilterRelations(this SqlBuilder builder, EntityRelationFilter filter,
        string subjectType, IEnumerable<string> entitiesIds, string? subjectRelation)
    {
        var entitiesIdsArr = entitiesIds as string[] ?? entitiesIds.ToArray();
        
        if (filter.SnapToken != null)
        {
            var parameters = new { SnapToken = new DbString()
            {
                Value = filter.SnapToken.Value.Value,
                Length = 26,
                IsFixedLength = true,
            }};
            builder = builder.Where(
                SnapTokenFilter,
                parameters);
        }

        if (!string.IsNullOrEmpty(subjectType))
            builder = builder.Where(SubjectTypeFilter, new {SubjectType = subjectType});
        
        if (!string.IsNullOrEmpty(filter.EntityType))
            builder = builder.Where(EntityTypeFilter, new {filter.EntityType});
        
        if (!string.IsNullOrEmpty(filter.Relation))
            builder = builder.Where(RelationFilter, new {filter.Relation});
        
        if (entitiesIdsArr.Length > 0)
            builder = builder.Where("entity_id = ANY(@EntitiesIds)", new {EntitiesIds = entitiesIdsArr});
        
        if (!string.IsNullOrEmpty(subjectRelation))
            builder = builder.Where(SubjectRelationFilter, new {SubjectRelation = subjectRelation});
        
        return builder;
    }
    
    public static SqlBuilder FilterRelations(this SqlBuilder builder, EntityRelationFilter filter,  IList<string> subjectsIds, string subjectType)
    {
     
        if (filter.SnapToken != null)
        {
            var parameters = new { SnapToken = new DbString()
            {
                Value = filter.SnapToken.Value.Value,
                Length = 26,
                IsFixedLength = true,
            }};
            builder = builder.Where(
                SnapTokenFilter,
                parameters);
        }
        
        if (!string.IsNullOrEmpty(filter.EntityType))
            builder = builder.Where(EntityTypeFilter, new {filter.EntityType});
        
        if (!string.IsNullOrEmpty(filter.Relation))
            builder = builder.Where(RelationFilter, new {filter.Relation});
        
        builder.Where(SubjectTypeFilter, new {SubjectType = subjectType});

        if (subjectsIds.Count != 0)
            builder = builder.Where("subject_id = ANY(@SubjectsIds)", new {SubjectsIds = subjectsIds});
        
        return builder;
    }

    
    public static SqlBuilder FilterAttributes(this SqlBuilder builder, EntityAttributeFilter filter)
    {
        if (filter.SnapToken != null)
        {
            var parameters = new { SnapToken = new DbString()
            {
                Value = filter.SnapToken.Value.Value,
                Length = 26,
                IsFixedLength = true,
            }};
            builder = builder.Where(
                SnapTokenFilter,
                parameters);
        }
        
        builder = builder.Where(EntityTypeFilter, new {EntityType = new DbString()
        {
            Value = filter.EntityType,
            Length = 256
        }});
        builder = builder.Where("attribute = @Attribute", new {Attribute =new DbString()
        {
            Value = filter.Attribute,
            Length = 64
        }});
        
        if (!string.IsNullOrWhiteSpace(filter.EntityId))
            builder = builder.Where(EntityIdFilter, new {EntityId = new DbString()
            {
                Value = filter.EntityId,
                Length = 64
            }});
        
        return builder;
    }
    
    public static SqlBuilder FilterAttributes(this SqlBuilder builder, AttributeFilter filter, IEnumerable<string> entitiesIds)
    {
        var entitiesIdsArr = entitiesIds as string[] ?? entitiesIds.ToArray();
        
        if (filter.SnapToken != null)
        {
            var parameters = new { SnapToken = new DbString()
            {
                Value = filter.SnapToken.Value.Value,
                Length = 26,
                IsFixedLength = true,
            }};
            builder = builder.Where(
                SnapTokenFilter,
                parameters);
        }
          
        builder = builder.Where(EntityTypeFilter, new {EntityType = new DbString()
        {
            Value = filter.EntityType,
            Length = 256
        }});
        builder = builder.Where("attribute = @Attribute", new {Attribute = new DbString()
        {
            Value = filter.Attribute,
            Length = 64
        }});

        
        if (entitiesIdsArr.Length != 0)
            builder = builder.Where("entity_id = ANY(@entitiesIds)", new {entitiesIds = entitiesIdsArr});
        
        return builder;
    }
    
    public static SqlBuilder FilterAttributes(this SqlBuilder builder, EntityAttributesFilter filter)
    {
        
        if (filter.SnapToken != null)
        {
            var parameters = new { SnapToken = new DbString()
            {
                Value = filter.SnapToken.Value.Value,
                Length = 26,
                IsFixedLength = true,
            }};
            builder = builder.Where(
                SnapTokenFilter,
                parameters);
        }

        if (!string.IsNullOrEmpty(filter.EntityId))
        {
            builder = builder.Where(EntityIdFilter, new {EntityId = new DbString()
            {
                Value = filter.EntityId,
                Length = 64
            }});
        }
          
        builder = builder.Where(EntityTypeFilter, new {EntityType = new DbString()
        {
            Value = filter.EntityType,
            Length = 256
        }});
        
        builder = builder.Where("attribute = ANY(@Attributes)", new { filter.Attributes });
        
        return builder;
    }

    public static SqlBuilder FilterAttributes(this SqlBuilder builder, EntityAttributesFilter filter, IEnumerable<string> entitiesIds)
    {
        var entitiesIdsArr = entitiesIds as string[] ?? entitiesIds.ToArray();

        if (filter.SnapToken != null)
        {
            var parameters = new { SnapToken = new DbString()
            {
                Value = filter.SnapToken.Value.Value,
                Length = 26,
                IsFixedLength = true,
            }};
            builder = builder.Where(
                SnapTokenFilter,
                parameters);
        }
          
        builder = builder.Where(EntityTypeFilter, new {EntityType = new DbString()
        {
            Value = filter.EntityType,
            Length = 256
        }});
        
        builder = builder.Where("attribute = ANY(@Attributes)", new { filter.Attributes });
        
        builder = builder.Where("entity_id = ANY(@entitiesIds)", new {entitiesIds = entitiesIdsArr});
        
        return builder;
    }

    public static SqlBuilder FilterDeleteRelations(this SqlBuilder builder, DeleteRelationsFilter[] filters)
    {
        builder.Where("deleted_tx_id IS NULL");
        for (int i = 0; i < filters.Length; i++)
        {
            builder.OrWhere($"""
                             (@EntityType{i} IS NULL OR entity_type = @EntityType{i}) and 
                             (@EntityId{i} IS NULL OR entity_id = @EntityId{i}) and 
                             (@SubjectType{i} IS NULL OR subject_type = @SubjectType{i}) and 
                             (@SubjectId{i} IS NULL OR subject_id = @SubjectId{i}) and 
                             (@Relation{i} IS NULL OR relation = @Relation{i}) and 
                             (@SubjectRelation{i} IS NULL OR subject_relation = @SubjectRelation{i})
                             """,
                new Dictionary<string, object?>
            {
                {$"@EntityType{i}", new DbString
                {
                    Value = filters[i].EntityType,
                    Length = 256
                }},
                {$"@EntityId{i}",  new DbString
                {
                    Value = filters[i].EntityId,
                    Length = 64
                }},
                {$"@SubjectType{i}", new DbString
                {
                    Value = filters[i].SubjectType,
                    Length = 256
                }},
                {$"@SubjectId{i}", new DbString
                {
                    Value = filters[i].SubjectId,
                    Length = 64
                }},
                {$"@Relation{i}", new DbString
                {
                    Value = filters[i].Relation,
                    Length = 64
                }},
                {$"@SubjectRelation{i}", new DbString
                {
                    Value = filters[i].SubjectRelation,
                    Length = 64
                }},
                
            });
        }

        return builder;
    }

    public static SqlBuilder FilterDeleteAttributes(this SqlBuilder builder, DeleteAttributesFilter[] filters)
    {
        builder.Where("deleted_tx_id IS NULL");
        for (int i = 0; i < filters.Length; i++)
        {
            builder.OrWhere($"entity_type = @EntityType{i} AND entity_id = @EntityId{i}",
                new Dictionary<string, object>
                {
                    { $"@EntityType{i}", filters[i].EntityType }, { $"@EntityId{i}", filters[i].EntityId },
                });

            if (!string.IsNullOrEmpty(filters[i].Attribute))
                builder = builder.Where($"attribute = @Attribute{i}",
                    new Dictionary<string, object> { { $"@Attribute{i}", filters[i].Attribute! }, });
        }

        return builder;
    }
}