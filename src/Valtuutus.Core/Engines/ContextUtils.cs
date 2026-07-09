using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines;

internal static class ContextUtils
{
    // Basic validation for context access arguments
    // Later on, it will be better to return proper errors
    // Like if the context prop is null or not found
    internal static bool IsContextValid(this PermissionNodeLeafExp node, IDictionary<string, object> context)
    {
        foreach (var a in node.Args)
        {
            if (a.Type != PermissionNodeExpArgumentType.ContextAccess) continue;
            var propertyName = ((PermissionNodeExpArgumentContextAccess)a).ContextPropertyName;
            if (!context.TryGetValue(propertyName, out var value) || value is null) return false;
        }
        return true;
    }
}