using Valtuutus.Core.Data;
using Valtuutus.Core.Lang;
using Valtuutus.Core.Pools;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

// Evaluates a schema-function leaf against the entity's attributes: fetched here for the
// single-op path, or handed in already fetched by a batching executor (EvaluateFetched).
internal static class AttributeExpressionEvaluator
{
    public static async Task<bool> Evaluate(IDataReaderProvider reader, Schema schema, CheckRequestContext ctx,
        string entityType, string entityId, PermissionNodeLeafExp leafExp, CancellationToken ct)
    {
        var fn = schema.Functions[leafExp.FunctionName];
        if (fn is null) throw new InvalidOperationException();

        if (!leafExp.IsContextValid(ctx.Context))
            return false;

        using var attributes = await reader.GetAttributesSingleEntity(
            new EntityAttributesFilter
            {
                Attributes = leafExp.AttributeArgNames,
                EntityId = entityId,
                EntityType = entityType,
                SnapToken = ctx.SnapToken
            }, ct).ConfigureAwait(false);

        return EvaluateFetched(fn, schema, ctx, entityType, leafExp, attributes.AsSpan());
    }

    /// <summary>
    /// Evaluates <paramref name="leafExp"/> against attribute rows the caller already fetched
    /// (the rows <see cref="IDataReaderProvider.GetAttributesSingleEntity"/> would return for
    /// <see cref="PermissionNodeLeafExp.AttributeArgNames"/>). Context validity is the caller's
    /// responsibility — see <see cref="Evaluate"/>.
    /// </summary>
    public static bool EvaluateFetched(Schema schema, CheckRequestContext ctx, string entityType,
        PermissionNodeLeafExp leafExp, ReadOnlySpan<AttributeTuple> attributes)
    {
        var fn = schema.Functions[leafExp.FunctionName];
        if (fn is null) throw new InvalidOperationException();
        return EvaluateFetched(fn, schema, ctx, entityType, leafExp, attributes);
    }

    private static bool EvaluateFetched(Function fn, Schema schema, CheckRequestContext ctx, string entityType,
        PermissionNodeLeafExp leafExp, ReadOnlySpan<AttributeTuple> attributes)
    {
        // Attribute order/completeness from the reader isn't guaranteed across providers
        // (a WHERE attribute IN (...) filter can return fewer rows than requested, in any
        // order), so index-matching against AttributeArgNames isn't safe. Build a lookup
        // once instead of rescanning `attributes` per function parameter.
        var attributesByName = DictionaryPool<string, AttributeTuple>.Rent();
        try
        {
            foreach (ref readonly var a in attributes)
                attributesByName[a.Attribute] = a;

            using var fnArgs = fn.BuildArgs(
                leafExp.Args,
                static (arg, paramType, state) =>
                {
                    var (byName, entityType, sch) = state;
                    if (!byName.TryGetValue(arg.AttributeName, out var a))
                        return new LiteralValueUnion { LiteralType = paramType };
                    return a.GetValue(sch.GetAttribute(entityType, arg.AttributeName).Type);
                },
                (attributesByName, entityType, schema),
                ctx.Context);

            return fn.Lambda(fnArgs.Span);
        }
        finally
        {
            DictionaryPool<string, AttributeTuple>.Return(attributesByName);
        }
    }
}
