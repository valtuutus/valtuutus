using System.Diagnostics.CodeAnalysis;

namespace Valtuutus.Core.Schemas;

public record Function
{
    public required string Name { get; init; }
    public required IDictionary<string, Type> Parameters { get; init; }
    public required Func<IDictionary<string, object>, bool> Lambda { get; init; }

    [SetsRequiredMembers]
    public Function(string name, IDictionary<string, Type> parameters, Func<IDictionary<string, object>, bool> lambda)
    {
        Name = name;
        Parameters = parameters;
        Lambda = lambda;
    }

    public bool Execute(IDictionary<string, object> arguments)
    {
        return Lambda(arguments);
    }
}