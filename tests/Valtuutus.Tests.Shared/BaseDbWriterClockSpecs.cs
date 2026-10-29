using System.Data.Common;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Data;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// A database writer records its transaction's creation time from the registered
/// <see cref="TimeProvider"/>.
/// </summary>
public abstract class BaseDbWriterClockSpecs : BaseWriterClockSpecs
{
    protected BaseDbWriterClockSpecs(IDatabaseFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task A_write_records_its_transaction_at_the_time_provider_time()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var token = await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>()
            .Write([AliceOwnsWorkspace], [], default);

        (await ReadCreatedAt(token.Value)).Should().Be(Time.GetUtcNow());
    }

    private async Task<DateTimeOffset> ReadCreatedAt(string transactionId)
    {
        await using var connection = (DbConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT created_at FROM transactions WHERE id = @id";
        var id = command.CreateParameter();
        id.ParameterName = "@id";
        id.Value = transactionId;
        command.Parameters.Add(id);

        return await command.ExecuteScalarAsync() switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            var value => throw new InvalidOperationException($"Unexpected created_at value: {value}")
        };
    }
}
