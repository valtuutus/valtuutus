using Valtuutus.Core.Data;
using Dapper;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer.Utils;

internal static class SqlBuilderExtensions
{
    internal const string TvpListIds = "TVP_ListIds";
    private const string EntityTypeFilter = "entity_type = @EntityType";
    private const string EntityIdFilter = "entity_id = @EntityId";
    private const string RelationFilter = "relation = @Relation";
    private const string SubjectTypeFilter = "subject_type = @SubjectType";
    private const string SubjectRelationFilter = "subject_relation = @subjectRelation";

    // SQL Server resolves unqualified UDTT names against the connection's default schema,
    // not the configured ValtuutusSqlServerOptions.Schema. Always pass the schema-qualified
    // type name (e.g. "[valtuutus].[TVP_ListIds]") to AsTableValuedParameter.
    internal static string FormatTvpListIdsName(string schema) => $"[{schema}].[{TvpListIds}]";

    public static SqlBuilder FilterRelations(this SqlBuilder builder, RelationTupleFilter tupleFilter)
    {
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, tupleFilter.SnapToken);

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
    
    public static SqlBuilder FilterDirectRelation(this SqlBuilder builder, RelationTupleFilter filter, string subjectId)
    {
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, filter.SnapToken);
        builder = builder.Where(EntityTypeFilter, new { EntityType = new DbString { Value = filter.EntityType, Length = 256 } });
        builder = builder.Where(EntityIdFilter, new { EntityId = new DbString { Value = filter.EntityId, Length = 64 } });
        builder = builder.Where(RelationFilter, new { Relation = new DbString { Value = filter.Relation, Length = 64 } });
        builder = builder.Where("subject_id = @SubjectId", new { SubjectId = new DbString { Value = subjectId, Length = 64 } });
        builder = builder.Where("subject_relation = ''");
        return builder;
    }

    public static SqlBuilder FilterIndirectRelations(this SqlBuilder builder, RelationTupleFilter filter)
    {
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, filter.SnapToken);
        builder = builder.Where(EntityTypeFilter, new { EntityType = new DbString { Value = filter.EntityType, Length = 256 } });
        builder = builder.Where(EntityIdFilter, new { EntityId = new DbString { Value = filter.EntityId, Length = 64 } });
        builder = builder.Where(RelationFilter, new { Relation = new DbString { Value = filter.Relation, Length = 64 } });
        builder = builder.Where("subject_relation <> ''");
        return builder;
    }

    public static SqlBuilder FilterDirectRelationBatch(this SqlBuilder builder, SnapToken snapToken,
        string entityType, string[] entityIds, string relation, string subjectId)
    {
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, snapToken);
        builder = builder.Where(EntityTypeFilter, new { EntityType = new DbString { Value = entityType, Length = 256 } });
        builder = builder.Where("entity_id IN @EntityIds", new { EntityIds = entityIds });
        builder = builder.Where(RelationFilter, new { Relation = new DbString { Value = relation, Length = 64 } });
        builder = builder.Where("subject_id = @SubjectId", new { SubjectId = new DbString { Value = subjectId, Length = 64 } });
        builder = builder.Where("subject_relation = ''");
        return builder;
    }

    public static SqlBuilder FilterRelations(this SqlBuilder builder, EntityRelationFilter entityRelationFilter,
        string subjectType, IEnumerable<string> entitiesIds, string? subjectRelation, string tvpTypeName)
    {
        var entitiesIdsArr = entitiesIds as string[] ?? entitiesIds.ToArray();

        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, entityRelationFilter.SnapToken);


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
            var tvpParam = TvpHelper.AsTvpParameter(entitiesIdsArr, tvpTypeName);
            builder = builder.Where("entity_id in (select id from @entitiesIds)", new { entitiesIds = tvpParam });
        }
        else
        {
            // An empty entity-id set means "no entities to resolve" -> match nothing. Omitting the
            // predicate here would return every row of this relation/subject_type (entity scope dropped).
            builder = builder.Where("1 = 0");
        }

        if (!string.IsNullOrEmpty(subjectRelation))
            builder = builder.Where(SubjectRelationFilter, new {SubjectRelation = new DbString()
            {
                Value = subjectRelation,
                Length = 64
            }});

        return builder;
    }

    public static SqlBuilder FilterRelations(this SqlBuilder builder, EntityRelationFilter entityFilter, string[] subjectsIds, string subjectType, string tvpTypeName)
    {
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, entityFilter.SnapToken);

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

        if (subjectsIds.Length > 0)
        {
            var tvpParam = TvpHelper.AsTvpParameter(subjectsIds, tvpTypeName);
            builder = builder.Where("subject_id in (select id from @subjectsIds)", new { subjectsIds = tvpParam });
        }
        else
        {
            // An empty subject-id set means "no subjects to resolve" -> match nothing. Omitting the
            // predicate here would return every row of this relation/subject_type (subject scope dropped).
            builder = builder.Where("1 = 0");
        }

        return builder;
    }


    public static SqlBuilder FilterAttributes(this SqlBuilder builder, EntityAttributeFilter filter)
    {
        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, filter.SnapToken);

        
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

    public static SqlBuilder FilterAttributes(this SqlBuilder builder, AttributeFilter filter, IEnumerable<string> entitiesIds, string tvpTypeName)
    {
        var entitiesIdsArr = entitiesIds as string[] ?? entitiesIds.ToArray();

        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, filter.SnapToken);

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

        if (entitiesIdsArr.Length > 0)
        {
            var tvpParam = TvpHelper.AsTvpParameter(entitiesIdsArr, tvpTypeName);
            builder = builder.Where("entity_id in (select id from @entitiesIds)", new { entitiesIds = tvpParam });
        }
        else
        {
            // An empty entity-id set means "no entities to resolve" -> match nothing. Omitting the
            // predicate here would return every row of this entity_type/attribute (entity scope dropped).
            builder = builder.Where("1 = 0");
        }

        return builder;
    }

    public static SqlBuilder FilterAttributes(this SqlBuilder builder, EntityAttributesFilter filter, IEnumerable<string> entitiesIds, string tvpTypeName)
    {
        var entitiesIdsArr = entitiesIds as string[] ?? entitiesIds.ToArray();

        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, filter.SnapToken);

        builder = builder.Where(EntityTypeFilter, new {EntityType = new DbString()
        {
            Value = filter.EntityType,
            Length = 256
        }});

        if (filter.Attributes.Length > 0)
        {
            var attributesTvp = TvpHelper.AsTvpParameter(filter.Attributes, tvpTypeName);
            builder = builder.Where("attribute in (select id from @Attributes)", new { Attributes = attributesTvp });
        }
        else
        {
            // An empty attribute set means "no attributes to resolve" -> match nothing. Omitting the
            // predicate here would return every row of this entity_type (attribute scope dropped).
            builder = builder.Where("1 = 0");
        }

        if (entitiesIdsArr.Length > 0)
        {
            var entitiesTvp = TvpHelper.AsTvpParameter(entitiesIdsArr, tvpTypeName);
            builder = builder.Where("entity_id in (select id from @entitiesIds)", new { entitiesIds = entitiesTvp });
        }
        else
        {
            // An empty entity-id set means "no entities to resolve" -> match nothing. Omitting the
            // predicate here would return every row of this entity_type/attribute (entity scope dropped).
            builder = builder.Where("1 = 0");
        }

        return builder;
    }

    public static SqlBuilder FilterAttributes(this SqlBuilder builder, EntityAttributesFilter filter, string tvpTypeName)
    {

        builder = CommonSqlBuilderExtensions.ApplySnapTokenFilter(builder, filter.SnapToken);

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

        if (filter.Attributes.Length > 0)
        {
            var attributesTvp = TvpHelper.AsTvpParameter(filter.Attributes, tvpTypeName);
            builder = builder.Where("attribute in (select id from @Attributes)", new { Attributes = attributesTvp });
        }
        else
        {
            // An empty attribute set means "no attributes to resolve" -> match nothing. Omitting the
            // predicate here would return every row of this entity_type (attribute scope dropped).
            builder = builder.Where("1 = 0");
        }

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