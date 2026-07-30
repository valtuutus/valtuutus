namespace Valtuutus.Core.Lang;

/// <summary>
/// Positional, boxing-free calling convention for schema DSL function bodies. Both the
/// source-generated compiled path (<c>SchemaFunctionsGen.All</c>) and the Expression-tree-compiled
/// fallback path (<see cref="Valtuutus.Core.Lang.SchemaReaders.SchemaFunctionReader"/>) produce
/// this delegate shape.
/// </summary>
public delegate bool FunctionExecutor(ReadOnlySpan<LiteralValueUnion> args);

// Internal composition delegate used while building a FunctionExecutor out of nested
// FunctionNode<T> expression trees (T is LiteralValueUnion for leaf/comparison operands, bool for
// the top-level function body) -- same fixed ReadOnlySpan<LiteralValueUnion> parameter, only the
// return type varies per node. Kept distinct from FunctionExecutor (rather than just using
// FunctionExecutor everywhere) because generic BCL delegates (Func<T>) cannot have a ref struct
// like ReadOnlySpan<T> substituted for a type parameter -- this hand-written delegate sidesteps
// that by keeping the span parameter fixed/closed and only T (the return type) open.
internal delegate T FunctionNodeExecutor<T>(ReadOnlySpan<LiteralValueUnion> args);

// Expression Trees can't represent ReadOnlySpan<T>'s indexer directly: its getter returns by
// managed pointer, and Expression.Property/Expression.MakeIndex throw "Property cannot have a
// managed pointer type" for it (confirmed during the #275 spike). Routing the read through this
// ordinary static method sidesteps that -- the method returns by value, so Expression.Call has no
// managed-pointer node to build.
internal static class SpanIndexHelper
{
    // Reached only via the reflected MethodInfo in ParameterIdFnNode (Expression.Call), so the
    // trimmer has no ordinary call-graph edge to it and would otherwise remove it as unused, the
    // same class of risk the DictionaryIndexerGetMethod comment on ParameterIdFnNode already
    // documents. The [DynamicDependency] that keeps it rooted lives on that reflecting call site
    // (ParameterIdFnNode.AtMethod), not here -- the attribute has to sit on the consumer that does
    // the reflection, not on the target itself (a self-referential declaration on the target
    // protects nothing).
    internal static LiteralValueUnion At(ReadOnlySpan<LiteralValueUnion> args, int index) => args[index];
}

public record struct LiteralValueUnion : IComparable<LiteralValueUnion>
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
