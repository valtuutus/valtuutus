using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using Weasel.Core;

namespace Valtuutus.Data.SqlServer.Weasel.Tests;

public sealed class ContractSpecs(SqlServerWeaselFixture fixture) : IClassFixture<SqlServerWeaselFixture>
{
    public static TheoryData<string> Names => ["default", "custom"];

    private static ValtuutusSqlServerOptions Options(string names) =>
        names == "custom" ? SqlServerWeaselFixture.CustomNames : new ValtuutusSqlServerOptions();

    [Theory, MemberData(nameof(Names))]
    public async Task ScriptInstall_HasNoDifference(string names)
    {
        var options = Options(names);
        await using var connection = new SqlConnection(await fixture.CreateDatabaseAsync());
        await connection.OpenAsync();
        await ScriptInstaller.InstallAsync(connection, options);

        var migration = await SchemaMigration.DetermineAsync(connection, SqlServerSchema.Tables(options));

        migration.Difference.Should().Be(SchemaPatchDifference.None);
    }

    [Theory, MemberData(nameof(Names))]
    public async Task WeaselInstall_MatchesScriptInstall(string names)
    {
        var options = Options(names);
        await using var scripted = new SqlConnection(await fixture.CreateDatabaseAsync());
        await scripted.OpenAsync();
        await ScriptInstaller.InstallAsync(scripted, options);

        var weaselDb = await fixture.CreateDatabaseAsync();
        await using (var sp = SqlServerWeaselFixture.BuildServices(weaselDb, options))
            await sp.ApplyValtuutusSchemaAsync();
        await using var weasel = new SqlConnection(weaselDb);
        await weasel.OpenAsync();

        (await SchemaSnapshot.TakeAsync(weasel, options.Schema))
            .Should().Equal(await SchemaSnapshot.TakeAsync(scripted, options.Schema));
    }

    [Fact]
    public async Task WeaselInstall_KeepsScriptCollation_OnDatabaseWithOtherDefault()
    {
        var options = SqlServerWeaselFixture.CustomNames;
        var cs = await fixture.CreateDatabaseAsync("Latin1_General_100_CI_AS_SC_UTF8");
        await using (var sp = SqlServerWeaselFixture.BuildServices(cs, options))
            await sp.ApplyValtuutusSchemaAsync();
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();

        await using var c = new SqlCommand("""
            SELECT DISTINCT c.collation_name FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
            WHERE t.schema_id = SCHEMA_ID(@s) AND c.collation_name IS NOT NULL AND TYPE_NAME(c.user_type_id) = 'nvarchar'
            """, connection);
        c.Parameters.AddWithValue("s", options.Schema);
        var collations = new List<string>();
        await using (var r = await c.ExecuteReaderAsync())
            while (await r.ReadAsync()) collations.Add(r.GetString(0));

        collations.Should().Equal("SQL_Latin1_General_CP1_CI_AS");
    }
}
