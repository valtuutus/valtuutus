using System.Collections.Frozen;

namespace Valtuutus.Core.Schemas;

internal class SchemaBuilder
{
    private readonly List<EntitySchemaBuilder> _entities = [];
    private readonly List<Function> _functions = [];

    public EntitySchemaBuilder WithEntity(string entityName)
    {
        var builder = new EntitySchemaBuilder(entityName, this);
        _entities.Add(builder);
        return builder;
    }

    public Schema Build()
    {
        var schema = new Schema(
            _entities.Select(e => e.Build()).ToDictionary(e => e.Name, e => e, StringComparer.Ordinal),
            _functions.ToDictionary(e => e.Name, StringComparer.Ordinal)
        );
        return schema;
    }

    public SchemaBuilder WithFunction(Function functionNode)
    {
        _functions.Add(functionNode);
        return this;
    }
}

internal class EntitySchemaBuilder(string name, SchemaBuilder schemaBuilder)
{
    private readonly Dictionary<string, Relation> _relations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Permission> _permissions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Attribute> _attributes = new(StringComparer.Ordinal);

    public EntitySchemaBuilder WithRelation(string relationName, Action<RelationSchemaBuilder> config)
    {
        var builder = new RelationSchemaBuilder(relationName);
        config(builder);
        _relations.Add(relationName, builder.Build());
        return this;
    }

    public SchemaBuilder SchemaBuilder => schemaBuilder;

    public EntitySchemaBuilder WithPermission(string permissionName, PermissionNode permissionTree)
    {
        _permissions.Add(permissionName, new Permission(permissionName, PermissionNode.Flatten(permissionTree)));
        return this;
    }

    public EntitySchemaBuilder WithAttribute(string attrName, Type attrType)
    {
        _attributes.Add(attrName, new Attribute(attrName, attrType));
        return this;
    }

    public EntitySchemaBuilder WithEntity(string entityName)
    {
        return schemaBuilder.WithEntity(entityName);
    }

    public Entity Build()
    {
        return new Entity
        {
            Name = name,
            Relations = _relations.ToFrozenDictionary(StringComparer.Ordinal),
            Permissions = _permissions.ToFrozenDictionary(StringComparer.Ordinal),
            Attributes = _attributes.ToFrozenDictionary(StringComparer.Ordinal)
        };
    }
}

internal class RelationSchemaBuilder(string name)
{
    private readonly List<RelationEntity> _entities = new();

    public RelationSchemaBuilder WithEntityType(string entityType, string? entityTypeRelation = null)
    {
        _entities.Add(new RelationEntity { Type = entityType, Relation = entityTypeRelation });
        return this;
    }

    public Relation Build()
    {
        var entityTypes = new HashSet<string>();
        var hasSubRelationPaths = false;
        foreach (var e in _entities)
        {
            entityTypes.Add(e.Type);
            if (e.Relation is not null) hasSubRelationPaths = true;
        }
        return new Relation { Entities = _entities, Name = name, EntityTypes = entityTypes, HasSubRelationPaths = hasSubRelationPaths };
    }
}