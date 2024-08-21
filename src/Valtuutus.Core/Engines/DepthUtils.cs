namespace Valtuutus.Core.Engines;

internal static class DepthUtils
{
    public static bool CheckDepthLimit(this IWithDepth req) =>
        req.Depth == 0;

    public static void DecreaseDepth(this IWithDepth req) =>
        req.Depth--;
}
