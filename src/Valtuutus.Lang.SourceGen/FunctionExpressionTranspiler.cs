using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;

namespace Valtuutus.Lang.SourceGen;

public enum FunctionParamType
{
    Int,
    String,
    Decimal,
    Boolean
}

public sealed record FunctionTranspileParameter(string Name, FunctionParamType Type);

public sealed record FunctionTranspileResult
{
    public bool Success { get; init; }
    public string? FunctionName { get; init; }
    public IReadOnlyList<FunctionTranspileParameter> Parameters { get; init; } = System.Array.Empty<FunctionTranspileParameter>();
    public string? BodyExpression { get; init; }
    public string? Error { get; init; }

    public static FunctionTranspileResult Ok(string functionName, IReadOnlyList<FunctionTranspileParameter> parameters, string body) =>
        new() { Success = true, FunctionName = functionName, Parameters = parameters, BodyExpression = body };

    public static FunctionTranspileResult Fail(string error) =>
        new() { Success = false, Error = error };
}

internal sealed class FunctionTranspileException : System.Exception
{
    public FunctionTranspileException(string message) : base(message) { }
}

public static class FunctionExpressionTranspiler
{
    public static FunctionTranspileResult Transpile(ValtuutusParser.FunctionDefinitionContext funcCtx)
    {
        var functionName = funcCtx.ID().GetText();

        try
        {
            var parameters = new List<FunctionTranspileParameter>();
            var paramTypes = new Dictionary<string, FunctionParamType>();

            var ids = funcCtx.parameterList().ID();
            var types = funcCtx.parameterList().type();
            for (int i = 0; i < ids.Length; i++)
            {
                var paramName = ids[i].GetText();
                var paramType = ToFunctionParamType(types[i].GetText());
                parameters.Add(new FunctionTranspileParameter(paramName, paramType));
                paramTypes[paramName] = paramType;
            }

            var body = EmitBooleanExpression(paramTypes, funcCtx.functionBody().functionExpression());
            return FunctionTranspileResult.Ok(functionName, parameters, body);
        }
        catch (FunctionTranspileException ex)
        {
            return FunctionTranspileResult.Fail($"Function '{functionName}': {ex.Message}");
        }
    }

    private static FunctionParamType ToFunctionParamType(string typeText) => typeText switch
    {
        "int" => FunctionParamType.Int,
        "string" => FunctionParamType.String,
        "bool" => FunctionParamType.Boolean,
        "decimal" => FunctionParamType.Decimal,
        _ => throw new FunctionTranspileException($"Unknown parameter type '{typeText}'")
    };

    private static string EmitBooleanExpression(IReadOnlyDictionary<string, FunctionParamType> paramTypes,
        ValtuutusParser.FunctionExpressionContext exprCtx)
    {
        return exprCtx switch
        {
            ValtuutusParser.EqualityExpressionContext eqCtx => EmitComparison(paramTypes, "==", eqCtx.functionExpression(0), eqCtx.functionExpression(1)),
            ValtuutusParser.InequalityExpressionContext neqCtx => EmitComparison(paramTypes, "!=", neqCtx.functionExpression(0), neqCtx.functionExpression(1)),
            ValtuutusParser.GreaterExpressionContext gtCtx => EmitOrderingComparison(paramTypes, ">", gtCtx.functionExpression(0), gtCtx.functionExpression(1)),
            ValtuutusParser.LessExpressionContext ltCtx => EmitOrderingComparison(paramTypes, "<", ltCtx.functionExpression(0), ltCtx.functionExpression(1)),
            ValtuutusParser.GreaterOrEqualExpressionContext gteCtx => EmitOrderingComparison(paramTypes, ">=", gteCtx.functionExpression(0), gteCtx.functionExpression(1)),
            ValtuutusParser.LessOrEqualExpressionContext lteCtx => EmitOrderingComparison(paramTypes, "<=", lteCtx.functionExpression(0), lteCtx.functionExpression(1)),
            ValtuutusParser.AndExpressionContext andCtx => $"({EmitBooleanExpression(paramTypes, andCtx.functionExpression(0))}) && ({EmitBooleanExpression(paramTypes, andCtx.functionExpression(1))})",
            ValtuutusParser.OrExpressionContext orCtx => $"({EmitBooleanExpression(paramTypes, orCtx.functionExpression(0))}) || ({EmitBooleanExpression(paramTypes, orCtx.functionExpression(1))})",
            ValtuutusParser.NotExpressionContext notCtx => $"!({EmitBooleanExpression(paramTypes, notCtx.functionExpression())})",
            // no extra wrap: callers of a boolean sub-expression already parenthesize it
            ValtuutusParser.ParenthesisExpressionContext parenCtx => EmitBooleanExpression(paramTypes, parenCtx.functionExpression()),
            ValtuutusParser.IdentifierExpressionContext or ValtuutusParser.LiteralExpressionContext => EmitImplicitBooleanEquality(paramTypes, exprCtx),
            _ => throw new FunctionTranspileException("Unsupported function expression type")
        };
    }

    private static (string Left, string Right, FunctionParamType Type) EmitValidatedOperandPair(
        IReadOnlyDictionary<string, FunctionParamType> paramTypes,
        ValtuutusParser.FunctionExpressionContext leftCtx, ValtuutusParser.FunctionExpressionContext rightCtx)
    {
        var (leftCode, leftType) = EmitOperandExpression(paramTypes, leftCtx);
        var (rightCode, rightType) = EmitOperandExpression(paramTypes, rightCtx);

        if (leftType != rightType)
        {
            throw new FunctionTranspileException(
                $"Incompatible types, comparing {leftType} and {rightType}");
        }

        return (leftCode, rightCode, leftType);
    }

    private static string EmitComparison(IReadOnlyDictionary<string, FunctionParamType> paramTypes,
        string op, ValtuutusParser.FunctionExpressionContext leftCtx, ValtuutusParser.FunctionExpressionContext rightCtx)
    {
        var (leftCode, rightCode, _) = EmitValidatedOperandPair(paramTypes, leftCtx, rightCtx);

        return $"({leftCode}) {op} ({rightCode})";
    }

    private static string EmitOrderingComparison(IReadOnlyDictionary<string, FunctionParamType> paramTypes,
        string op, ValtuutusParser.FunctionExpressionContext leftCtx, ValtuutusParser.FunctionExpressionContext rightCtx)
    {
        var (leftCode, rightCode, leftType) = EmitValidatedOperandPair(paramTypes, leftCtx, rightCtx);

        return leftType switch
        {
            FunctionParamType.Int or FunctionParamType.Decimal =>
                $"global::System.Nullable.Compare({leftCode}, {rightCode}) {op} 0",
            FunctionParamType.String =>
                $"string.Compare({leftCode}, {rightCode}, global::System.StringComparison.Ordinal) {op} 0",
            FunctionParamType.Boolean =>
                throw new FunctionTranspileException("Ordering comparisons are not supported for boolean operands"),
            _ => throw new FunctionTranspileException($"Unsupported operand type {leftType}")
        };
    }

    private static string EmitImplicitBooleanEquality(IReadOnlyDictionary<string, FunctionParamType> paramTypes,
        ValtuutusParser.FunctionExpressionContext exprCtx)
    {
        if (exprCtx is ValtuutusParser.LiteralExpressionContext litCtx)
        {
            if (litCtx.literal().BOOLEAN_LITERAL() == null)
            {
                throw new FunctionTranspileException($"Expected boolean literal, got {litCtx.literal().GetText()}");
            }

            var (code, _) = EmitLiteral(litCtx.literal());
            return $"({code}) == (true)";
        }

        if (exprCtx is ValtuutusParser.IdentifierExpressionContext idCtx)
        {
            var (code, type) = EmitParameterRef(paramTypes, idCtx);
            if (type != FunctionParamType.Boolean)
            {
                throw new FunctionTranspileException($"Expected boolean parameter, got {type}");
            }

            return $"({code}) == (true)";
        }

        throw new FunctionTranspileException("Unsupported function expression type");
    }

    private static (string Code, FunctionParamType Type) EmitOperandExpression(
        IReadOnlyDictionary<string, FunctionParamType> paramTypes,
        ValtuutusParser.FunctionExpressionContext exprCtx)
    {
        return exprCtx switch
        {
            ValtuutusParser.IdentifierExpressionContext idCtx => EmitParameterRef(paramTypes, idCtx),
            ValtuutusParser.LiteralExpressionContext litCtx => EmitLiteral(litCtx.literal()),
            _ => throw new FunctionTranspileException("Expected parameter or literal operand")
        };
    }

    private static (string Code, FunctionParamType Type) EmitParameterRef(
        IReadOnlyDictionary<string, FunctionParamType> paramTypes,
        ValtuutusParser.IdentifierExpressionContext idCtx)
    {
        var id = idCtx.ID().GetText();
        if (!paramTypes.TryGetValue(id, out var type))
        {
            throw new FunctionTranspileException($"'{id}' is not defined in the function context.");
        }

        // Schema DSL parameter names may collide with C# reserved keywords (e.g. "class").
        // Verbatim-escape unconditionally rather than special-casing detected keywords.
        return ($"@{id}", type);
    }

    private static (string Code, FunctionParamType Type) EmitLiteral(ValtuutusParser.LiteralContext literalCtx)
    {
        if (literalCtx.STRING_LITERAL() != null)
        {
            var value = literalCtx.STRING_LITERAL().GetText().Trim('"');
            return (SymbolDisplay.FormatLiteral(value, true), FunctionParamType.String);
        }

        if (literalCtx.INT_LITERAL() != null)
        {
            return (literalCtx.INT_LITERAL().GetText(), FunctionParamType.Int);
        }

        if (literalCtx.DECIMAL_LITERAL() != null)
        {
            return (literalCtx.DECIMAL_LITERAL().GetText() + "m", FunctionParamType.Decimal);
        }

        if (literalCtx.BOOLEAN_LITERAL() != null)
        {
            return (literalCtx.BOOLEAN_LITERAL().GetText(), FunctionParamType.Boolean);
        }

        throw new FunctionTranspileException("Unknown literal");
    }
}
