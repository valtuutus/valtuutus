using AwesomeAssertions;
using Npgsql;
using Weasel.Core;

namespace Valtuutus.Data.Postgres.Weasel.Tests;

[Collection("PostgresWeasel")]
public sealed class ContractSpecs(PostgresWeaselFixture fixture)
{
    public static TheoryData<string> Names => ["default", "custom"];

    private static ValtuutusPostgresOptions Options(string names) =>
        names == "custom" ? PostgresWeaselFixture.CustomNames : new ValtuutusPostgresOptions();

    [Theory, MemberData(nameof(Names))]
    public async Task ScriptInstall_HasNoDifference(string names)
    {
        var options = Options(names);
        await using var connection = new NpgsqlConnection(await fixture.CreateDatabaseAsync());
        await connection.OpenAsync();
        await ScriptInstaller.InstallAsync(connection, options);

        var migration = await SchemaMigration.DetermineAsync(connection, PostgresSchema.Tables(options));

        migration.Difference.Should().Be(SchemaPatchDifference.None);
    }

    [Theory, MemberData(nameof(Names))]
    public async Task WeaselInstall_MatchesScriptInstall(string names)
    {
        var options = Options(names);
        await using var scripted = new NpgsqlConnection(await fixture.CreateDatabaseAsync());
        await scripted.OpenAsync();
        await ScriptInstaller.InstallAsync(scripted, options);

        var weaselDb = await fixture.CreateDatabaseAsync();
        await using (var sp = PostgresWeaselFixture.BuildServices(weaselDb, options))
            await sp.ApplyValtuutusSchemaAsync();
        await using var weasel = new NpgsqlConnection(weaselDb);
        await weasel.OpenAsync();

        (await SchemaSnapshot.TakeAsync(weasel, options.Schema))
            .Should().Equal(await SchemaSnapshot.TakeAsync(scripted, options.Schema));
    }
}
