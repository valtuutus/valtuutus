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
    Expression
}

public record PermissionNodeLeaf(PermissionNodeLeafType Type)
{
    public PermissionNodeLeafPermission? PermissionNode { get; init; }
    public PermissionNodeLeafExp? ExpressionNode { get; init; }
}

public record PermissionNodeLeafPermission(string Permission);

public enum AttributeTypes
{
    Int,
    String,
    Decimal
}

public enum PermissionNodeExpArgumentType
{
    Attribute,
    ContextAccess,
    Literal
}

public abstract record PermissionNodeExpArgument
{
    public abstract PermissionNodeExpArgumentType Type { get; }
    public required int ArgOrder { get; init; }
}

public record PermissionNodeExpArgumentAttribute : PermissionNodeExpArgument 
{
    public override PermissionNodeExpArgumentType Type => PermissionNodeExpArgumentType.Attribute;
    public required string AttributeName { get; init; }
}

public record PermissionNodeExpArgumentContextAccess : PermissionNodeExpArgument
{
    public override PermissionNodeExpArgumentType Type => PermissionNodeExpArgumentType.ContextAccess;
    public required string ContextPropertyName { get; init; }
}

public enum LiteralType
{
    String,
    Int,
    Decimal,
}

public abstract record PermissionNodeExpArgumentLiteral : PermissionNodeExpArgument
{
    public override PermissionNodeExpArgumentType Type => PermissionNodeExpArgumentType.ContextAccess;
    public abstract LiteralType LiteralType { get; }
}

public record PermissionNodeExpArgumentStringLiteral : PermissionNodeExpArgumentLiteral
{
    public override LiteralType LiteralType => LiteralType.String;
    public required string Value { get; init; }
}

public record PermissionNodeExpArgumentIntLiteral : PermissionNodeExpArgumentLiteral
{
    public override LiteralType LiteralType => LiteralType.Int;
    public required int Value { get; init; }
}

public record PermissionNodeExpArgumentDecimalLiteral : PermissionNodeExpArgumentLiteral
{
    public override LiteralType LiteralType => LiteralType.Decimal;
    public required decimal Value { get; init; }
}

public record PermissionNodeLeafExp(string FunctionName, PermissionNodeExpArgument[] Args)
{
    public string[] GetArgsAttributesNames() => Args
        .Where(a => a.Type == PermissionNodeExpArgumentType.Attribute)
        .Cast<PermissionNodeExpArgumentAttribute>()
        .Select(x => x.AttributeName)
        .ToArray();
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

    public static PermissionNode Expression(string functionName, PermissionNodeExpArgument[] args)
    {
        return new PermissionNode(PermissionNodeType.Leaf)
        {
            LeafNode = new PermissionNodeLeaf(PermissionNodeLeafType.Expression) { ExpressionNode = new PermissionNodeLeafExp(functionName, args) }
        };
    }
}

public record Permission(string Name, PermissionNode Tree);