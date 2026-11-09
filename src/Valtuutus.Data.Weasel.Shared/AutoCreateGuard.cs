using JasperFx;

namespace Valtuutus.Data.Weasel;

internal static class AutoCreateGuard
{
    /// <summary>
    /// <see cref="AutoCreate.All"/> drops and recreates tables on any difference, which deletes
    /// every tuple and attribute, so it is refused.
    /// </summary>
    public static void EnsureSafe(AutoCreate autoCreate)
    {
        if (autoCreate == AutoCreate.All)
            throw new ArgumentOutOfRangeException(nameof(autoCreate), autoCreate,
                "AutoCreate.All drops and recreates the Valtuutus tables, deleting their data. Use CreateOrUpdate, CreateOnly or None.");
    }
}
