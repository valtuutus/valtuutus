namespace Valtuutus.Data.InMemory;

/// <summary>
/// The transaction state the stores share across scopes: writes take their id and apply under one
/// lock, so they commit in id order and the latest id is always settled: no write can still land
/// at or under it.
/// </summary>
internal sealed class InMemoryTransactions
{
    public object WriteLock { get; } = new();

    // Written and read under WriteLock: a Ulid is too wide to be read whole without it.
    private Ulid? _latest;

    public Ulid? Latest
    {
        get { lock (WriteLock) return _latest; }
    }

    public void Publish(Ulid id) => _latest = id;
}
