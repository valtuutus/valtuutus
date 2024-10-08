﻿using System.Diagnostics.CodeAnalysis;
using Valtuutus.Core.Lang;

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
    
    internal IDictionary<FunctionParameter, PermissionNodeExpArgument> CreateParamToArgMap(IList<PermissionNodeExpArgument> args)
    {
        return Parameters
            .Aggregate(new Dictionary<FunctionParameter, PermissionNodeExpArgument>(), (arguments, parameter) =>
            {
                arguments.Add(parameter, args.First(a => a.ArgOrder == parameter.ParamOrder));
                return arguments;
            });
    }

    public bool Execute(IDictionary<string, object?> arguments)
    {
        return Lambda(arguments);
    }
}

internal static class ParamToArgMapExtensions
{
    public static IDictionary<string, object?> ToLambdaArgs(
        this IDictionary<FunctionParameter, PermissionNodeExpArgument> map,
        Func<PermissionNodeExpArgumentAttribute, object?> attrValueMapper,
        IDictionary<string, object> context)
    {
        return map.ToDictionary(
            pair => pair.Key.ParamName,
            pair =>
            {
                return pair.Value switch
                {
                    PermissionNodeExpArgumentAttribute arg => attrValueMapper(arg),
                    PermissionNodeExpArgumentStringLiteral arg => arg.Value,
                    PermissionNodeExpArgumentIntLiteral arg => arg.Value,
                    PermissionNodeExpArgumentDecimalLiteral arg => arg.Value,
                    PermissionNodeExpArgumentContextAccess arg => context[arg.ContextPropertyName],
                    _ => throw new NotSupportedException("Unsuported argument type.")
                };
            }
        );
    }
}