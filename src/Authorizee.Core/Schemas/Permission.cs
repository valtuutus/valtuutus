namespace Authorizee.Core.Schemas;

public enum PermissionNodeType
{
    Leaf,
    Expression
}

public enum PermissionOperation
{
    Intersect,
    Union,
}

public record PermissionNodeLeaf(string Value);

public record PermissionNodeOperation(PermissionOperation Operation, List<PermissionNode> Children);

public record PermissionNode(PermissionNodeType Type)
{
    public PermissionNodeLeaf? LeafNode { get; init; }
    public PermissionNodeOperation? ExpressionNode { get; init; }

    public static PermissionNode Intersect(string left, string right)
    {
        return new PermissionNode(PermissionNodeType.Expression)
            { ExpressionNode = new PermissionNodeOperation(PermissionOperation.Intersect, [Leaf(left), Leaf(right)]) };
    }

    public static PermissionNode Intersect(string left, PermissionNode right)
    {
        return new PermissionNode(PermissionNodeType.Expression)
            { ExpressionNode = new PermissionNodeOperation(PermissionOperation.Intersect, [Leaf(left), right]) };
    }

    public static PermissionNode Union(string left, string right)
    {
        return new PermissionNode(PermissionNodeType.Expression)
            { ExpressionNode = new PermissionNodeOperation(PermissionOperation.Union, [Leaf(left), Leaf(right)]) };
    }
    
    public static PermissionNode Union(params string[] checks)
    {
        var nodes = checks.Select(Leaf).ToList();
        
        return new PermissionNode(PermissionNodeType.Expression)
            { ExpressionNode = new PermissionNodeOperation(PermissionOperation.Union, nodes) };
    }

    public static PermissionNode Union(params PermissionNode[] nodes)
    {
        return new PermissionNode(PermissionNodeType.Expression)
            { ExpressionNode = new PermissionNodeOperation(PermissionOperation.Union, nodes.ToList()) };
    }

    
    public static PermissionNode Leaf(string value)
    {
        return new PermissionNode(PermissionNodeType.Leaf)
        {
            LeafNode = new PermissionNodeLeaf(value)
        };
    }
}

public record Permission(string Name, PermissionNode Tree);