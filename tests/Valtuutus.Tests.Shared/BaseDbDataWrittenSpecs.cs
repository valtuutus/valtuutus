using System.Data.Common;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Db;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// A write or delete inside the caller's own transaction commits when the caller says so, so the
/// writer announces nothing itself; the caller announces it with
/// <see cref="IDbDataWriterProvider.NotifyCommitted"/> once the transaction has committed.
/// </summary>
public abstract class BaseDbDataWrittenSpecs : BaseDataWrittenSpecs
{
    protected BaseDbDataWrittenSpecs(IDatabaseFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task A_write_in_the_callers_transaction_is_announced_by_NotifyCommitted()
    {
        var hook = new CheckingHook { Armed = true };
        await using var provider = BuildProvider(hook);
        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IDbDataWriterProvider>();
        await using var connection = await OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();

        var token = await writer.Write(connection, transaction, [AliceOwnsWorkspace], [], default);
        hook.Announced.Should().BeEmpty("the caller has not committed yet");
        await transaction.CommitAsync();
        await writer.NotifyCommitted(token);

        hook.Announced.Should().Equal(token);
        hook.Seen.Should().Equal(true);
    }

    [Fact]
    public async Task A_delete_in_the_callers_transaction_is_announced_by_NotifyCommitted()
    {
        var hook = new CheckingHook();
        await using var provider = BuildProvider(hook);
        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IDbDataWriterProvider>();
        await writer.Write([AliceOwnsWorkspace], [], default);
        hook.Announced.Clear();
        hook.Armed = true;
        await using var connection = await OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();

        var token = await writer.Delete(connection, transaction, RevokeAliceOwnership, default);
        hook.Announced.Should().BeEmpty("the caller has not committed yet");
        await transaction.CommitAsync();
        await writer.NotifyCommitted(token);

        hook.Announced.Should().Equal(token);
        hook.Seen.Should().Equal(false);
    }

    private async Task<DbConnection> OpenConnection()
    {
        var connection = (DbConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await connection.OpenAsync();
        return connection;
    }
}
