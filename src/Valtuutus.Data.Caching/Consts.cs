namespace Valtuutus.Data.Caching;

public static class Consts
{
    public const string LatestSnapTokenKey = "latest-snaptoken";

    /// <summary>
    /// Keyed-service key under which <c>AddCaching</c> re-registers whatever <c>ICheckEngine</c>
    /// was previously registered (V1 <c>CheckEngine</c> or the opt-in <c>CheckEngineV2</c>), so
    /// <see cref="CachedCheckEngine"/> can decorate it regardless of which one it is.
    /// </summary>
    public const string InnerCheckEngineKey = "valtuutus:caching:inner-check-engine";

    public const string InnerLookupEntityEngineKey = "valtuutus:caching:inner-lookup-entity-engine";

    public const string InnerLookupSubjectEngineKey = "valtuutus:caching:inner-lookup-subject-engine";
}