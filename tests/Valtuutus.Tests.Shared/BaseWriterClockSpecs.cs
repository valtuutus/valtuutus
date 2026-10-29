using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Data;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// A writer takes its transaction's time from the <see cref="TimeProvider"/> registered in the
/// service collection: the snap token a write or delete returns carries that time.
/// </summary>
public abstract class BaseWriterClockSpecs : IAsyncLifetime
{
    protected static readonly RelationTuple AliceOwnsWorkspace = new(TestsConsts.Workspaces.Identifier,
        TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice);

    private static readonly DeleteFilter RevokeAliceOwnership = new()
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

    protected BaseWriterClockSpecs(IDatabaseFixture fixture) => Fixture = fixture;

    protected IDatabaseFixture Fixture { get; }

    // Whole seconds, so every column type the providers store it in keeps it exactly.
    protected FakeTimeProvider Time { get; } = new(new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero));

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_write_returns_a_snap_token_taken_at_the_time_provider_time()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var token = await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>()
            .Write([AliceOwnsWorkspace], [], default);

        Ulid.Parse(token.Value).Time.Should().Be(Time.GetUtcNow());
    }

    [Fact]
    public async Task A_delete_returns_a_snap_token_taken_at_the_time_provider_time()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await writer.Write([AliceOwnsWorkspace], [], default);
        Time.Advance(TimeSpan.FromMinutes(1));

        var token = await writer.Delete(RevokeAliceOwnership, default);

        Ulid.Parse(token.Value).Time.Should().Be(Time.GetUtcNow());
    }

    protected ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection().AddValtuutusCore(TestsConsts.DefaultSchema);
        services.AddSingleton<TimeProvider>(Time);
        AddSpecificProvider(services).AddConcurrentQueryLimit(3);
        return services.BuildServiceProvider();
    }
}
