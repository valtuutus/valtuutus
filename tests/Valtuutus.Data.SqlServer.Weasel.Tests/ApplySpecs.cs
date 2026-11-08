using System.Data;
using AwesomeAssertions;
using JasperFx;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Weasel.Core;

namespace Valtuutus.Data.SqlServer.Weasel.Tests;

[Collection("SqlServerWeasel")]
public sealed class ApplySpecs(SqlServerWeaselFixture fixture)
{
    private const string CustomLockId = "custom-valtuutus-lock";

    private static async Task Lock(SqlConnection connection, string procedure)
    {
        await using var command = new SqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Resource", CustomLockId);
        command.Parameters.AddWithValue("@LockOwner", "Session");
        if (procedure == "sp_getapplock")
        {
            command.Parameters.AddWithValue("@LockMode", "Exclusive");
            command.Parameters.AddWithValue("@LockTimeout", 0);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SchemaPatchDifference> DifferenceAsync(string cs, ValtuutusSqlServerOptions o)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        return (await SchemaMigration.DetermineAsync(connection, SqlServerSchema.Tables(o))).Difference;
    }

    [Fact]
    public async Task FailingStatement_FailsTheApply()
    {
        var o = SqlServerWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using (var connection = new SqlConnection(cs))
        {
            await connection.OpenAsync();
            await ScriptInstaller.Exec(connection, $"CREATE SCHEMA [{o.Schema}]");
            // A view under the table's name makes the table creation a no-op and the index
            // statements that follow fail.
            await ScriptInstaller.Exec(connection, $"CREATE VIEW [{o.Schema}].[{o.RelationsTableName}] AS SELECT 1 AS id");
        }
        await using var sp = SqlServerWeaselFixture.BuildServices(cs, o);

        var act = () => sp.ApplyValtuutusSchemaAsync();

        await act.Should().ThrowAsync<SqlException>();
    }

    [Fact]
    public async Task ApplyingTwice_LeavesNoDifference()
    {
        var cs = await fixture.CreateDatabaseAsync();
        await using var sp = SqlServerWeaselFixture.BuildServices(cs, SqlServerWeaselFixture.CustomNames);
        await sp.ApplyValtuutusSchemaAsync();
        await sp.ApplyValtuutusSchemaAsync();

        (await DifferenceAsync(cs, SqlServerWeaselFixture.CustomNames)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task InitialScriptOnly_IsUpgradedToCurrentModel()
    {
        var o = SqlServerWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using (var connection = new SqlConnection(cs))
        {
            await connection.OpenAsync();
            await ScriptInstaller.InstallAsync(connection, o, onlyFirst: 1);
        }
        (await DifferenceAsync(cs, o)).Should().NotBe(SchemaPatchDifference.None);

        await using var sp = SqlServerWeaselFixture.BuildServices(cs, o);
        await sp.ApplyValtuutusSchemaAsync();

        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task ConsumerAddedIndex_SurvivesApply()
    {
        var o = SqlServerWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var sp = SqlServerWeaselFixture.BuildServices(cs, o);
        await sp.ApplyValtuutusSchemaAsync();
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await ScriptInstaller.Exec(connection, $"CREATE INDEX consumer_idx ON [{o.Schema}].[{o.RelationsTableName}] (subject_id)");

        await sp.ApplyValtuutusSchemaAsync();

        await using var c = new SqlCommand("SELECT COUNT(*) FROM sys.indexes WHERE name = 'consumer_idx'", connection);
        ((int)(await c.ExecuteScalarAsync())!).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentApplies_BothSucceed()
    {
        var o = SqlServerWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var a = SqlServerWeaselFixture.BuildServices(cs, o);
        await using var b = SqlServerWeaselFixture.BuildServices(cs, o);

        await Task.WhenAll(a.ApplyValtuutusSchemaAsync(), b.ApplyValtuutusSchemaAsync());

        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task NonProviderConnection_IsRejected()
    {
        var connection = Substitute.For<IDbConnection>();
        await using var sp = SqlServerWeaselFixture.BuildServices("unused", new ValtuutusSqlServerOptions(), o => o.ConnectionFactory = _ => () => connection);

        var act = () => sp.ApplyValtuutusSchemaAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{connection.GetType().FullName}*");
        connection.Received(1).Dispose();
    }

    [Fact]
    public async Task ConnectionFactoryOverride_IsUsed()
    {
        var o = SqlServerWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var sp = SqlServerWeaselFixture.BuildServices("Server=127.0.0.1,1;Connect Timeout=1", o,
            options => options.ConnectionFactory = _ => () => new SqlConnection(cs));

        await sp.ApplyValtuutusSchemaAsync();

        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task HostedService_AppliesOnStart()
    {
        var o = SqlServerWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var sp = SqlServerWeaselFixture.BuildServices(cs, o);

        foreach (var service in sp.GetServices<IHostedService>())
            await service.StartAsync(CancellationToken.None);

        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task Apply_WaitsForLockHolder()
    {
        var o = SqlServerWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var holder = new SqlConnection(cs);
        await holder.OpenAsync();
        await Lock(holder, "sp_getapplock");
        await using var sp = SqlServerWeaselFixture.BuildServices(cs, o, options => options.LockId = CustomLockId);

        var apply = sp.ApplyValtuutusSchemaAsync();
        (await Task.WhenAny(apply, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(apply);

        await Lock(holder, "sp_releaseapplock");
        await apply;

        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task Apply_FailsWhenLockHeldPastTimeout()
    {
        var o = SqlServerWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var holder = new SqlConnection(cs);
        await holder.OpenAsync();
        await Lock(holder, "sp_getapplock");
        await using var sp = SqlServerWeaselFixture.BuildServices(cs, o, options =>
        {
            options.LockId = CustomLockId;
            options.LockTimeout = TimeSpan.FromSeconds(1);
        });

        var act = () => sp.ApplyValtuutusSchemaAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{CustomLockId}*");

        await Lock(holder, "sp_releaseapplock");
        await sp.ApplyValtuutusSchemaAsync();
        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task Apply_IsCancelledWhileWaitingForLock()
    {
        var o = SqlServerWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync();
        await using var holder = new SqlConnection(cs);
        await holder.OpenAsync();
        await Lock(holder, "sp_getapplock");
        await using var sp = SqlServerWeaselFixture.BuildServices(cs, o, options => options.LockId = CustomLockId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var act = () => sp.ApplyValtuutusSchemaAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        await Lock(holder, "sp_releaseapplock");
        await sp.ApplyValtuutusSchemaAsync();
        (await DifferenceAsync(cs, o)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public void AutoCreateAll_IsRefused()
    {
        var act = () => SqlServerWeaselFixture.BuildServices("Server=unused", new ValtuutusSqlServerOptions(), o => o.AutoCreate = AutoCreate.All);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ApplyWithoutOptIn_Throws()
    {
        await using var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => sp.ApplyValtuutusSchemaAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AddSqlServerWeasel*");
    }
}
