namespace Authorizee.Core.Schemas;

public interface ISchemaNode
{
    public NodeType Type { get; }
}

public class SchemaRelationsIdentifier(Schema Schema)
{
    private readonly List<(string, string)> _relations = new();
    private readonly Dictionary<string, ISchemaNode> _nodeKeyMap = new();

    public (List<(string, string)>, Dictionary<string, ISchemaNode>) Identify()
    {
        foreach (var entity in Schema.Entities)
        {
            BuildGraphRecursive(BuildEntityKey(entity.Name), null);
        }

        return (_relations, _nodeKeyMap);
    }

    private void BuildGraphRecursive(string key, string? parentKey)
    {
        if (!string.IsNullOrWhiteSpace(parentKey))
        {
            _relations.Add((parentKey, key));
            _relations.Add((key, parentKey));
        }

        if (_nodeKeyMap.ContainsKey(key))
            return;

        Action builder = key.Split(':') switch
        {
            ["entity", { } entityName] => () => BuildEntityGraphRecursive(key, entityName),
            ["entity", { } entityName, "attr", { } attrName] => () =>
                BuildAttributeGraphRecursive(key, attrName, entityName),
            ["entity", { } entityName, "relation", { } relationName] => () =>
                BuildRelationGraphRecursive(key, relationName, entityName),
            ["entity", { } entityName, "perm", { } permName] => () =>
                BuildPermissionGraphRecursive(key, permName, entityName),
            _ => throw new InvalidOperationException()
        };

        builder();
    }

    public void BuildEntityGraphRecursive(string key, string entityName)
    {
        var entity =
            Schema.Entities.First(x => x.Name.Equals(entityName, StringComparison.InvariantCultureIgnoreCase));

        _nodeKeyMap.Add(key, new EntityNode()
        {
            Name = entity.Name
        });

        foreach (var attr in entity.Attributes)
        {
            BuildGraphRecursive(BuildAttrKey(entityName, attr.Name), key);
        }

        foreach (var relation in entity.Relations)
        {
            BuildGraphRecursive(BuildRelationKey(entityName, relation.Name), key);
        }

        foreach (var perm in entity.Permissions)
        {
            BuildGraphRecursive(BuildPermissionKey(entityName, perm.Name), key);
        }
    }

    private void BuildPermissionGraphRecursive(string key, string permissionName, string entityName)
    {
        var perm = Schema.GetPermissions(entityName)
            .First(x => x.Name.Equals(permissionName, StringComparison.InvariantCultureIgnoreCase));

        _nodeKeyMap.Add(key, new PermissionGraphNode()
        {
            Name = permissionName,
            ExpressionRoot = perm.Tree
        });

        RelationType identifyType(string entityType, string relationName)
        {
            var permission = Schema.GetPermissions(entityType)
                .FirstOrDefault(x => x.Name == relationName);
            var relation = Schema.GetRelations(entityType)
                .FirstOrDefault(x => x.Name == relationName);
            var attribute = Schema.GetAttributes(entityType)
                .FirstOrDefault(x => x.Name == relationName);

            return new { permission, relation, attribute } switch
            {
                { permission: null, relation: not null } => RelationType.DirectRelation,
                { permission: not null, relation: null } => RelationType.Permission,
                { attribute: not null } => RelationType.Attribute,
                _ => RelationType.None
            };
        }

        void handleLeaf(PermissionNodeLeaf leafNode)
        {
            if (leafNode.Value.Split('.') is [{ } relationName, { } relationLock])
            {
                var relation = Schema.GetRelations(entityName)
                    .First(x => x.Name.Equals(relationName));

                foreach (var relationEntity in relation.Entities)
                {
                    if (!string.IsNullOrWhiteSpace(relationEntity.Relation))
                    {
                        throw new InvalidOperationException();
                    }

                    Action action = identifyType(relationEntity.Type, relationLock) switch
                    {
                        RelationType.Attribute => () =>
                            BuildGraphRecursive(BuildAttrKey(relationEntity.Type, relationLock), key),
                        RelationType.Permission => () =>
                            BuildGraphRecursive(BuildPermissionKey(relationEntity.Type, relationLock), key),
                        RelationType.DirectRelation => () =>
                            BuildGraphRecursive(BuildRelationKey(relationEntity.Type, relationLock), key),
                    };

                    action();
                }
            }
            else
            {
                Action action = identifyType(entityName, leafNode.Value) switch
                {
                    RelationType.Attribute => () => BuildGraphRecursive(BuildAttrKey(entityName, leafNode.Value), key),
                    RelationType.Permission => () =>
                        BuildGraphRecursive(BuildPermissionKey(entityName, leafNode.Value), key),
                    RelationType.DirectRelation => () =>
                        BuildGraphRecursive(BuildRelationKey(entityName, leafNode.Value), key),
                };

                action();
            }
        }


        void walk(PermissionNode node)
        {
            if (node.Type == PermissionNodeType.Leaf)
            {
                handleLeaf(node.LeafNode!);
            }
            else
            {
                foreach (var child in node.ExpressionNode!.Children)
                {
                    walk(child);
                }
            }
        }

        walk(perm.Tree);
    }

    private void BuildRelationGraphRecursive(string key, string relationName, string entityName)
    {
        var relation = Schema.GetRelations(entityName)
            .First(x => x.Name.Equals(relationName, StringComparison.InvariantCultureIgnoreCase));

        _nodeKeyMap.Add(key, new RelationNode()
        {
            Name = relationName
        });

        foreach (var relationEntity in relation.Entities)
        {
            if (!string.IsNullOrWhiteSpace(relationEntity.Relation))
            {
                BuildGraphRecursive(BuildRelationKey(relationEntity.Type, relationEntity.Relation), key);
            }
            else
            {
                BuildGraphRecursive(BuildEntityKey(relationEntity.Type), key);
            }
        }
    }

    private void BuildAttributeGraphRecursive(string key, string attributeName, string entityName)
    {
        var attr = Schema.GetAttributes(entityName)
            .First(x => x.Name.Equals(attributeName, StringComparison.InvariantCultureIgnoreCase));

        _nodeKeyMap.Add(key, new AttributeNode()
        {
            Name = attr.Name
        });
    }

    private static string BuildAttrKey(string entityName, string attributeName)
    {
        return $"entity:{entityName}:attr:{attributeName}";
    }

    private static string BuildEntityKey(string entityName)
    {
        return $"entity:{entityName}";
    }

    private static string BuildRelationKey(string entityName, string relationName)
    {
        return $"entity:{entityName}:relation:{relationName}";
    }

    private static string BuildPermissionKey(string entityName, string permissionName)
    {
        return $"entity:{entityName}:perm:{permissionName}";
    }
}

public record SchemaGraph
{
    private readonly Schema _schema;
    private Dictionary<string, int> nodeKeyToIndexMap = new();
    private List<ISchemaNode> _nodes = new();
    private readonly bool[][] _matrix;

    public SchemaGraph(Schema schema)
    {
        _schema = schema;
        var (relations, keyMap) = new SchemaRelationsIdentifier(schema).Identify();

        _matrix = new bool[keyMap.Count][];

        var index = 0;
        foreach (var (key, node) in keyMap)
        {
            nodeKeyToIndexMap.Add(key, index);
            _matrix[index] = new bool[keyMap.Count];
            _nodes.Add(node);
            index++;
        }

        foreach (var (nodeA, nodeB) in relations)
        {
            var nodeAIndex = nodeKeyToIndexMap[nodeA];
            var nodeBIndex = nodeKeyToIndexMap[nodeB];

            _matrix[nodeAIndex][nodeBIndex] = true;
        }
    }
}

public record EntityNode : ISchemaNode
{
    public NodeType Type => NodeType.Entity;
    public required string Name { get; init; }
    public List<ISchemaNode> Connections { get; init; } = [];
}

public record AttributeNode : ISchemaNode
{
    public required string Name { get; init; }
    public NodeType Type => NodeType.Attribute;
    public List<ISchemaNode> Connections { get; init; } = [];
}

public record RelationNode : ISchemaNode
{
    public required string Name { get; init; }
    public NodeType Type => NodeType.Relation;
    public List<ISchemaNode> Connections { get; init; } = [];
}

public record PermissionGraphNode : ISchemaNode
{
    public required string Name { get; init; }
    public required PermissionNode ExpressionRoot { get; init; }
    public NodeType Type => NodeType.Permission;
    public List<ISchemaNode> Connections { get; init; } = [];
}

public enum NodeType
{
    Entity,
    Attribute,
    Relation,
    Permission,
    Rule
}