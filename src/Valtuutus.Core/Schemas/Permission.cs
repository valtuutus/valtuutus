using Valtuutus.Core.Lang;

namespace Valtuutus.Core.Schemas;

internal enum PermissionNodeType
{
    Leaf,
    Expression
}

internal enum PermissionOperation
{
    Intersect,
    Union,
}

internal enum PermissionNodeLeafType
{
    Permission,
    Expression
}

internal record PermissionNodeLeaf(PermissionNodeLeafType Type)
{
    public PermissionNodeLeafPermission? PermissionNode { get; init; }
    public PermissionNodeLeafExp? ExpressionNode { get; init; }
}

internal record PermissionNodeLeafPermission(string Permission);

internal enum PermissionNodeExpArgumentType
{
    Attribute,
    ContextAccess,
    Literal
}

internal abstract record PermissionNodeExpArgument
{
    public abstract PermissionNodeExpArgumentType Type { get; }
    public required int ArgOrder { get; init; }
}

internal record PermissionNodeExpArgumentAttribute : PermissionNodeExpArgument 
{
    public override PermissionNodeExpArgumentType Type => PermissionNodeExpArgumentType.Attribute;
    public required string AttributeName { get; init; }
}

internal record PermissionNodeExpArgumentContextAccess : PermissionNodeExpArgument
{
    public override PermissionNodeExpArgumentType Type => PermissionNodeExpArgumentType.ContextAccess;
    public required string ContextPropertyName { get; init; }
}

internal abstract record PermissionNodeExpArgumentLiteral : PermissionNodeExpArgument
{
    public override PermissionNodeExpArgumentType Type => PermissionNodeExpArgumentType.ContextAccess;
    public abstract LangType LiteralType { get; }
}

internal record PermissionNodeExpArgumentStringLiteral : PermissionNodeExpArgumentLiteral
{
    public override LangType LiteralType => LangType.String;
    public required string Value { get; init; }
}

internal record PermissionNodeExpArgumentIntLiteral : PermissionNodeExpArgumentLiteral
{
    public override LangType LiteralType => LangType.Int;
    public required int Value { get; init; }
}

internal record PermissionNodeExpArgumentDecimalLiteral : PermissionNodeExpArgumentLiteral
{
    public override LangType LiteralType => LangType.Decimal;
    public required decimal Value { get; init; }
}

internal record PermissionNodeExpArgumentBooleanLiteral : PermissionNodeExpArgumentLiteral
{
    public override LangType LiteralType => LangType.Boolean;
    public required bool Value { get; init; }
}

internal record PermissionNodeLeafExp(string FunctionName, PermissionNodeExpArgument[] Args)
{
    internal string[] GetArgsAttributesNames() => Args
        .Where(a => a.Type == PermissionNodeExpArgumentType.Attribute)
        .Cast<PermissionNodeExpArgumentAttribute>()
        .Select(x => x.AttributeName)
        .ToArray();
}

internal record PermissionNodeOperation(PermissionOperation Operation, List<PermissionNode> Children);

internal record PermissionNode(PermissionNodeType Type)
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

public record Permission
{
    internal Permission(string Name, PermissionNode Tree)
    {
        this.Name = Name;
        this.Tree = Tree;
    }

    public string Name { get; init; }
    internal PermissionNode Tree { get; init; }
}