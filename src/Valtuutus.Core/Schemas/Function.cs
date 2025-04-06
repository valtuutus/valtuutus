using System.Diagnostics.CodeAnalysis;
using Valtuutus.Core.Lang;
using Valtuutus.Core.Pools;

namespace Valtuutus.Core.Schemas;

public record FunctionParameter
{
    public required LangType ParamType { get; init; }
    public required string ParamName { get; init; }
    public required int ParamOrder { get; init; }
}

public record Function
{
    public string Name { get; init; }
    public List<FunctionParameter> Parameters { get; init; }
    internal Func<IDictionary<string, object?>, bool> Lambda { get; init; }
    
    internal Function(string name, List<FunctionParameter> parameters, Func<IDictionary<string, object?>, bool> lambda)
    {
        Name = name;
        Parameters = parameters;
        Lambda = lambda;
    }
    
    internal PooledDictionary<FunctionParameter, PermissionNodeExpArgument> CreateParamToArgMap(IList<PermissionNodeExpArgument> args)
    {
        var pooled = PooledDictionary<FunctionParameter, PermissionNodeExpArgument>.Rent();

        foreach (var parameter in Parameters)
        {
            pooled.Dictionary[parameter] = args.First(a => a.ArgOrder == parameter.ParamOrder);
        }

        return pooled;
    }

    public bool Execute(IDictionary<string, object?> arguments)
    {
        return Lambda(arguments);
    }
}

internal static class ParamToArgMapExtensions
{
    public static PooledDictionary<string, object?> ToLambdaArgs(
        this PooledDictionary<FunctionParameter, PermissionNodeExpArgument> map,
        Func<PermissionNodeExpArgumentAttribute, object?> attrValueMapper,
        IDictionary<string, object> context)
    {
        var pooled = PooledDictionary<string, object?>.Rent();

        foreach (var pair in map.Dictionary)
        {
            object? value = pair.Value switch
            {
                PermissionNodeExpArgumentAttribute arg => attrValueMapper(arg),
                PermissionNodeExpArgumentStringLiteral arg => arg.Value,
                PermissionNodeExpArgumentIntLiteral arg => arg.Value,
                PermissionNodeExpArgumentDecimalLiteral arg => arg.Value,
                PermissionNodeExpArgumentContextAccess arg => context[arg.ContextPropertyName],
                _ => throw new NotSupportedException("Unsupported argument type.")
            };

            pooled.Dictionary[pair.Key.ParamName] = value;
        }

        return pooled;
    }
}