using System.Linq.Expressions;
using System.Reflection;
using Valtuutus.Lang;

namespace Valtuutus.Core.Lang;

internal abstract record FunctionNode<T>
{
    internal abstract Expression<Func<IDictionary<string, object?>, T>> GetExpression(ParameterExpression args);
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

internal record struct LiteralValueUnion : IComparable<LiteralValueUnion>
{
    public LangType LiteralType { get; set; }
    public int? IntValue { get; set; }
    public string? StringValue { get; set; }
    public decimal? DecimalValue { get; set; }
    public bool? BooleanValue { get; set; }

    public int CompareTo(LiteralValueUnion other)
    {
        if (LiteralType != other.LiteralType)
            throw new ArgumentException("Incompatible literal type comparison");

        var intValueComparison = Nullable.Compare(IntValue, other.IntValue);
        if (intValueComparison != 0)
            return intValueComparison;

        var stringValueComparison = string.Compare(StringValue, other.StringValue, StringComparison.Ordinal);
        if (stringValueComparison != 0)
            return stringValueComparison;

        return Nullable.Compare(DecimalValue, other.DecimalValue);
    }

    public static bool operator <(LiteralValueUnion left, LiteralValueUnion right) => left.CompareTo(right) < 0;

    public static bool operator >(LiteralValueUnion left, LiteralValueUnion right) => left.CompareTo(right) > 0;

    public static bool operator <=(LiteralValueUnion left, LiteralValueUnion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(LiteralValueUnion left, LiteralValueUnion right) => left.CompareTo(right) >= 0;

    // Named wrappers around the operators above, so ExpressionNode.cs's comparison nodes can pass an
    // explicit MethodInfo into Expression.Equal/NotEqual/LessThan/etc. instead of relying on those
    // factories' by-name operator-method search (GetUserDefinedBinaryOperator), which throws under
    // NativeAOT once trimming removes an operator that's otherwise only reachable via reflection.
    // Calling the operators here directly (a == b, a < b, ...) also keeps them reachable for the
    // trimmer, since this is now a normal static call site rather than a reflection-only one.
    internal static bool AreEqual(LiteralValueUnion left, LiteralValueUnion right) => left == right;
    internal static bool AreNotEqual(LiteralValueUnion left, LiteralValueUnion right) => left != right;
    internal static bool IsLessThan(LiteralValueUnion left, LiteralValueUnion right) => left < right;
    internal static bool IsGreaterThan(LiteralValueUnion left, LiteralValueUnion right) => left > right;
    internal static bool IsLessThanOrEqual(LiteralValueUnion left, LiteralValueUnion right) => left <= right;
    internal static bool IsGreaterThanOrEqual(LiteralValueUnion left, LiteralValueUnion right) => left >= right;
}

internal abstract record LeafFunctionNode : FunctionNode<LiteralValueUnion>
{
}

internal record StringLiteralFnNode : LeafFunctionNode
{
    internal override LangType TypeContext => LangType.String;
    internal required string Value { get; init; }

    internal override Expression<Func<IDictionary<string, object?>, LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LangType.String, StringValue = Value };
        return Expression.Lambda<Func<IDictionary<string, object?>, LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }
}

internal record IntegerLiteralFnNode : LeafFunctionNode
{
    internal override Expression<Func<IDictionary<string, object?>, LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LangType.Int, IntValue = Value };
        return Expression.Lambda<Func<IDictionary<string, object?>, LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }

    internal override LangType TypeContext => LangType.Int;
    internal int Value { get; init; }
}

internal record DecimalLiteralFnNode : LeafFunctionNode
{
    internal override LangType TypeContext => LangType.Decimal;
    internal required decimal Value { get; init; }

    internal override Expression<Func<IDictionary<string, object?>, LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LangType.Decimal, DecimalValue = Value };
        return Expression.Lambda<Func<IDictionary<string, object?>, LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }
}

internal record BooleanLiteralFnNode : LeafFunctionNode
{
    internal override LangType TypeContext => LangType.Boolean;
    internal required bool Value { get; init; }

    internal override Expression<Func<IDictionary<string, object?>, LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var wrappedValue = new LiteralValueUnion { LiteralType = LangType.Boolean, BooleanValue = Value };
        return Expression.Lambda<Func<IDictionary<string, object?>, LiteralValueUnion>>(
            Expression.Constant(wrappedValue), args);
    }
}

internal record ParameterIdFnNode : LeafFunctionNode
{
    // IDictionary<string, object?>.get_Item, resolved once via a typeof() on the exact interface
    // (a linker-recognized pattern that doesn't require DynamicallyAccessedMembers/produce a
    // warning) rather than Expression.Property(instance, "Item", args), whose *overload* is
    // unconditionally [RequiresUnreferencedCode] and throws under trimming/NativeAOT. Calling the
    // indexer's own get_Item directly (not a wrapper method) keeps the compiled/interpreted tree
    // identical in shape to the original code -- no extra call frame on this hot path.
    private static readonly MethodInfo DictionaryIndexerGetMethod =
        typeof(IDictionary<string, object?>).GetProperty("Item")!.GetGetMethod()!;

    internal override Expression<Func<IDictionary<string, object?>, LiteralValueUnion>> GetExpression(
        ParameterExpression args)
    {
        var key = Expression.Constant(ParameterName);

        var indexExpression = Expression.Call(args, DictionaryIndexerGetMethod, key);

        var newLiteralValueUnionExpression = ParameterType switch
        {
           LangType.String => Expression.MemberInit(
                Expression.New(typeof(LiteralValueUnion)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.LiteralType))!,
                    Expression.Constant(ParameterType)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.StringValue))!,
                    Expression.Convert(indexExpression, typeof(string)))
            ),
            LangType.Int => Expression.MemberInit(
                Expression.New(typeof(LiteralValueUnion)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.LiteralType))!,
                    Expression.Constant(ParameterType)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.IntValue))!,
                    Expression.Convert(indexExpression, typeof(int?)))
            ),

            LangType.Decimal => Expression.MemberInit(
                Expression.New(typeof(LiteralValueUnion)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.LiteralType))!,
                    Expression.Constant(ParameterType)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.DecimalValue))!,
                    Expression.Convert(indexExpression, typeof(decimal?)))
            ),
            LangType.Boolean => Expression.MemberInit(
                Expression.New(typeof(LiteralValueUnion)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.LiteralType))!,
                    Expression.Constant(ParameterType)),
                Expression.Bind(
                    typeof(LiteralValueUnion).GetProperty(nameof(LiteralValueUnion.BooleanValue))!,
                    Expression.Convert(indexExpression, typeof(bool?)))
            ),

            _ => throw new ArgumentException("Unsupported type for LiteralValueUnion")
        };

        var lambda =
            Expression.Lambda<Func<IDictionary<string, object?>, LiteralValueUnion>>(newLiteralValueUnionExpression,
                args);

        return lambda;
    }

    internal override LangType TypeContext => ParameterType;
    internal required string ParameterName { get; set; }
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

    internal override Expression<Func<IDictionary<string, object?>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.LessThanOrEqual(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<Func<IDictionary<string, object?>, bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record LessExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.IsLessThan).Method;

    internal override Expression<Func<IDictionary<string, object?>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.LessThan(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<Func<IDictionary<string, object?>, bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record GreaterOrEqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.IsGreaterThanOrEqual).Method;

    internal override Expression<Func<IDictionary<string, object?>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.GreaterThanOrEqual(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<Func<IDictionary<string, object?>, bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record GreaterExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.IsGreaterThan).Method;

    internal override Expression<Func<IDictionary<string, object?>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.GreaterThan(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<Func<IDictionary<string, object?>, bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record NotEqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.AreNotEqual).Method;

    internal override Expression<Func<IDictionary<string, object?>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.NotEqual(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<Func<IDictionary<string, object?>, bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record EqualExpressionFnNode : BinaryFunctionNode<bool, LiteralValueUnion>
{
    private static readonly MethodInfo Method =
        ((Func<LiteralValueUnion, LiteralValueUnion, bool>)LiteralValueUnion.AreEqual).Method;

    internal override Expression<Func<IDictionary<string, object?>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.Equal(leftExpression, rightExpression, liftToNull: false, method: Method);

        return Expression.Lambda<Func<IDictionary<string, object?>, bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record OrExpressionFnNode : BinaryFunctionNode<bool, bool>
{
    internal override Expression<Func<IDictionary<string, object?>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.OrElse(leftExpression, rightExpression);

        return Expression.Lambda<Func<IDictionary<string, object?>, bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record ParenthesisExpressionFnNode : UnaryFunctionNode<bool>
{
    internal override Expression<Func<IDictionary<string, object?>, bool>> GetExpression(ParameterExpression args)
    {
        var childExpression = Child.GetExpression(args).Body;

        return Expression.Lambda<Func<IDictionary<string, object?>, bool>>(childExpression, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record NotExpressionFnNode : UnaryFunctionNode<bool>
{
    internal override Expression<Func<IDictionary<string, object?>, bool>> GetExpression(ParameterExpression args)
    {
        var childExpression = Child.GetExpression(args).Body;

        return Expression.Lambda<Func<IDictionary<string, object?>, bool>>(Expression.Not(childExpression), args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}

internal record AndExpressionFnNode : BinaryFunctionNode<bool, bool>
{
    internal override Expression<Func<IDictionary<string, object?>, bool>> GetExpression(ParameterExpression args)
    {
        var leftExpression = Left.GetExpression(args).Body;
        var rightExpression = Right.GetExpression(args).Body;

        var comparison = Expression.AndAlso(leftExpression, rightExpression);

        return Expression.Lambda<Func<IDictionary<string, object?>, bool>>(comparison, args);
    }

    internal override LangType TypeContext => LangType.Boolean;
}