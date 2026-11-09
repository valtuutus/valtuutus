using System.Data.Common;
using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data;
using Valtuutus.Data.Caching;
using Valtuutus.Data.Db;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// A request without a snap token sees every write that returned before it was made, with or without
/// the cache, even while an older write is still open. The older write is the caller's own transaction,
/// which another task ends a while after the newer write starts.
/// </summary>
public abstract class BaseDbReadYourWritesSpecs : IAsyncLifetime
{
    private static readonly RelationTuple AliceOwnsPublic = new(TestsConsts.Workspaces.Identifier,
        TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice);

    private static readonly RelationTuple BobOwnsPrivate = new(TestsConsts.Workspaces.Identifier,
        TestsConsts.Workspaces.PrivateWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Bob);

    // Long enough that a read made as soon as the newer write returns runs while the older one is open.
    private static readonly TimeSpan OlderWriteOpenFor = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    protected BaseDbReadYourWritesSpecs(IDatabaseFixture fixture) => Fixture = fixture;

    protected IDatabaseFixture Fixture { get; }

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);

    /// <summary>How long after a write ended without removing its registration readers may still stop below it.</summary>
    protected virtual TimeSpan EndedWriteRelease => TimeSpan.FromMilliseconds(200);

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task A_read_without_a_token_sees_a_write_that_returned(bool cached, bool olderCommits)
    {
        await using var provider = BuildProvider(cached);
        await WarmLatestToken(provider);
        var writer = provider.GetRequiredService<IDbDataWriterProvider>();
        await using var connection = await OpenConnection();
        await using var older = await connection.BeginTransactionAsync();
        await writer.Write(connection, older, [BobOwnsPrivate], [], default);

        var olderEnds = EndLater(older, olderCommits);
        await writer.Write([AliceOwnsPublic], [], default);
        var read = await AliceCanDelete(provider, snapToken: null);
        await olderEnds;

        read.Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_without_a_token_sees_a_write_in_the_callers_transaction_once_announced(bool cached)
    {
        await using var provider = BuildProvider(cached);
        await WarmLatestToken(provider);
        var writer = provider.GetRequiredService<IDbDataWriterProvider>();
        await using var olderConnection = await OpenConnection();
        await using var older = await olderConnection.BeginTransactionAsync();
        await writer.Write(olderConnection, older, [BobOwnsPrivate], [], default);

        await using var connection = await OpenConnection();
        SnapToken written;
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            written = await writer.Write(connection, transaction, [AliceOwnsPublic], [], default);
            await transaction.CommitAsync();
        }

        var olderEnds = EndLater(older, commit: true);
        await writer.NotifyCommitted(written);
        var read = await AliceCanDelete(provider, snapToken: null);
        await olderEnds;

        read.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task The_cache_catches_up_once_an_older_write_rolls_back()
    {
        await using var provider = BuildProvider(cached: true);
        await WarmLatestToken(provider);
        var writer = provider.GetRequiredService<IDbDataWriterProvider>();
        await using var connection = await OpenConnection();
        await using var older = await connection.BeginTransactionAsync();
        await writer.Write(connection, older, [BobOwnsPrivate], [], default);

        var olderEnds = EndLater(older, commit: false);
        await writer.Write([AliceOwnsPublic], [], default);
        await olderEnds;
        await Task.Delay(EndedWriteRelease);
        var read = await AliceCanDelete(provider, snapToken: null);

        read.Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_carrying_the_written_token_sees_the_write(bool cached)
    {
        await using var provider = BuildProvider(cached);
        await WarmLatestToken(provider);
        var writer = provider.GetRequiredService<IDbDataWriterProvider>();
        await using var connection = await OpenConnection();
        await using var older = await connection.BeginTransactionAsync();
        await writer.Write(connection, older, [BobOwnsPrivate], [], default);

        var olderEnds = EndLater(older, commit: true);
        var written = await writer.Write([AliceOwnsPublic], [], default);
        var read = await AliceCanDelete(provider, written);
        await olderEnds;

        read.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_write_behind_an_open_write_returns_once_the_settle_timeout_passes()
    {
        var timeout = TimeSpan.FromMilliseconds(300);
        await using var provider = BuildProvider(cached: false, timeout);
        var writer = provider.GetRequiredService<IDbDataWriterProvider>();
        await using var connection = await OpenConnection();
        await using var older = await connection.BeginTransactionAsync();
        await writer.Write(connection, older, [BobOwnsPrivate], [], default);

        var started = Stopwatch.StartNew();
        var written = await writer.Write([AliceOwnsPublic], [], default);
        var waited = started.Elapsed;
        var read = await AliceCanDelete(provider, written);
        await older.RollbackAsync();

        waited.Should().BeGreaterThanOrEqualTo(timeout).And.BeLessThan(timeout + TimeSpan.FromSeconds(2));
        read.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_write_returns_while_the_injected_clock_stands_still()
    {
        // The older write rolls back while the newer one waits for it, and a write that just rolled
        // back still holds readers back for a moment, so the newer write checks the settled token
        // more than once.
        await using var provider = BuildProvider(cached: false, clock: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var writer = provider.GetRequiredService<IDbDataWriterProvider>();
        await using var connection = await OpenConnection();
        await using var older = await connection.BeginTransactionAsync();
        await writer.Write(connection, older, [BobOwnsPrivate], [], default);

        var newer = writer.Write([AliceOwnsPublic], [], default);
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        await older.RollbackAsync();
        await newer.WaitAsync(TimeSpan.FromSeconds(15));
        var read = await AliceCanDelete(provider, snapToken: null);

        read.Allowed.Should().BeTrue();
    }

    private ServiceProvider BuildProvider(bool cached, TimeSpan? settleTimeout = null, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(TestsConsts.DefaultSchema);
        if (clock is not null) services.AddSingleton(clock);
        var builder = AddSpecificProvider(services);
        if (settleTimeout is { } timeout) builder.Options.SettleTimeout = timeout;
        if (cached)
        {
            services.AddSingleton<IFusionCache>(_ => new FusionCache(new FusionCacheOptions()));
            builder.AddCaching();
        }
        return services.BuildServiceProvider();
    }

    // A token-less read before the scenario, so a cached provider already knows a latest token and
    // answers later token-less reads from it.
    private static Task WarmLatestToken(IServiceProvider provider) => AliceCanDelete(provider, snapToken: null);

    private static async Task EndLater(DbTransaction transaction, bool commit)
    {
        await Task.Delay(OlderWriteOpenFor);
        if (commit) await transaction.CommitAsync();
        else await transaction.RollbackAsync();
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
