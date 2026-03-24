namespace Valtuutus.Data.InMemory;

internal sealed class TransactionsStore
{
    private readonly object _lock = new();
    private Ulid? _latest;

    public void Create(Ulid id)
    {
        lock (_lock) { _latest = id; }
    }

    public Ulid? GetLatest()
    {
        lock (_lock) { return _latest; }
    }
}
