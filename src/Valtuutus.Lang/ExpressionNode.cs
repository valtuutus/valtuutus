﻿using System.Linq.Expressions;

namespace Valtuutus.Lang;

internal enum FunctionNodeType
{
    StringLiteral,
    IntLiteral,
    DecimalLiteral,
    ParameterId,
    InListExpression,
    LessOrEqualExpression,
    LessExpression,
    GreaterOrEqualExpression,
    GreaterExpression,
    NotEqualExpression,
    EqualExpression,
    OrExpression,
    AndExpression,
    ParenthesisExpression,
    StringLiteralList,
    IntLiteralList,
    DecimalLiteralList
}

internal abstract record FunctionNode<T>
{
    internal abstract Expression<Func<IDictionary<string, object>, T>> GetExpression(ParameterExpression args);
    internal virtual Type TypeContext { get; set; }
    internal abstract FunctionNodeType NodeType { get; }
}

internal abstract record UnaryFunctionNode<T> : FunctionNode<T>
{
    internal FunctionNode<T> Child { get; set; } = null!;
}

internal enum LiteralType
{
    Int,
    String,
    Decimal
}

internal record LiteralValueUnion : IComparable<LiteralValueUnion>
{
    public LiteralType LiteralType { get; set; }
    public int? IntValue { get; set; }
    public string? StringValue { get; set; }
    public decimal? DecimalValue { get; set; }

    public int CompareTo(LiteralValueUnion? other)
    {
        if (ReferenceEquals(this, other))
        {
            return 0;
        }

        if (other is null)
        {
            return 1;
        }

        if (LiteralType != other.LiteralType)
        {
            throw new ArgumentException();
        }

        var intValueComparison = Nullable.Compare(IntValue, other.IntValue);
        if (intValueComparison != 0)
        {
            return intValueComparison;
        }

        var stringValueComparison = string.Compare(StringValue, other.StringValue, StringComparison.Ordinal);
        if (stringValueComparison != 0)
        {
            return stringValueComparison;
        }

        return Nullable.Compare(DecimalValue, other.DecimalValue);
    }

    public static bool operator <(LiteralValueUnion left, LiteralValueUnion right) => left.CompareTo(right) < 0;

    public static bool operator >(LiteralValueUnion left, LiteralValueUnion right) => left.CompareTo(right) > 0;

    public static bool operator <=(LiteralValueUnion left, LiteralValueUnion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(LiteralValueUnion left, LiteralValueUnion right) => left.CompareTo(right) >= 0;

}

internal abstract record LeafFunctionNode : FunctionNode<LiteralValueUnion>
{
}

internal record StringLiteralFnNode : LeafFunctionNode
{
    internal override Type TypeContext => typeof(string);
    internal override FunctionNodeType NodeType => FunctionNodeType.StringLiteral;
    internal required string Value { get; init; }

    internal override Expression<Func<IDictionary<string, object>, LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LiteralType.String, StringValue = Value };
        return Expression.Lambda<Func<IDictionary<string, object>, LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }
}

internal record IntegerLiteralFnNode : LeafFunctionNode
{
    internal override Expression<Func<IDictionary<string, object>, LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LiteralType.Int, IntValue = Value };
        return Expression.Lambda<Func<IDictionary<string, object>, LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }

    internal override Type TypeContext => typeof(int);
    internal override FunctionNodeType NodeType => FunctionNodeType.IntLiteral;
    internal int Value { get; init; }
}

internal record DecimalLiteralFnNode : LeafFunctionNode
{
    internal override Type TypeContext => typeof(LiteralValueUnion);
    internal override FunctionNodeType NodeType => FunctionNodeType.DecimalLiteral;
    internal required decimal Value { get; init; }

    internal override Expression<Func<IDictionary<string, object>, LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LiteralType.Decimal, DecimalValue = Value };
        return Expression.Lambda<Func<IDictionary<string, object>, LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }
}

internal record ParameterIdFnNode : LeafFunctionNode
{
    internal override Expression<Func<IDictionary<string, object>, LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var key = Expression.Constant(ParameterName);
        
        var indexExpression = Expression.Property(args, "Item", key);
        
        var newLiteralValueUnionExpression = ParameterType switch
        {
            { } t when t == typeof(string) => Expression.MemberInit(
                Expression.New(typeof(LiteralValueUnion)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.LiteralType))!,
                    Expression.Constant(GetLiteralTypeFromParameterType(ParameterType))),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.StringValue))!,
                    Expression.Convert(indexExpression, typeof(string)))
            ),
            { } t when t == typeof(int) => Expression.MemberInit(
                Expression.New(typeof(LiteralValueUnion)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.LiteralType))!,
                    Expression.Constant(GetLiteralTypeFromParameterType(ParameterType))),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.IntValue))!,
                    Expression.Convert(indexExpression, typeof(int?)))
            ),

            { } t when t == typeof(decimal) => Expression.MemberInit(
                Expression.New(typeof(LiteralValueUnion)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.LiteralType))!,
                    Expression.Constant(GetLiteralTypeFromParameterType(ParameterType))),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.DecimalValue))!,
                    Expression.Convert(indexExpression, typeof(decimal?)))
            ),

            _ => throw new ArgumentException("Unsupported type for LiteralValueUnion")
        };
        
        var lambda =
            Expression.Lambda<Func<IDictionary<string, object>, LiteralValueUnion>>(newLiteralValueUnionExpression,
                args);
        
        return lambda;
    }

    internal override FunctionNodeType NodeType => FunctionNodeType.ParameterId;
    internal required string ParameterName { get; set; }
    internal required Type ParameterType { get; set; }

    private static LiteralType GetLiteralTypeFromParameterType(Type parameterType)
    {
        if (parameterType == typeof(int))
            return LiteralType.Int;
        if (parameterType == typeof(string))
            return LiteralType.String;
        if (parameterType == typeof(decimal))
            return LiteralType.Decimal;
        throw new ArgumentException($"Unsupported parameter type: {parameterType}");
    }
}

internal abstract record BinaryFunctionNode<TOut, TIn> : FunctionNode<TOut>
{
    internal FunctionNode<TIn> Left { get; set; } = null!;
    internal FunctionNode<TIn> Right { get; set; } = null!;
}

internal record LessOrEqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    internal override Expression<Func<IDictionary<string, object>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.LessThanOrEqual(leftExpression, rightExpression);

        return Expression.Lambda<Func<IDictionary<string, object>, bool>>(comparison, args);
    }

    internal override FunctionNodeType NodeType => FunctionNodeType.LessOrEqualExpression;
}

// internal record InListExpressionFnNode : BinaryFunctionNode
// {
//     internal override FunctionNodeType NodeType => FunctionNodeType.InListExpression;
//     internal required IEnumerable<UnaryFunctionNode> Values { get; set; }
// }

internal record LessExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    internal override Expression<Func<IDictionary<string, object>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.LessThan(leftExpression, rightExpression);

        return Expression.Lambda<Func<IDictionary<string, object>, bool>>(comparison, args);
    }

    internal override FunctionNodeType NodeType => FunctionNodeType.LessExpression;
}

internal record GreaterOrEqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    internal override Expression<Func<IDictionary<string, object>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.GreaterThanOrEqual(leftExpression, rightExpression);

        return Expression.Lambda<Func<IDictionary<string, object>, bool>>(comparison, args);
    }

    internal override FunctionNodeType NodeType => FunctionNodeType.GreaterOrEqualExpression;
}

internal record GreaterExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    internal override Expression<Func<IDictionary<string, object>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.GreaterThan(leftExpression, rightExpression);

        return Expression.Lambda<Func<IDictionary<string, object>, bool>>(comparison, args);
    }

    internal override FunctionNodeType NodeType => FunctionNodeType.GreaterExpression;
}

internal record NotEqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    internal override Expression<Func<IDictionary<string, object>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.NotEqual(leftExpression, rightExpression);

        return Expression.Lambda<Func<IDictionary<string, object>, bool>>(comparison, args);
    }

    internal override FunctionNodeType NodeType => FunctionNodeType.NotEqualExpression;
}

internal record EqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    internal override Expression<Func<IDictionary<string, object>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.Equal(leftExpression, rightExpression);

        return Expression.Lambda<Func<IDictionary<string, object>, bool>>(comparison, args);
    }

    internal override FunctionNodeType NodeType => FunctionNodeType.EqualExpression;
}

internal record OrExpressionFnNode : BinaryFunctionNode<bool, bool>
{
    internal override Expression<Func<IDictionary<string, object>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.OrElse(leftExpression, rightExpression);

        return Expression.Lambda<Func<IDictionary<string, object>, bool>>(comparison, args);
    }

    internal override FunctionNodeType NodeType => FunctionNodeType.OrExpression;
}

internal record ParenthesisExpressionFnNode : UnaryFunctionNode<bool>
{
    internal override Expression<Func<IDictionary<string, object>, bool>> GetExpression(ParameterExpression args)
    {
        var childExpression = Child.GetExpression(args).Body;

        return Expression.Lambda<Func<IDictionary<string, object>, bool>>(childExpression, args);
    }

    internal override FunctionNodeType NodeType => FunctionNodeType.ParenthesisExpression;
}

internal record AndExpressionFnNode : BinaryFunctionNode<bool, bool>
{
    internal override Expression<Func<IDictionary<string, object>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.AndAlso(leftExpression, rightExpression);

        return Expression.Lambda<Func<IDictionary<string, object>, bool>>(comparison, args);
    }

    internal override FunctionNodeType NodeType => FunctionNodeType.AndExpression;
}

// internal record StringLiteralListFnNode : FunctionNode<string[]>
// {
//     internal override Expression<Func<IDictionary<string, object>, string[]>> GetExpression(
//         ParameterExpression args)
//     {
//         return Expression.Lambda<Func<IDictionary<string, object>, string[]>>(Expression.Constant(Values));
//     }
//
//     internal override FunctionNodeType NodeType => FunctionNodeType.StringLiteralList;
//     internal required List<string> Values { get; init; }
// }
//
// internal record IntLiteralListFnNode : FunctionNode<int[]>
// {
//     internal override Expression<Func<IDictionary<string, object>, int[]>> GetExpression(ParameterExpression args)
//     {
//         return Expression.Lambda<Func<IDictionary<string, object>, int[]>>(Expression.Constant(Values));
//     }
//
//     internal override FunctionNodeType NodeType => FunctionNodeType.IntLiteralList;
//     internal required List<int> Values { get; init; }
// }
//
// internal record DecimalLiteralListFnNode : FunctionNode<decimal[]>
// {
//     internal override Expression<Func<IDictionary<string, object>, decimal[]>> GetExpression(ParameterExpression args)
//     {
//         return Expression.Lambda<Func<IDictionary<string, object>, decimal[]>>(Expression.Constant(Values));
//     }
//
//     internal override FunctionNodeType NodeType => FunctionNodeType.DecimalLiteralList;
//     internal required List<decimal> Values { get; init; }
// }