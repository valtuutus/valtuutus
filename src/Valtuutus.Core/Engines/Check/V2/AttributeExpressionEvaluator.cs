using Valtuutus.Core.Data;
using Valtuutus.Core.Lang;
using Valtuutus.Core.Pools;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

// Copied verbatim from CheckEngine.CheckLeafFn (minus explain-node bookkeeping) so V1 and V2
// evaluate schema functions identically. V1 intentionally does not call this — keep the two
// in sync manually if CheckLeafFn changes.
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

        // Attribute order/completeness from the reader isn't guaranteed across providers
        // (a WHERE attribute IN (...) filter can return fewer rows than requested, in any
        // order), so index-matching against AttributeArgNames isn't safe. Build a lookup
        // once instead of rescanning `attributes` per function parameter.
        var attributesByName = DictionaryPool<string, AttributeTuple>.Rent();
        try
        {
            foreach (var a in attributes)
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
