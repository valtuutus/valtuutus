using IdGen;

namespace Authorizee.Data.Postgres.Tests;

public class MockTimeSource : ITimeSource
{
    private long _current;

    public MockTimeSource()
        : this(0) { }

    public DateTimeOffset Epoch { get; private set; }

    public TimeSpan TickDuration { get; }

    public MockTimeSource(long current)
        : this(current, TimeSpan.FromMilliseconds(1), DateTimeOffset.MinValue) { }

    public MockTimeSource(TimeSpan tickDuration)
        : this(0, tickDuration, DateTimeOffset.MinValue) { }

    public MockTimeSource(long current, TimeSpan tickDuration, DateTimeOffset epoch)
    {
        _current = current;
        TickDuration = tickDuration;
        Epoch = epoch;
    }

    public virtual long GetTicks() => _current;

    public void NextTick() => Interlocked.Increment(ref _current);

}

public class MockAutoIncrementingIntervalTimeSource : MockTimeSource
{
    private readonly int _incrementevery;
    private int _count;

    public MockAutoIncrementingIntervalTimeSource(int incrementEvery, long? current = 1, TimeSpan? tickDuration = null, DateTimeOffset? epoch = null)
        : base(current ?? 0, tickDuration ?? TimeSpan.FromMilliseconds(1), epoch ?? DateTimeOffset.MinValue)
    {
        _incrementevery = incrementEvery;
        _count = 0;
    }

    public override long GetTicks()
    {
        if (_count == _incrementevery)
        {
            NextTick();
            _count = 0;
        }
        Interlocked.Increment(ref _count);

        return base.GetTicks();
    }
}