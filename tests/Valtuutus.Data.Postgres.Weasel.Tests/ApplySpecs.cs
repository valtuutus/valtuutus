using System.Data;
using AwesomeAssertions;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Npgsql;
using Weasel.Core;

namespace Valtuutus.Data.Postgres.Weasel.Tests;

[Collection("PostgresWeasel")]
public sealed class ApplySpecs(PostgresWeaselFixture fixture)
{
    private const int CustomLockId = 424242;

    private static async Task Lock(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", (long)CustomLockId);
        await command.ExecuteScalarAsync();
    }

    private static async Task<SchemaPatchDifference> DifferenceAsync(string cs, ValtuutusPostgresOptions o)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        return (await SchemaMigration.DetermineAsync(connection, PostgresSchema.Tables(o))).Difference;
    }

    [Fact]
    public async Task FailingStatement_FailsTheApply()
    {
        var o = PostgresWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using (var connection = new NpgsqlConnection(cs))
        {
            await connection.OpenAsync();
            // A view under the table's name makes CREATE TABLE IF NOT EXISTS a no-op and the
            // index statements that follow fail.
            await ScriptInstaller.Exec(connection, $"CREATE SCHEMA \"{o.Schema}\"; CREATE VIEW \"{o.Schema}\".\"{o.RelationsTableName}\" AS SELECT 1 AS id");
        }
        await using var sp = PostgresWeaselFixture.BuildServices(cs, o);

        var act = () => sp.ApplyValtuutusSchemaAsync();

        await act.Should().ThrowAsync<SchemaMigrationException>();
    }

    [Fact]
    public async Task ApplyingTwice_LeavesNoDifference()
    {
        var cs = await fixture.CreateDatabaseAsync();
        await using var sp = PostgresWeaselFixture.BuildServices(cs, PostgresWeaselFixture.CustomNames);
        await sp.ApplyValtuutusSchemaAsync();
        await sp.ApplyValtuutusSchemaAsync();

        (await DifferenceAsync(cs, PostgresWeaselFixture.CustomNames)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task InitialScriptOnly_IsUpgradedToCurrentModel()
    {
        var o = PostgresWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using (var connection = new NpgsqlConnection(cs))
        {
            await connection.OpenAsync();
            await ScriptInstaller.InstallAsync(connection, o, onlyFirst: 1);
        }
        (await DifferenceAsync(cs, o)).Should().NotBe(SchemaPatchDifference.None);

        await using var sp = PostgresWeaselFixture.BuildServices(cs, o);
        await sp.ApplyValtuutusSchemaAsync();

        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task ConsumerAddedIndex_SurvivesApply()
    {
        var o = PostgresWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var sp = PostgresWeaselFixture.BuildServices(cs, o);
        await sp.ApplyValtuutusSchemaAsync();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await ScriptInstaller.Exec(connection, $"CREATE INDEX consumer_idx ON \"{o.Schema}\".\"{o.RelationsTableName}\" (subject_id)");

        await sp.ApplyValtuutusSchemaAsync();

        await using var c = new NpgsqlCommand("select count(*) from pg_indexes where indexname = 'consumer_idx'", connection);
        ((long)(await c.ExecuteScalarAsync())!).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentApplies_BothSucceed()
    {
        var o = PostgresWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var a = PostgresWeaselFixture.BuildServices(cs, o);
        await using var b = PostgresWeaselFixture.BuildServices(cs, o);

        await Task.WhenAll(a.ApplyValtuutusSchemaAsync(), b.ApplyValtuutusSchemaAsync());

        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task NonProviderConnection_IsRejected()
    {
        var connection = Substitute.For<IDbConnection>();
        await using var sp = PostgresWeaselFixture.BuildServices("unused", new ValtuutusPostgresOptions(), o => o.ConnectionFactory = _ => () => connection);

        var act = () => sp.ApplyValtuutusSchemaAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{connection.GetType().FullName}*");
        connection.Received(1).Dispose();
    }

    [Fact]
    public async Task ConnectionFactoryOverride_IsUsed()
    {
        var o = PostgresWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var sp = PostgresWeaselFixture.BuildServices("Host=127.0.0.1;Port=1;Timeout=1", o,
            options => options.ConnectionFactory = _ => () => new NpgsqlConnection(cs));

        await sp.ApplyValtuutusSchemaAsync();

        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task HostedService_AppliesOnStart()
    {
        var o = PostgresWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var sp = PostgresWeaselFixture.BuildServices(cs, o);

        foreach (var service in sp.GetServices<IHostedService>())
            await service.StartAsync(CancellationToken.None);

        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task Apply_WaitsForLockHolder()
    {
        var o = PostgresWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var holder = new NpgsqlConnection(cs);
        await holder.OpenAsync();
        await Lock(holder, "SELECT pg_advisory_lock(@id)");
        await using var sp = PostgresWeaselFixture.BuildServices(cs, o, options => options.LockId = CustomLockId);

        var apply = sp.ApplyValtuutusSchemaAsync();
        (await Task.WhenAny(apply, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(apply);

        await Lock(holder, "SELECT pg_advisory_unlock(@id)");
        await apply;

        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task Apply_FailsWhenLockHeldPastTimeout()
    {
        var o = PostgresWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var holder = new NpgsqlConnection(cs);
        await holder.OpenAsync();
        await Lock(holder, "SELECT pg_advisory_lock(@id)");
        await using var sp = PostgresWeaselFixture.BuildServices(cs, o, options =>
        {
            options.LockId = CustomLockId;
            options.LockTimeout = TimeSpan.FromSeconds(1);
        });

        var act = () => sp.ApplyValtuutusSchemaAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{CustomLockId}*");

        await Lock(holder, "SELECT pg_advisory_unlock(@id)");
        await sp.ApplyValtuutusSchemaAsync();
        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task Apply_IsCancelledWhileWaitingForLock()
    {
        var o = PostgresWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var holder = new NpgsqlConnection(cs);
        await holder.OpenAsync();
        await Lock(holder, "SELECT pg_advisory_lock(@id)");
        await using var sp = PostgresWeaselFixture.BuildServices(cs, o, options => options.LockId = CustomLockId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var act = () => sp.ApplyValtuutusSchemaAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        await Lock(holder, "SELECT pg_advisory_unlock(@id)");
        await sp.ApplyValtuutusSchemaAsync();
        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public void AutoCreateAll_IsRefused()
    {
        var act = () => PostgresWeaselFixture.BuildServices("Host=unused", new ValtuutusPostgresOptions(), o => o.AutoCreate = AutoCreate.All);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ApplyWithoutOptIn_Throws()
    {
        await using var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => sp.ApplyValtuutusSchemaAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AddPostgresWeasel*");
    }
}
