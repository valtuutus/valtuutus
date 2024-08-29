using System.Diagnostics.CodeAnalysis;

namespace Valtuutus.Core.Schemas;

public record FunctionParameter
{
    public required Type ParamType { get; init; }
    public required string ParamName { get; init; }
    public required int ParamOrder { get; init; }
}

public record Function
{
    public string Name { get; init; }
    public List<FunctionParameter> Parameters { get; init; }
    public Func<IDictionary<string, object?>, bool> Lambda { get; init; }
    
    public Function(string name, List<FunctionParameter> parameters, Func<IDictionary<string, object?>, bool> lambda)
    {
        Name = name;
        Parameters = parameters;
        Lambda = lambda;
    }

    public bool Execute(IDictionary<string, object?> arguments)
    {
        return Lambda(arguments);
    }
}