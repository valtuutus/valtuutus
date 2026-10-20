using System.Diagnostics;

namespace Valtuutus.Core.Observability;

public static class DefaultActivitySource
{
    public const string SourceName = "Valtuutus";
    public static ActivitySource Instance { get; } = new ActivitySource(SourceName);
    
    [Obsolete("Nothing is emitted on this source; every span is on SourceName.")]
    public const string SourceNameInternal = "Valtuutus.Internal";

    [Obsolete("Nothing is emitted on this source; every span is on SourceName.")]
    public static ActivitySource InternalSourceInstance { get; } = new ActivitySource("Valtuutus.Internal");
}