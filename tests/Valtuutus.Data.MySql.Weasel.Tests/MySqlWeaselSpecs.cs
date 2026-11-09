using System.Data;
using AwesomeAssertions;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using NSubstitute;
using Weasel.Core;

namespace Valtuutus.Data.MySql.Weasel.Tests;

public abstract class MySqlWeaselSpecs(MySqlWeaselFixtureBase fixture)
{
    // Named locks are server-wide and every test process shares the server, so the name is this
    // process's own.
    private static readonly string CustomLockId = $"custom-valtuutus-lock-{Guid.NewGuid():N}";

    public static TheoryData<string> Names => ["default", "custom"];

    private ValtuutusMySqlOptions Options(string names, string database) =>
        names == "custom" ? fixture.CustomNames(database) : fixture.DefaultNames;

    private static async Task<SchemaPatchDifference> DifferenceAsync(string cs, ValtuutusMySqlOptions o, string database)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        return (await SchemaMigration.DetermineAsync(connection, MySqlSchema.Tables(o, database))).Difference;
    }

    private static async Task<object?> Scalar(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", CustomLockId);
        return await command.ExecuteScalarAsync();
    }

    [Theory, MemberData(nameof(Names))]
    public async Task ScriptInstall_HasNoDifference(string names)
    {
        var (db, cs) = await fixture.CreateDatabaseAsync();
        var options = Options(names, db);
        await using (var connection = new MySqlConnection(cs))
        {
            await connection.OpenAsync();
            await ScriptInstaller.InstallAsync(connection, options, db);
        }

        (await DifferenceAsync(cs, options, db)).Should().Be(SchemaPatchDifference.None);
    }

    [Theory, MemberData(nameof(Names))]
    public async Task WeaselInstall_MatchesScriptInstall(string names)
    {
        var (scriptDb, scriptCs) = await fixture.CreateDatabaseAsync();
        var (weaselDb, weaselCs) = await fixture.CreateDatabaseAsync();
        await using var scripted = new MySqlConnection(scriptCs);
        await scripted.OpenAsync();
        await ScriptInstaller.InstallAsync(scripted, Options(names, scriptDb), scriptDb);
        await using (var sp = fixture.BuildServices(weaselCs, Options(names, weaselDb)))
            await sp.ApplyValtuutusSchemaAsync();
        await using var weasel = new MySqlConnection(weaselCs);
        await weasel.OpenAsync();

        (await SchemaSnapshot.TakeAsync(weasel, weaselDb, Options(names, weaselDb)))
            .Should().Equal(await SchemaSnapshot.TakeAsync(scripted, scriptDb, Options(names, scriptDb)));
    }

    [Fact]
    public async Task FailingStatement_FailsTheApply()
    {
        var (_, cs) = await fixture.CreateDatabaseAsync();
        await using (var connection = new MySqlConnection(cs))
        {
            await connection.OpenAsync();
            // A view under the table's name makes CREATE TABLE IF NOT EXISTS a no-op and the
            // index statements that follow fail.
            await ScriptInstaller.Exec(connection, "CREATE VIEW relation_tuples AS SELECT 1 AS id");
        }
        await using var sp = fixture.BuildServices(cs, fixture.DefaultNames);

        var act = () => sp.ApplyValtuutusSchemaAsync();

        await act.Should().ThrowAsync<MySqlException>();
    }

    [Fact]
    public async Task ApplyingTwice_LeavesNoDifference()
    {
        var (db, cs) = await fixture.CreateDatabaseAsync();
        await using var sp = fixture.BuildServices(cs, fixture.DefaultNames);
        await sp.ApplyValtuutusSchemaAsync();
        await sp.ApplyValtuutusSchemaAsync();

        (await DifferenceAsync(cs, fixture.DefaultNames, db)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task WrongPrimaryKeyOrder_IsRepaired()
    {
        var (db, cs) = await fixture.CreateDatabaseAsync();
        await using (var connection = new MySqlConnection(cs))
        {
            await connection.OpenAsync();
            await ScriptInstaller.InstallAsync(connection, fixture.DefaultNames, db);
            await ScriptInstaller.Exec(connection, "ALTER TABLE attributes DROP PRIMARY KEY, ADD PRIMARY KEY (id, entity_type, entity_id, attribute)");
        }
        (await DifferenceAsync(cs, fixture.DefaultNames, db)).Should().NotBe(SchemaPatchDifference.None);

        await using var sp = fixture.BuildServices(cs, fixture.DefaultNames);
        await sp.ApplyValtuutusSchemaAsync();

        (await DifferenceAsync(cs, fixture.DefaultNames, db)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task ConsumerAddedIndex_SurvivesApply()
    {
        var (_, cs) = await fixture.CreateDatabaseAsync();
        await using var sp = fixture.BuildServices(cs, fixture.DefaultNames);
        await sp.ApplyValtuutusSchemaAsync();
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await ScriptInstaller.Exec(connection, "CREATE INDEX consumer_idx ON relation_tuples (subject_id)");

        await sp.ApplyValtuutusSchemaAsync();

        await using var c = new MySqlCommand("SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND index_name = 'consumer_idx'", connection);
        Convert.ToInt64(await c.ExecuteScalarAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentApplies_BothSucceed()
    {
        var (db, cs) = await fixture.CreateDatabaseAsync();
        await using var a = fixture.BuildServices(cs, fixture.DefaultNames);
        await using var b = fixture.BuildServices(cs, fixture.DefaultNames);

        await Task.WhenAll(a.ApplyValtuutusSchemaAsync(), b.ApplyValtuutusSchemaAsync());

        (await DifferenceAsync(cs, fixture.DefaultNames, db)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task NonProviderConnection_IsRejected()
    {
        var connection = Substitute.For<IDbConnection>();
        await using var sp = fixture.BuildServices("unused", fixture.DefaultNames, o => o.ConnectionFactory = _ => () => connection);

        var act = () => sp.ApplyValtuutusSchemaAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{connection.GetType().FullName}*");
        connection.Received(1).Dispose();
    }

    [Fact]
    public async Task ConnectionFactoryOverride_IsUsed()
    {
        var (db, cs) = await fixture.CreateDatabaseAsync();
        await using var sp = fixture.BuildServices("Server=127.0.0.1;Port=1;Connection Timeout=1", fixture.DefaultNames,
            options => options.ConnectionFactory = _ => () => new MySqlConnection(cs));

        await sp.ApplyValtuutusSchemaAsync();

        (await DifferenceAsync(cs, fixture.DefaultNames, db)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task HostedService_AppliesOnStart()
    {
        var (db, cs) = await fixture.CreateDatabaseAsync();
        await using var sp = fixture.BuildServices(cs, fixture.DefaultNames);

        foreach (var service in sp.GetServices<IHostedService>())
            await service.StartAsync(CancellationToken.None);

        (await DifferenceAsync(cs, fixture.DefaultNames, db)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task Apply_WaitsForLockHolder()
    {
        var (db, cs) = await fixture.CreateDatabaseAsync();
        await using var holder = new MySqlConnection(cs);
        await holder.OpenAsync();
        (await Scalar(holder, "SELECT GET_LOCK(@name, 10)")).Should().Be(1L);
        await using var sp = fixture.BuildServices(cs, fixture.DefaultNames, options => options.LockId = CustomLockId);

        var apply = sp.ApplyValtuutusSchemaAsync();
        (await Task.WhenAny(apply, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(apply);

        await Scalar(holder, "SELECT RELEASE_LOCK(@name)");
        await apply;

        (await DifferenceAsync(cs, fixture.DefaultNames, db)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task Apply_FailsWhenLockHeldPastTimeout()
    {
        var (db, cs) = await fixture.CreateDatabaseAsync();
        await using var holder = new MySqlConnection(cs);
        await holder.OpenAsync();
        (await Scalar(holder, "SELECT GET_LOCK(@name, 10)")).Should().Be(1L);
        await using var sp = fixture.BuildServices(cs, fixture.DefaultNames, options =>
        {
            options.LockId = CustomLockId;
            options.LockTimeout = TimeSpan.FromSeconds(1);
        });

        var act = () => sp.ApplyValtuutusSchemaAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{CustomLockId}*");

        await Scalar(holder, "SELECT RELEASE_LOCK(@name)");
        await sp.ApplyValtuutusSchemaAsync();
        (await DifferenceAsync(cs, fixture.DefaultNames, db)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task Apply_IsCancelledWhileWaitingForLock()
    {
        var (db, cs) = await fixture.CreateDatabaseAsync();
        await using var holder = new MySqlConnection(cs);
        await holder.OpenAsync();
        (await Scalar(holder, "SELECT GET_LOCK(@name, 10)")).Should().Be(1L);
        await using var sp = fixture.BuildServices(cs, fixture.DefaultNames, options => options.LockId = CustomLockId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var act = () => sp.ApplyValtuutusSchemaAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        await Scalar(holder, "SELECT RELEASE_LOCK(@name)");
        await sp.ApplyValtuutusSchemaAsync();
        (await DifferenceAsync(cs, fixture.DefaultNames, db)).Should().Be(SchemaPatchDifference.None);
    }

    [Fact]
    public void AutoCreateAll_IsRefused()
    {
        var act = () => fixture.BuildServices("Server=unused", fixture.DefaultNames, o => o.AutoCreate = AutoCreate.All);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ApplyWithoutOptIn_Throws()
    {
        await using var sp = new ServiceCollection().BuildServiceProvider();

        var act = () => sp.ApplyValtuutusSchemaAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AddMySqlWeasel*");
    }
}

public sealed class MySqlServerWeaselSpecs(MySqlWeaselFixture fixture) : MySqlWeaselSpecs(fixture), IClassFixture<MySqlWeaselFixture>;

public sealed class MariaDbServerWeaselSpecs(MariaDbWeaselFixture fixture) : MySqlWeaselSpecs(fixture), IClassFixture<MariaDbWeaselFixture>;
