namespace Authorizee.Core.Schemas;

public record Rule
{
    public required string Name { get; init; }
    public required string RuleFn { get; init; }
}