using FluentAssertions;
using Valtuutus.Core.Engines.Check.V2;

namespace Valtuutus.Data.InMemory.Tests.V2;

public class CompletionMailboxSpecs
{
    [Fact]
    public async Task Synchronous_post_dequeues_without_suspension()
    {
        var mailbox = new CompletionMailbox();
        mailbox.Post(new Completion(Token: 7, Result: true, Payload: null, Error: null));
        var vt = mailbox.DequeueAsync(CancellationToken.None);
        vt.IsCompleted.Should().BeTrue("a queued completion must be consumable synchronously");
        (await vt).Token.Should().Be(7);
    }

    [Fact]
    public async Task Post_wakes_a_waiting_consumer()
    {
        var mailbox = new CompletionMailbox();
        var pending = mailbox.DequeueAsync(CancellationToken.None).AsTask();
        pending.IsCompleted.Should().BeFalse();
        mailbox.Post(new Completion(3, false, null, null));
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Token.Should().Be(3);
    }

    [Fact]
    public async Task Concurrent_posters_lose_nothing()
    {
        var mailbox = new CompletionMailbox();
        const int posters = 8, perPoster = 1_000;
        var postTasks = Enumerable.Range(0, posters).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perPoster; i++)
                mailbox.Post(new Completion(p * perPoster + i, true, null, null));
        })).ToArray();

        var seen = new HashSet<int>();
        for (var i = 0; i < posters * perPoster; i++)
            seen.Add((await mailbox.DequeueAsync(CancellationToken.None)).Token).Should().BeTrue();
        await Task.WhenAll(postTasks);
        seen.Should().HaveCount(posters * perPoster);
    }

    [Fact]
    public async Task Dequeue_honors_cancellation()
    {
        var mailbox = new CompletionMailbox();
        using var cts = new CancellationTokenSource(50);
        var act = async () => await mailbox.DequeueAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Clear_drops_unconsumed_completions_so_a_reused_mailbox_starts_empty()
    {
        var mailbox = new CompletionMailbox();
        mailbox.Post(new Completion(1, true, null, null));
        mailbox.Post(new Completion(2, true, null, null));

        mailbox.Clear();

        var pending = mailbox.DequeueAsync(CancellationToken.None).AsTask();
        pending.IsCompleted.Should().BeFalse("Clear must drop both queued completions, leaving nothing to dequeue");
        mailbox.Post(new Completion(3, false, null, null));
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Token.Should().Be(3);
    }
}
