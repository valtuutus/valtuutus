namespace Valtuutus.Core.Engines.Check;

public enum CheckNodeType
{
    Permission,
    Expression,
    Relation,
    TupleToUserSet,
    Attribute,
    Function
}

public sealed class CheckNode
{
    public CheckNodeType Type { get; internal set; }
    public required string Name { get; init; }
    public bool Result { get; internal set; }
    public string? Detail { get; internal set; }
    public IReadOnlyList<CheckNode> Children => _children;
    internal readonly List<CheckNode> _children = [];
}

public sealed class CheckExplainResult
{
    public required bool Result { get; init; }
    public required CheckNode Root { get; init; }
}
