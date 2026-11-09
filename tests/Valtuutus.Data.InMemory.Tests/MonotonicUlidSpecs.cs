using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Valtuutus.Data.InMemory.Tests;

public class MonotonicUlidSpecs
{
    private static readonly DateTimeOffset Start = new(2021, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);

    [Fact]
    public void Ulids_taken_at_the_same_instant_strictly_increase()
    {
        var ulids = Enumerable.Range(0, 1000).Select(_ => MonotonicUlid.Next(_clock)).ToList();

        ulids.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        ulids.Select(u => u.ToString()).Should().BeInAscendingOrder(StringComparer.Ordinal);
        ulids.Should().AllSatisfy(u => u.Time.Should().Be(Start));
    }

    [Fact]
    public void A_ulid_taken_after_the_clock_went_back_follows_the_previous_one()
    {
        var clock = new SettableClock { Now = Start };
        var first = MonotonicUlid.Next(clock);
        clock.Now = Start.AddHours(-1);

        var second = MonotonicUlid.Next(clock);

        second.Should().BeGreaterThan(first);
    }

    [Fact]
    public void A_ulid_taken_in_a_later_millisecond_carries_that_instant()
    {
        MonotonicUlid.Next(_clock);
        _clock.Advance(TimeSpan.FromMilliseconds(1));

        var later = MonotonicUlid.Next(_clock);

        later.Time.Should().Be(Start.AddMilliseconds(1));
    }

    [Fact]
    public void A_full_millisecond_carries_into_the_next_one()
    {
        var ulids = Enumerable.Range(0, 70_000).Select(_ => MonotonicUlid.Next(_clock)).ToList();

        ulids.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        ulids[0].Time.Should().Be(Start);
        ulids[^1].Time.Should().Be(Start.AddMilliseconds(1));
    }

    [Fact]
    public void A_ulid_taken_after_a_floor_ahead_of_the_clock_follows_the_floor()
    {
        var floor = Ulid.NewUlid(Start.AddHours(1));

        var next = MonotonicUlid.NextAfter(_clock, floor);

        next.Should().BeGreaterThan(floor);
        next.Time.Should().Be(Start.AddHours(1));
    }

    [Fact]
    public void Ulids_taken_after_a_floor_keep_following_it()
    {
        var after = MonotonicUlid.NextAfter(_clock, Ulid.NewUlid(Start.AddHours(1)));

        var next = MonotonicUlid.Next(_clock);

        next.Should().BeGreaterThan(after);
    }

    [Fact]
    public void A_floor_behind_the_clock_changes_nothing()
    {
        var before = MonotonicUlid.Next(_clock);

        var next = MonotonicUlid.NextAfter(_clock, Ulid.NewUlid(Start.AddHours(-1)));

        next.Should().BeGreaterThan(before);
        next.Time.Should().Be(Start);
    }

    [Fact]
    public void Each_clock_keeps_its_own_order()
    {
        var other = new FakeTimeProvider(Start.AddYears(-1));
        MonotonicUlid.Next(_clock);

        var ulid = MonotonicUlid.Next(other);

        ulid.Time.Should().Be(Start.AddYears(-1));
    }

    [Fact]
    public async Task Ulids_taken_concurrently_are_unique_and_increase_per_caller()
    {
        var batches = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            Enumerable.Range(0, 5000).Select(_ => MonotonicUlid.Next(_clock)).ToList())));

        batches.SelectMany(b => b).Should().OnlyHaveUniqueItems();
        batches.Should().AllSatisfy(b => b.Should().BeInAscendingOrder());
    }

    // FakeTimeProvider refuses to move backwards.
    private sealed class SettableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
