using System.Diagnostics;

namespace Authorizee.Core.Observability;

public static class DefaultActivitySource
{
    public const string SourceName = "Authorizee";
    public static ActivitySource Instance { get; } = new ActivitySource(SourceName);
    
    public const string SourceNameInternal = "Authorizee.Internal";
    public static ActivitySource InternalSourceInstance { get; } = new ActivitySource(SourceNameInternal);
}