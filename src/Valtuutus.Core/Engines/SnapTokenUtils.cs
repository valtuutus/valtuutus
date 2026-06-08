using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines;

internal static class SnapTokenUtils
{
    /// <summary>
    /// Resolves the effective snap token for a request: the caller-supplied token when present,
    /// otherwise the latest committed token (falling back to <see cref="SnapToken.MinValue"/>).
    /// Pure — it does not mutate the caller's request, so request instances can be safely reused.
    /// </summary>
    public static async ValueTask<SnapToken> ResolveLatest(IDataReaderProvider reader, SnapToken? current,
        CancellationToken cancellationToken)
    {
        if (current is { } token)
            return token;

        return await reader.GetLatestSnapToken(cancellationToken) ?? SnapToken.MinValue;
    }
}
