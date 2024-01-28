namespace Authorizee.Core.Schemas;

public class SchemaBuilder
{
    private readonly List<EntitySchemaBuilder> _entities = [];

    public EntitySchemaBuilder WithEntity(string entityName)
    {
        var builder = new EntitySchemaBuilder(entityName, this);
        _entities.Add(builder);
        return builder;
    }

    public Schema Build()
    {
        return new Schema(_entities.Select(e => e.Build()).ToList());
    }
}

public class EntitySchemaBuilder(string name, SchemaBuilder schemaBuilder)
{
    private readonly List<Relation> _relations = new();
    private readonly List<Permission> _permissions = new();

    public EntitySchemaBuilder WithRelation(string relationName, Action<RelationSchemaBuilder> config)
    {
        var builder = new RelationSchemaBuilder(relationName);
        config(builder);
        _relations.Add(builder.Build());
        return this;
    }

    public EntitySchemaBuilder WithPermission(string permissionName, PermissionNode permissionTree)
    {
        _permissions.Add(new Permission(permissionName, permissionTree));
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
            Relations = _relations,
            Permissions = _permissions
        };
    }
}

public class RelationSchemaBuilder(string name)
{
    private readonly List<RelationEntity> _entities = new();

    public RelationSchemaBuilder WithEntityType(string entityType, string? entityTypeRelation = null)
    {
        _entities.Add(new RelationEntity
        {
            Type = entityType,
            Relation = entityTypeRelation
        });
        return this;
    }

    public Relation Build()
    {
        return new Relation
        {
            Entities = _entities,
            Name = name
        };
    }
}