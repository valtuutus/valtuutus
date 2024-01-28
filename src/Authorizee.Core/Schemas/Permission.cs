namespace Authorizee.Core.Schemas;

public record PermissionNode(string Value)
{
    public PermissionNode? Left { get; init; }
    public PermissionNode? Right { get; init; }

    public const string AND = "AND";
    public const string OR = "OR";

    public static PermissionNode And(string left, string right)
    {
        return new PermissionNode(AND)
        {
            Left = new PermissionNode(left),
            Right = new PermissionNode(right)
        };
    }
    
    public static PermissionNode And(string left, PermissionNode right)
    {
        return new PermissionNode(AND)
        {
            Left = new PermissionNode(left),
            Right = right
        };
    }
    
    public static PermissionNode Or(string left, string right)
    {
        return new PermissionNode(OR)
        {
            Left = new PermissionNode(left),
            Right = new PermissionNode(right)
        };
    }
}

public record Permission(string Name, PermissionNode PermissionTree);