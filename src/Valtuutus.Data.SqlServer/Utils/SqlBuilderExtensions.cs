using System.Data;
using Valtuutus.Core.Data;
using Dapper;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer.Utils;

internal static class SqlBuilderExtensions
{
    private const string TvpListIds = "TVP_ListIds";
    private const string EntityTypeFilter = "entity_type = @EntityType";
    private const string EntityIdFilter = "entity_id = @EntityId";
    private const string RelationFilter = "relation = @Relation";
    private const string SubjectTypeFilter = "subject_type = @SubjectType";
    private const string SubjectRelationFilter = "subject_relation = @subjectRelation";

    public static SqlBuilder FilterRelations(this SqlBuilder builder, RelationTupleFilter tupleFilter)
    {
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, tupleFilter);

        builder = builder.Where(EntityTypeFilter, new {EntityType = new DbString()
        {
            Value = tupleFilter.EntityType,
            Length = 256
        }});
        builder = builder.Where(EntityIdFilter, new {EntityId = new DbString()
        {
            Value = tupleFilter.EntityId,
            Length = 64
        }});
        builder = builder.Where(RelationFilter, new {Relation = new DbString()
        {
            Value = tupleFilter.Relation,
            Length = 64
        }});

        if (!string.IsNullOrEmpty(tupleFilter.SubjectId))
            builder = builder.Where("subject_id = @SubjectId", new {SubjectId = new DbString()
            {
                Value = tupleFilter.SubjectId,
                Length = 64
            }});
        
        if (!string.IsNullOrEmpty(tupleFilter.SubjectRelation))
            builder = builder.Where(SubjectRelationFilter, new {SubjectRelation =new DbString()
            {
                Value = tupleFilter.SubjectRelation,
                Length = 64
            }});
        
        if (!string.IsNullOrEmpty(tupleFilter.SubjectType))
            builder = builder.Where(SubjectTypeFilter, new {SubjectType = new DbString()
            {
                Value = tupleFilter.SubjectType,
                Length = 256
            }});
        
        return builder;
    }
    
    public static SqlBuilder FilterRelations(this SqlBuilder builder, EntityRelationFilter entityRelationFilter,
        string subjectType, IEnumerable<string> entitiesIds, string? subjectRelation)
    {
        var entitiesIdsArr = entitiesIds as string[] ?? entitiesIds.ToArray();
        
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, entityRelationFilter);

        
        if (!string.IsNullOrEmpty(subjectType))
            builder = builder.Where(SubjectTypeFilter, new {SubjectType = new DbString()
            {
                Value = subjectType,
                Length = 256
            }});
        
        if (!string.IsNullOrEmpty(entityRelationFilter.EntityType))
            builder = builder.Where(EntityTypeFilter, new {EntityType = new DbString()
            {
                Value = entityRelationFilter.EntityType,
                Length = 256
            }});
        
        if (!string.IsNullOrEmpty(entityRelationFilter.Relation))
            builder = builder.Where(RelationFilter, new {Relation = new DbString()
            {
                Value = entityRelationFilter.Relation,
                Length = 64
            }});

        if (entitiesIdsArr.Length > 0)
        {
            var dt = new DataTable();
            dt.Columns.Add("id", typeof(string));
            foreach (var entityId in entitiesIdsArr)
                dt.Rows.Add(entityId);
            builder = builder.Where("entity_id in (select id from @entitiesIds)", new {entitiesIds = dt.AsTableValuedParameter(TvpListIds)});
        }
        
        if (!string.IsNullOrEmpty(subjectRelation))
            builder = builder.Where(SubjectRelationFilter, new {SubjectRelation = new DbString()
            {
                Value = subjectRelation,
                Length = 64
            }});
        
        return builder;
    }
    
    public static SqlBuilder FilterRelations(this SqlBuilder builder, EntityRelationFilter entityFilter, IList<string> subjectsIds, string subjectType)
    {
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, entityFilter);

        if (!string.IsNullOrEmpty(entityFilter.EntityType))
            builder = builder.Where(EntityTypeFilter, new {EntityType = new DbString()
            {
                Value = entityFilter.EntityType,
                Length = 256
            }});
        
        if (!string.IsNullOrEmpty(entityFilter.Relation))
            builder = builder.Where(RelationFilter, new {Relation = new DbString()
            {
                Value = entityFilter.Relation,
                Length = 64
            }});
        
        builder.Where(SubjectTypeFilter, new {SubjectType = new DbString()
        {
            Value = subjectType,
            Length = 256
        }});

        if (subjectsIds.Count > 0)
        {
            var dt = new DataTable();
            dt.Columns.Add("id", typeof(string));
            foreach (var subjectId in subjectsIds)
                dt.Rows.Add(subjectId);
            builder = builder.Where("subject_id in (select id from @subjectsIds)", new {subjectsIds = dt.AsTableValuedParameter(TvpListIds)});
        }
        
        return builder;
    }

    
    public static SqlBuilder FilterAttributes(this SqlBuilder builder, EntityAttributeFilter filter)
    {
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, filter);

        
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
        
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, filter);
        
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
        
        var dt = new DataTable();
        dt.Columns.Add("id", typeof(string));
        foreach (var entityId in entitiesIdsArr)
            dt.Rows.Add(entityId);
        builder = builder.Where("entity_id in (select id from @entitiesIds)", new {entitiesIds = dt.AsTableValuedParameter(TvpListIds)});
        

        
        return builder;
    }
    
    public static SqlBuilder FilterAttributes(this SqlBuilder builder, EntityAttributesFilter filter, IEnumerable<string> entitiesIds)
    {
        var entitiesIdsArr = entitiesIds as string[] ?? entitiesIds.ToArray();
        
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, filter);
        
        builder = builder.Where(EntityTypeFilter, new {EntityType = new DbString()
        {
            Value = filter.EntityType,
            Length = 256
        }});
        
        
        var attributesDt = new DataTable();
        attributesDt.Columns.Add("id", typeof(string));
        foreach (var attribute in filter.Attributes)
            attributesDt.Rows.Add(attribute);
        builder = builder.Where("attribute in (select id from @Attributes)", new {Attributes = attributesDt.AsTableValuedParameter(TvpListIds)});
        
        var entitiesIdDt = new DataTable();
        entitiesIdDt.Columns.Add("id", typeof(string));
        foreach (var entityId in entitiesIdsArr)
            entitiesIdDt.Rows.Add(entityId);
        builder = builder.Where("entity_id in (select id from @entitiesIds)", new {entitiesIds = entitiesIdDt.AsTableValuedParameter(TvpListIds)});
        

        return builder;
    }

    public static SqlBuilder FilterAttributes(this SqlBuilder builder, EntityAttributesFilter filter)
    {
        
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, filter);
        
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
        
        
        var attributesDt = new DataTable();
        attributesDt.Columns.Add("id", typeof(string));
        foreach (var attribute in filter.Attributes)
            attributesDt.Rows.Add(attribute);
        builder = builder.Where("attribute in (select id from @Attributes)", new {Attributes = attributesDt.AsTableValuedParameter(TvpListIds)});
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
            builder.OrWhere($"entity_type = @EntityType{i} AND entity_id = @EntityId{i}",  new Dictionary<string, object>
            {
                {$"@EntityType{i}", filters[i].EntityType},
                {$"@EntityId{i}", filters[i].EntityId},
            });
            
            if (!string.IsNullOrEmpty(filters[i].Attribute))
                builder = builder.Where($"attribute = @Attribute{i}", new Dictionary<string, object>
                {
                    {$"@Attribute{i}", filters[i].Attribute!},
                });
        }

        return builder;
    }

    
}