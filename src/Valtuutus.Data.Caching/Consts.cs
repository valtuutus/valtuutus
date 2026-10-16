namespace Valtuutus.Data.Caching;

public static class Consts
{
    public const string LatestSnapTokenKey = "latest-snaptoken";

    /// <summary>
    /// Keyed-service key under which <c>AddCaching</c> re-registers whatever <c>ICheckEngine</c>
    /// was previously registered (<c>CheckEngineV2</c>, unless the app registered its own), so
    /// <see cref="CachedCheckEngine"/> can decorate it regardless of which one it is.
    /// </summary>
    public const string InnerCheckEngineKey = "valtuutus:caching:inner-check-engine";

    public const string InnerLookupEntityEngineKey = "valtuutus:caching:inner-lookup-entity-engine";

    public const string InnerLookupSubjectEngineKey = "valtuutus:caching:inner-lookup-subject-engine";
}
