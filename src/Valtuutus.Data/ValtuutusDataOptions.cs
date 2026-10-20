using Valtuutus.Core.Data;

namespace Valtuutus.Data;

/// <summary>
/// POCO that stores the options for the Valtuutus.Data library.
/// </summary>
public record ValtuutusDataOptions
{
    /// <summary>
    /// The amount of concurrent queries that a Check Request can evaluate at the same time.
    /// </summary>
    public int MaxConcurrentQueries { get; internal set; } = 5;
    
    /// <summary>
    /// Handlers run, in the order added, once a write or delete has committed, with its snap token
    /// (for a write in the caller's own transaction, when the caller announces it with
    /// <c>NotifyCommitted</c>). <c>AddCaching</c> adds one that publishes the token as the cache's
    /// latest token.
    /// </summary>
    public IList<Func<IServiceProvider, SnapToken, Task>> OnDataWritten { get; } = new List<Func<IServiceProvider, SnapToken, Task>>();

    /// <summary>
    /// Runs the <see cref="OnDataWritten"/> handlers, in the order added, with a committed write's
    /// snap token. Data providers call it once a write or delete has committed.
    /// </summary>
    public async Task NotifyDataWritten(IServiceProvider services, SnapToken snapToken)
    {
        foreach (var handler in OnDataWritten)
            await handler(services, snapToken);
    }
}