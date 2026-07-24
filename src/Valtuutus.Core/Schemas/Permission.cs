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
    Negate,
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

internal sealed class PermissionNodeLeafPermission
{
    public string Permission { get; }
    public string? UserSet { get; }
    public string? ComputedUserSet { get; }
    public bool IsIndirect { get; }

    public PermissionNodeLeafPermission(string permission)
    {
        Permission = permission;
        var i = permission.IndexOf('.');
        if (i > 0 && i < permission.Length - 1)
        {
            UserSet = permission[..i];
            ComputedUserSet = permission[(i + 1)..];
            IsIndirect = true;
        }
    }
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
    public override PermissionNodeExpArgumentType Type => PermissionNodeExpArgumentType.Literal;
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

public record PermissionNodeLeafExp(string FunctionName, PermissionNodeExpArgument[] Args)
{
    internal string[] AttributeArgNames { get; } = Args
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

    public static PermissionNode Negate(PermissionNode child)
    {
        return new PermissionNode(PermissionNodeType.Expression)
            { ExpressionNode = new PermissionNodeOperation(PermissionOperation.Negate, [child]) };
    }

    /// <summary>
    /// Collapses nested same-operator Union/Intersect chains (e.g. the binary tree the DSL
    /// parser produces for "a or b or c") into a single n-ary node, bottom-up. Each flattened
    /// level splices an already-flattened same-operator child's children directly into the
    /// parent, so arbitrarily deep chains collapse in one pass.
    /// </summary>
    internal static PermissionNode Flatten(PermissionNode node)
    {
        if (node.Type != PermissionNodeType.Expression) return node;
        var expr = node.ExpressionNode!;

        if (expr.Operation == PermissionOperation.Negate)
        {
            var flatChild = Flatten(expr.Children[0]);
            return ReferenceEquals(flatChild, expr.Children[0]) ? node : Negate(flatChild);
        }

        var op = expr.Operation;
        var flattenedChildren = new List<PermissionNode>(expr.Children.Count);
        var changed = false;
        foreach (var child in expr.Children)
        {
            var flatChild = Flatten(child);
            if (flatChild.Type == PermissionNodeType.Expression && flatChild.ExpressionNode!.Operation == op)
            {
                flattenedChildren.AddRange(flatChild.ExpressionNode.Children);
                changed = true;
            }
            else
            {
                flattenedChildren.Add(flatChild);
                if (!ReferenceEquals(flatChild, child)) changed = true;
            }
        }

        if (!changed) return node;
        return new PermissionNode(PermissionNodeType.Expression)
            { ExpressionNode = new PermissionNodeOperation(op, flattenedChildren) };
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