using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// <see cref="ValtuutusDataOptions.OnDataWritten"/> announces a snap token whose data is already
/// readable: a check run from the hook, in its own scope (so on its own connection) and pinned to
/// the announced token, sees the write. A cache that publishes the announced token as the latest
/// one relies on this, or a check run in between caches an answer from before the write.
/// </summary>
public abstract class BaseDataWrittenSpecs : IAsyncLifetime
{
    protected static readonly RelationTuple AliceOwnsWorkspace = new(TestsConsts.Workspaces.Identifier,
        TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice);

    protected static readonly DeleteFilter RevokeAliceOwnership = new()
    {
        Relations =
        [
            new DeleteRelationsFilter
            {
                EntityType = TestsConsts.Workspaces.Identifier, EntityId = TestsConsts.Workspaces.PublicWorkspace,
                Relation = "owner", SubjectType = TestsConsts.Users.Identifier, SubjectId = TestsConsts.Users.Alice
            }
        ]
    };

    // A read that has to wait for the write to commit, which in turn waits for the hook, would
    // never finish; the bound turns that into a failure.
    private static readonly TimeSpan HookCheckTimeout = TimeSpan.FromSeconds(10);

    protected BaseDataWrittenSpecs(IDatabaseFixture fixture) => Fixture = fixture;

    protected IDatabaseFixture Fixture { get; }

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_write_is_readable_at_its_token_when_OnDataWritten_runs()
    {
        var hook = new CheckingHook();
        await using var provider = BuildProvider(hook);
        await using var scope = provider.CreateAsyncScope();

        hook.Armed = true;
        await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>().Write([AliceOwnsWorkspace], [], default);

        hook.Seen.Should().Equal(true);
    }

    [Fact]
    public async Task A_delete_is_readable_at_its_token_when_OnDataWritten_runs()
    {
        var hook = new CheckingHook();
        await using var provider = BuildProvider(hook);
        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await writer.Write([AliceOwnsWorkspace], [], default);

        hook.Armed = true;
        await writer.Delete(RevokeAliceOwnership, default);

        hook.Seen.Should().Equal(false);
    }

    protected ServiceProvider BuildProvider(CheckingHook hook)
    {
        var services = new ServiceCollection().AddValtuutusCore(TestsConsts.DefaultSchema);
        AddSpecificProvider(services).AddConcurrentQueryLimit(3).Options.OnDataWritten.Add(hook.Run);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// When armed, checks from inside the hook whether Alice can delete the public workspace (an
    /// owner-only permission) at the announced token, and records the answer.
    /// </summary>
    protected sealed class CheckingHook
    {
        public bool Armed { get; set; }

        public List<bool> Seen { get; } = [];

        public List<SnapToken> Announced { get; } = [];

        public async Task Run(IServiceProvider services, SnapToken token)
        {
            Announced.Add(token);
            if (!Armed) return;

            using var timeout = new CancellationTokenSource(HookCheckTimeout);
            await using var scope = services.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
            var result = await engine.Check(new CheckRequest(TestsConsts.Workspaces.Identifier,
                TestsConsts.Workspaces.PublicWorkspace, "delete", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice, snapToken: token), timeout.Token);
            Seen.Add(result.Allowed);
        }
    }
}
