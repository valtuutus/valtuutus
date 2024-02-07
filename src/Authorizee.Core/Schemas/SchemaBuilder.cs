namespace Authorizee.Core.Schemas;

// TODO: Validate schema correctness before injecting into the dependency injection container

public class SchemaBuilder
{
    private readonly List<EntitySchemaBuilder> _entities = [];
    private readonly List<Rule> _rules = [];

    public EntitySchemaBuilder WithEntity(string entityName)
    {
        var builder = new EntitySchemaBuilder(entityName, this);
        _entities.Add(builder);
        return builder;
    }

    public SchemaBuilder WithRule<T>(string ruleName, string rule)
    {
        _rules.Add(new Rule { Name = ruleName, RuleFn = rule });
        return this;
    }

    public (Schema, SchemaGraph) Build()
    {
        var schema = new Schema(_entities.Select(e => e.Build()).ToList(), _rules);
        var graph = new SchemaGraph(schema);
        return (schema, graph);
    }
}

public class EntitySchemaBuilder(string name, SchemaBuilder schemaBuilder)
{
    private readonly List<Relation> _relations = new();
    private readonly List<Permission> _permissions = new();
    private readonly List<Attribute> _attributes = new();

    public EntitySchemaBuilder WithRelation(string relationName, Action<RelationSchemaBuilder> config)
    {
        var builder = new RelationSchemaBuilder(relationName);
        config(builder);
        _relations.Add(builder.Build());
        return this;
    }
    
    public SchemaBuilder SchemaBuilder => schemaBuilder;

    public EntitySchemaBuilder WithPermission(string permissionName, PermissionNode permissionTree)
    {
        _permissions.Add(new Permission(permissionName, permissionTree));
        return this;
    }

    public EntitySchemaBuilder WithAttribute(string attrName, Type attrType)
    {
        _attributes.Add(new Attribute(attrName, attrType));
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
            Permissions = _permissions,
            Attributes = _attributes
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