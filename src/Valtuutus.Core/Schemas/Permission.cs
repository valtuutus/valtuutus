namespace Valtuutus.Core.Schemas;

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

public enum PermissionNodeLeafType
{
    Permission,
    AttributeExpression
}

public record PermissionNodeLeaf(PermissionNodeLeafType Type)
{
    public PermissionNodeLeafPermission? PermissionNode { get; init; }
    public PermissionNodeLeafAttributeExp? ExpressionNode { get; init; }
}

public record PermissionNodeLeafPermission(string Permission);

public enum AttributeTypes
{
    Int,
    String,
    Decimal
}

public record PermissionNodeLeafAttributeExp(string AttributeName, AttributeTypes Type)
{
    public Func<int, bool>? IntExpression { get; init; }
    public Func<string, bool>? StringExpression { get; init; }
    public Func<decimal, bool>? DecimalExpression { get; init; }
}

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
    
    public static PermissionNode Intersect(params PermissionNode[] nodes)
    {
        return new PermissionNode(PermissionNodeType.Expression)
            { ExpressionNode = new PermissionNodeOperation(PermissionOperation.Intersect, nodes.ToList()) };
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
    
    public static PermissionNode Leaf(string permName)
    {
        return new PermissionNode(PermissionNodeType.Leaf)
        {
            LeafNode = new PermissionNodeLeaf(PermissionNodeLeafType.Permission)
            {
                PermissionNode = new PermissionNodeLeafPermission(permName)
            }
        };
    }

    public static PermissionNode AttributeIntExpression(string attrName, Func<int, bool> exp)
    {
        return new PermissionNode(PermissionNodeType.Leaf)
        {
            LeafNode = new PermissionNodeLeaf(PermissionNodeLeafType.AttributeExpression)
            {
                ExpressionNode = new PermissionNodeLeafAttributeExp(attrName, AttributeTypes.Int)
                {
                    IntExpression = exp
                }
            }
        };
    }

    public static PermissionNode AttributeStringExpression(string attrName, Func<string, bool> exp)
    {
        return new PermissionNode(PermissionNodeType.Leaf)
        {
            LeafNode = new PermissionNodeLeaf(PermissionNodeLeafType.AttributeExpression)
            {
                ExpressionNode = new PermissionNodeLeafAttributeExp(attrName, AttributeTypes.String)
                {
                    StringExpression = exp
                }
            }
        };
    }

    public static PermissionNode AttributeDecimalExpression(string attrName, Func<decimal, bool> exp)
    {
        return new PermissionNode(PermissionNodeType.Leaf)
        {
            LeafNode = new PermissionNodeLeaf(PermissionNodeLeafType.AttributeExpression)
            {
                ExpressionNode = new PermissionNodeLeafAttributeExp(attrName, AttributeTypes.Decimal)
                {
                    DecimalExpression = exp
                }
            }
        };
    }
}

public record Permission(string Name, PermissionNode Tree);