namespace Authorizee.Core.Schemas;

public interface ISchemaNode
{
    public string Name { get; }
    public NodeType Type { get; }
}

public class SchemaRelationsIdentifier(Schema Schema)
{
    private readonly List<(string, string)> _relations = new();
    private readonly Dictionary<string, (NodeType type, string key)> _permToNodeKeyAndType = new();
    private readonly Dictionary<string, ISchemaNode> _nodeKeyMap = new();

    public (List<(string, string)>, Dictionary<string, ISchemaNode>, Dictionary<string, (NodeType type, string key)>) Identify()
    {
        foreach (var entity in Schema.Entities)
        {
            BuildGraphRecursive(BuildEntityKey(entity.Name), null);
        }

        return (_relations, _nodeKeyMap, _permToNodeKeyAndType);
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

        _permToNodeKeyAndType.Add($"{entityName}.{permissionName}", (NodeType.Permission, key));

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

        _permToNodeKeyAndType.Add($"{entityName}.{relationName}", (NodeType.Relation, key));

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

        _permToNodeKeyAndType.Add($"{entityName}.{attributeName}", (NodeType.Attribute, key));
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
    private Dictionary<string, int> _nodeKeyToIndexMap = new();
    private readonly Dictionary<string, (NodeType type, string key)> _permNameKeyMap;
    private List<ISchemaNode> _nodes = new();
    private readonly bool[][] _matrix;

    public SchemaGraph(Schema schema)
    {
        _schema = schema;
        var (relations, keyMap, permNameKeyMap) = new SchemaRelationsIdentifier(schema).Identify();
        _permNameKeyMap = permNameKeyMap; 

        _matrix = new bool[keyMap.Count][];

        var index = 0;
        foreach (var (key, node) in keyMap)
        {
            _nodeKeyToIndexMap.Add(key, index);
            _matrix[index] = new bool[keyMap.Count];
            _nodes.Add(node);
            index++;
        }

        foreach (var (nodeA, nodeB) in relations)
        {
            var nodeAIndex = _nodeKeyToIndexMap[nodeA];
            var nodeBIndex = _nodeKeyToIndexMap[nodeB];

            _matrix[nodeAIndex][nodeBIndex] = true;
        }
    }

    public (NodeType, ISchemaNode)? GetItem(string entityName, string permission)
    {
        if (!_permNameKeyMap.TryGetValue($"{entityName}.{permission}", out var tuple))
        {
            return null;
        }

        var nodeIndex = _nodeKeyToIndexMap[tuple.key];

        return (tuple.type, _nodes[nodeIndex]);
    }
    
    public NodeType? GetItemType(string entityName, string permission)
    {
        if (!_permNameKeyMap.TryGetValue($"{entityName}.{permission}", out var tuple))
        {
            return null;
        }
        
        return tuple.type;
    }

    public HashSet<ISchemaNode> GetRelatedItems(string entityName, string permission, string subjectType)
    {
        var relations = new HashSet<(int, int)>();
        var visited = new HashSet<int>();
        
        bool walk(int nodeIndex, int? parentIndex = null)
        {
            var node = _nodes[nodeIndex];
            
            if (node.Name == subjectType)
            {
                Console.WriteLine($"node is of subject type {subjectType}");
                return true;
            }
            
            if (!visited.Add(nodeIndex))
            {
                Console.WriteLine($"Already visited node {nodeIndex}");
                return false;
            }

            if (parentIndex.HasValue)
            {
                relations.Add((nodeIndex, parentIndex.Value));
            }

            var row = _matrix[nodeIndex];

            if (node.Type != NodeType.Permission)
                return false;
            
            var res = false;
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i])
                {
                    Console.WriteLine($"Expanding node index {i}, {_nodes[i].Type}:{_nodes[i].Name}");
                    if (!walk(i, nodeIndex))
                    {
                        Console.WriteLine($"Adding node index {i}, {_nodes[i].Type}:{_nodes[i].Name}");
                        res = true;
                    }
                }
            }

            return res;
        }
        
        if (!_permNameKeyMap.TryGetValue($"{entityName}.{permission}", out var key))
        {
            return new HashSet<ISchemaNode>();
        }
            
        var permIndex = _nodeKeyToIndexMap[key.key];

        walk(permIndex);

        foreach (var (child, parent) in relations)
        {
            Console.WriteLine($"{_nodes[parent].Name} ---> {_nodes[child].Name}");
        }

        return new HashSet<ISchemaNode>();
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
}

public record RelationNode : ISchemaNode
{
    public required string Name { get; init; }
    public NodeType Type => NodeType.Relation;
}

public record PermissionGraphNode : ISchemaNode
{
    public required string Name { get; init; }
    public required PermissionNode ExpressionRoot { get; init; }
    public NodeType Type => NodeType.Permission;
}

public enum NodeType
{
    Entity,
    Attribute,
    Relation,
    Permission,
    Rule
}