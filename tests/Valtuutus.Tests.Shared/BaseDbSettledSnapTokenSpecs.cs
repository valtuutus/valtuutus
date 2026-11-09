using System.Data.Common;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data;
using Valtuutus.Data.Db;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// A request without a snap token reads at a settled token: one no write can still commit under. A
/// write takes its id before it commits, so one that is still open when a newer write commits would
/// otherwise land inside the newer write's snapshot after that snapshot was read, and an answer
/// cached at its token would go stale.
/// </summary>
public abstract class BaseDbSettledSnapTokenSpecs : IAsyncLifetime
{
    private static readonly RelationTuple AliceOwnsPublic = new(TestsConsts.Workspaces.Identifier,
        TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice);

    private static readonly RelationTuple BobOwnsPrivate = new(TestsConsts.Workspaces.Identifier,
        TestsConsts.Workspaces.PrivateWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Bob);

    private static readonly DeleteFilter RevokeAlice = new()
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

    // A read the open write blocks would never finish; the bound turns that into a failure.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    protected BaseDbSettledSnapTokenSpecs(IDatabaseFixture fixture) => Fixture = fixture;

    protected IDatabaseFixture Fixture { get; }

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_read_without_a_token_reports_a_snapshot_an_open_write_cannot_change()
    {
        await using var provider = BuildProvider();
        var writer = provider.GetRequiredService<IDbDataWriterProvider>();
        await using var connection = await OpenConnection();
        await using var older = await connection.BeginTransactionAsync();
        await writer.Write(connection, older, [AliceOwnsPublic], [], default);
        await writer.Write([BobOwnsPrivate], [], default);

        var read = await AliceCanDelete(provider, snapToken: null);
        await older.CommitAsync();
        var reread = await AliceCanDelete(provider, read.SnapToken);

        reread.Allowed.Should().Be(read.Allowed, "the snapshot at {0} was already read", read.SnapToken!.Value.Value);
    }

    [Fact]
    public async Task A_read_without_a_token_sees_writes_once_the_open_write_commits()
    {
        await using var provider = BuildProvider();
        var writer = provider.GetRequiredService<IDbDataWriterProvider>();
        await using var connection = await OpenConnection();
        await using var older = await connection.BeginTransactionAsync();
        await writer.Write(connection, older, [AliceOwnsPublic], [], default);
        var newer = await writer.Write([BobOwnsPrivate], [], default);

        var whileOpen = await AliceCanDelete(provider, snapToken: null);
        await older.CommitAsync();
        var afterCommit = await AliceCanDelete(provider, snapToken: null);

        string.CompareOrdinal(whileOpen.SnapToken!.Value.Value, newer.Value).Should().BeNegative("the older write was still open");
        afterCommit.Allowed.Should().BeTrue();
        afterCommit.SnapToken!.Value.Value.Should().Be(newer.Value);
    }

    [Fact]
    public async Task A_write_that_rolled_back_stops_holding_reads_back()
    {
        await using var provider = BuildProvider();
        var writer = provider.GetRequiredService<IDbDataWriterProvider>();
        await using var connection = await OpenConnection();
        await using (var abandoned = await connection.BeginTransactionAsync())
        {
            await writer.Write(connection, abandoned, [AliceOwnsPublic], [], default);
            await abandoned.RollbackAsync();
        }
        var newer = await writer.Write([BobOwnsPrivate], [], default);

        await Task.Delay(TimeSpan.FromMilliseconds(200));
        var read = await AliceCanDelete(provider, snapToken: null);

        read.Allowed.Should().BeFalse();
        read.SnapToken!.Value.Value.Should().Be(newer.Value);
    }

    [Fact]
    public async Task A_write_from_a_clock_behind_takes_an_id_above_the_newest_committed()
    {
        await using var provider = BuildProvider();
        var newer = await provider.GetRequiredService<IDataWriterProvider>().Write([BobOwnsPrivate], [], default);
        await using var behind = BuildProvider(new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(-1)));
        var writer = behind.GetRequiredService<IDataWriterProvider>();

        var granted = await writer.Write([AliceOwnsPublic], [], default);
        var revoked = await writer.Delete(RevokeAlice, default);

        string.CompareOrdinal(granted.Value, newer.Value).Should().BePositive();
        string.CompareOrdinal(revoked.Value, granted.Value).Should().BePositive();
        (await AliceCanDelete(provider, granted)).Allowed.Should().BeTrue();
        (await AliceCanDelete(provider, revoked)).Allowed.Should().BeFalse();
    }

    private ServiceProvider BuildProvider(TimeProvider? clock = null)
    {
        var services = new ServiceCollection().AddValtuutusCore(TestsConsts.DefaultSchema);
        if (clock is not null) services.AddSingleton(clock);
        AddSpecificProvider(services);
        return services.BuildServiceProvider();
    }

    private static async Task<CheckResult> AliceCanDelete(IServiceProvider provider, SnapToken? snapToken)
    {
        using var timeout = new CancellationTokenSource(ReadTimeout);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICheckEngine>().Check(new CheckRequest(
            TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "delete",
            TestsConsts.Users.Identifier, TestsConsts.Users.Alice, snapToken: snapToken), timeout.Token);
    }

    private async Task<DbConnection> OpenConnection()
    {
        var connection = (DbConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await connection.OpenAsync();
        return connection;
    }
}
