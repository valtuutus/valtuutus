using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines;

internal static class ContextUtils
{
    // Basic validation for context access arguments
    // Later on, it will be better to return proper errors
    // Like if the context prop is null or not found
    internal static bool IsContextValid(this PermissionNodeLeafExp node, IDictionary<string, object> context)
    {
        return node.Args.All(a =>
            a.Type != PermissionNodeExpArgumentType.ContextAccess
            || (
                context.ContainsKey(((PermissionNodeExpArgumentContextAccess)a).ContextPropertyName)
                    && context[((PermissionNodeExpArgumentContextAccess)a).ContextPropertyName] is not null
            )
        );
    }
}