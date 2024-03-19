using System.Diagnostics;

namespace Valtuutus.Core.Observability;

public static class DefaultActivitySource
{
    public const string SourceName = "Valtuutus";
    public static ActivitySource Instance { get; } = new ActivitySource(SourceName);
    
    public const string SourceNameInternal = "Valtuutus.Internal";
    public static ActivitySource InternalSourceInstance { get; } = new ActivitySource(SourceNameInternal);
}