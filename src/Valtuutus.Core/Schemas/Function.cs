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
    internal FunctionExecutor Lambda { get; init; }

    internal Function(string name, List<FunctionParameter> parameters, FunctionExecutor lambda)
    {
        Name = name;
        Parameters = parameters;
        Lambda = lambda;
    }

    public bool Execute(ReadOnlySpan<LiteralValueUnion> arguments) => Lambda(arguments);

    /// <summary>
    /// Builds this function's positional argument buffer directly from Parameters' ParamOrder --
    /// replaces the old two-step CreateParamToArgMap (FunctionParameter -&gt; PermissionNodeExpArgument
    /// pooled dict) + ToLambdaArgs (string-keyed, object?-boxing pooled dict) pipeline with one pass
    /// into an ArrayPool-backed buffer. Caller disposes the returned PooledLiteralValueArray and
    /// passes its Span to Lambda.
    /// </summary>
    internal PooledLiteralValueArray BuildArgs<TState>(
        IList<PermissionNodeExpArgument> args,
        Func<PermissionNodeExpArgumentAttribute, LangType, TState, LiteralValueUnion> attrValueMapper,
        TState state,
        IDictionary<string, object> context)
    {
        var pooled = PooledLiteralValueArray.Rent(Parameters.Count);
        var span = pooled.Span;

        foreach (var parameter in Parameters)
        {
            var arg = args[parameter.ParamOrder];
            span[parameter.ParamOrder] = arg switch
            {
                PermissionNodeExpArgumentAttribute a => attrValueMapper(a, parameter.ParamType, state),
                PermissionNodeExpArgumentStringLiteral s => new LiteralValueUnion { LiteralType = LangType.String, StringValue = s.Value },
                PermissionNodeExpArgumentIntLiteral i => new LiteralValueUnion { LiteralType = LangType.Int, IntValue = i.Value },
                PermissionNodeExpArgumentDecimalLiteral d => new LiteralValueUnion { LiteralType = LangType.Decimal, DecimalValue = d.Value },
                PermissionNodeExpArgumentBooleanLiteral b => new LiteralValueUnion { LiteralType = LangType.Boolean, BooleanValue = b.Value },
                PermissionNodeExpArgumentContextAccess c => FromContextValue(context[c.ContextPropertyName], parameter.ParamType),
                _ => throw new NotSupportedException("Unsupported argument type.")
            };
        }

        return pooled;
    }

    private static LiteralValueUnion FromContextValue(object value, LangType type) => type switch
    {
        LangType.String => new LiteralValueUnion { LiteralType = LangType.String, StringValue = (string)value },
        LangType.Int => new LiteralValueUnion { LiteralType = LangType.Int, IntValue = (int)value },
        LangType.Decimal => new LiteralValueUnion { LiteralType = LangType.Decimal, DecimalValue = (decimal)value },
        LangType.Boolean => new LiteralValueUnion { LiteralType = LangType.Boolean, BooleanValue = (bool)value },
        _ => throw new NotSupportedException("Unsupported type for LiteralValueUnion")
    };
}
