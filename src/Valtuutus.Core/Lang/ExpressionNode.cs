using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Valtuutus.Lang;

namespace Valtuutus.Core.Lang;

internal abstract record FunctionNode<T>
{
    internal abstract Expression<FunctionNodeExecutor<T>> GetExpression(ParameterExpression args);
    internal abstract LangType TypeContext { get; }
}

internal abstract record UnaryFunctionNode<T> : FunctionNode<T>
{
    internal FunctionNode<T> Child { get; set; } = null!;
}

public enum LangType
{
    Int,
    String,
    Decimal,
    Boolean
}

internal static class LangTypeExtensions
{
    internal static LangType ToLangType(this  ValtuutusParser.TypeContext type)
    {
        return type.GetText() switch
        {
            "string" => LangType.String,
            "int" => LangType.Int,
            "bool" => LangType.Boolean,
            "decimal" => LangType.Decimal,
            _ => throw new InvalidOperationException("Unknown Language Type")
        };
    }

    internal static string ToTypeString(this LangType langType)
    {
        return langType switch
        {
            LangType.String => "string",
            LangType.Int => "int",
            LangType.Boolean => "bool",
            LangType.Decimal => "decimal",
            _ => throw new InvalidOperationException("Unknown Language Type")
        };
    }

    internal static Type ToClrType(this LangType langType)
    {
        return langType switch
        {
            LangType.String => typeof(string),
            LangType.Int => typeof(int),
            LangType.Boolean => typeof(bool),
            LangType.Decimal => typeof(decimal),
            _ => throw new InvalidOperationException("Unknown Language Type")
        };
    }
}

internal abstract record LeafFunctionNode : FunctionNode<LiteralValueUnion>
{
}

internal record StringLiteralFnNode : LeafFunctionNode
{
    internal override LangType TypeContext => LangType.String;
    internal required string Value { get; init; }

    internal override Expression<FunctionNodeExecutor<LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LangType.String, StringValue = Value };
        return Expression.Lambda<FunctionNodeExecutor<LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }
}

internal record IntegerLiteralFnNode : LeafFunctionNode
{
    internal override Expression<FunctionNodeExecutor<LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LangType.Int, IntValue = Value };
        return Expression.Lambda<FunctionNodeExecutor<LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }

    internal override LangType TypeContext => LangType.Int;
    internal int Value { get; init; }
}

internal record DecimalLiteralFnNode : LeafFunctionNode
{
    internal override LangType TypeContext => LangType.Decimal;
    internal required decimal Value { get; init; }

    internal override Expression<FunctionNodeExecutor<LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LangType.Decimal, DecimalValue = Value };
        return Expression.Lambda<FunctionNodeExecutor<LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }
}

internal record BooleanLiteralFnNode : LeafFunctionNode
{
    internal override LangType TypeContext => LangType.Boolean;
    internal required bool Value { get; init; }

    internal override Expression<FunctionNodeExecutor<LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LangType.Boolean, BooleanValue = Value };
        return Expression.Lambda<FunctionNodeExecutor<LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }
}

internal record ParameterIdFnNode : LeafFunctionNode
{
    // Expression Trees can't build a managed-pointer node for ReadOnlySpan<T>'s indexer (see
    // SpanIndexHelper's doc comment in LiteralValueUnion.cs) -- indexing goes through that helper
    // via Expression.Call instead of Expression.Property/MakeIndex. Reached only via this reflected
    // MethodInfo, so [DynamicDependency] keeps SpanIndexHelper.At rooted under trimming/NativeAOT --
    // without it the trimmer has no ordinary call-graph edge to At and could remove it as unused.
    [DynamicDependency(nameof(SpanIndexHelper.At), typeof(SpanIndexHelper))]
    private static readonly MethodInfo AtMethod =
        typeof(SpanIndexHelper).GetMethod(nameof(SpanIndexHelper.At), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal override Expression<FunctionNodeExecutor<LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        // No MemberInit/type-switch needed here (unlike the old dict-indexer version): the arg
        // buffer built by Function.BuildArgs already carries the correctly-typed LiteralValueUnion
        // (LiteralType + the one relevant value field) at this parameter's position -- this node
        // just reads it back out by position.
        var indexExpression = Expression.Call(AtMethod, args, Expression.Constant(ParameterOrder));

        return Expression.Lambda<FunctionNodeExecutor<LiteralValueUnion>>(indexExpression, args);
    }

    internal override LangType TypeContext => ParameterType;
    internal required string ParameterName { get; set; }
    internal required int ParameterOrder { get; set; }
    internal required LangType ParameterType { get; set; }
}

internal abstract record BinaryFunctionNode<TOut, TIn> : FunctionNode<TOut>
{
    internal FunctionNode<TIn> Left { get; set; } = null!;
    internal FunctionNode<TIn> Right { get; set; } = null!;
}

internal record LessOrEqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.IsLessThanOrEqual).Method;

    internal override Expression<FunctionNodeExecutor<bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.LessThanOrEqual(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<FunctionNodeExecutor<bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record LessExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.IsLessThan).Method;

    internal override Expression<FunctionNodeExecutor<bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.LessThan(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<FunctionNodeExecutor<bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record GreaterOrEqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.IsGreaterThanOrEqual).Method;

    internal override Expression<FunctionNodeExecutor<bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.GreaterThanOrEqual(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<FunctionNodeExecutor<bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record GreaterExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.IsGreaterThan).Method;

    internal override Expression<FunctionNodeExecutor<bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.GreaterThan(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<FunctionNodeExecutor<bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record NotEqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.AreNotEqual).Method;

    internal override Expression<FunctionNodeExecutor<bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.NotEqual(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<FunctionNodeExecutor<bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record EqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.AreEqual).Method;

    internal override Expression<FunctionNodeExecutor<bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.Equal(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<FunctionNodeExecutor<bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record OrExpressionFnNode : BinaryFunctionNode<bool, bool>
{
    internal override Expression<FunctionNodeExecutor<bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.OrElse(leftExpression, rightExpression);

        return Expression.Lambda<FunctionNodeExecutor<bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record ParenthesisExpressionFnNode : UnaryFunctionNode<bool>
{
    internal override Expression<FunctionNodeExecutor<bool>> GetExpression(ParameterExpression args)
    {
        var childExpression = Child.GetExpression(args).Body;

        return Expression.Lambda<FunctionNodeExecutor<bool>>(childExpression, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record NotExpressionFnNode : UnaryFunctionNode<bool>
{
    internal override Expression<FunctionNodeExecutor<bool>> GetExpression(ParameterExpression args)
    {
        var childExpression = Child.GetExpression(args).Body;

        return Expression.Lambda<FunctionNodeExecutor<bool>>(Expression.Not(childExpression), args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record AndExpressionFnNode : BinaryFunctionNode<bool, bool>
{
    internal override Expression<FunctionNodeExecutor<bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.AndAlso(leftExpression, rightExpression);

        return Expression.Lambda<FunctionNodeExecutor<bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}
