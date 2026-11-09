using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

/// <summary>
/// The store is shared by every scope, and so is its latest token: a request without a token reads
/// at the latest write whichever scope made it, and never at a token a write can still land under.
/// </summary>
public sealed class LatestSnapTokenSpecs
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(TestsConsts.DefaultSchema).AddInMemory();
        return services.BuildServiceProvider();
    }

    private static CheckRequest OwnerCanDelete(string workspace, SnapToken? snapToken = null) =>
        new(TestsConsts.Workspaces.Identifier, workspace, "delete", TestsConsts.Users.Identifier,
            TestsConsts.Users.Alice, snapToken: snapToken);

    private static RelationTuple AliceOwns(string workspace) =>
        new(TestsConsts.Workspaces.Identifier, workspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice);

    [Fact]
    public async Task A_check_without_a_token_in_another_scope_sees_a_write()
    {
        await using var provider = Build();
        SnapToken written;
        await using (var writing = provider.CreateAsyncScope())
            written = await writing.ServiceProvider.GetRequiredService<IDataWriterProvider>().Write([AliceOwns("1")], [], default);

        await using var reading = provider.CreateAsyncScope();
        var result = await reading.ServiceProvider.GetRequiredService<ICheckEngine>().Check(OwnerCanDelete("1"), default);

        result.Allowed.Should().BeTrue();
        result.SnapToken.Should().Be(written);
    }

    [Fact]
    public async Task An_answer_read_without_a_token_does_not_change_at_its_token()
    {
        await using var provider = Build();
        var observations = new ConcurrentBag<(string Workspace, SnapToken Token, bool Allowed)>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            var random = new Random(w);
            while (!stop.IsCancellationRequested)
            {
                var workspace = random.Next(4).ToString();
                await using var scope = provider.CreateAsyncScope();
                var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
                if (random.Next(2) == 0)
                    await writer.Write([AliceOwns(workspace)], [], default);
                else
                    await writer.Delete(new DeleteFilter
                    {
                        Relations =
                        [
                            new DeleteRelationsFilter
                            {
                                EntityType = TestsConsts.Workspaces.Identifier, EntityId = workspace, Relation = "owner",
                                SubjectType = TestsConsts.Users.Identifier, SubjectId = TestsConsts.Users.Alice
                            }
                        ]
                    }, default);
            }
        })).ToArray();
        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(async () =>
        {
            var random = new Random(100 + r);
            while (!stop.IsCancellationRequested)
            {
                var workspace = random.Next(4).ToString();
                await using var scope = provider.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ICheckEngine>().Check(OwnerCanDelete(workspace), default);
                observations.Add((workspace, result.SnapToken!.Value, result.Allowed));
            }
        })).ToArray();
        await Task.WhenAll(writers.Concat(readers));

        await using var recheck = provider.CreateAsyncScope();
        var engine = recheck.ServiceProvider.GetRequiredService<ICheckEngine>();
        var changed = 0;
        foreach (var (workspace, token, allowed) in observations)
            if ((await engine.Check(OwnerCanDelete(workspace, token), default)).Allowed != allowed)
                changed++;

        observations.Should().NotBeEmpty();
        changed.Should().Be(0);
    }
}
