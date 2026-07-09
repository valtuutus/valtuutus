using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.SqlServer;
using Valtuutus.Lang;

// AOT spike (#218): exercises Microsoft.Data.SqlClient 6.0.1 under NativeAOT -- not officially
// declared AOT-compatible by Microsoft. Covers connection open, SqlBulkCopy (relation/attribute
// writes via SqlServerDataWriterProvider), and parameterized reads through the Check engine.
// Auth paths (Kerberos/SSPI) aren't hit here since this uses SQL auth, so a clean run here does
// NOT clear those paths -- only the ones this codebase actually uses.
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__valtuutusmssql")
    ?? (args.Length > 0 ? args[0] : throw new InvalidOperationException(
        "Provide a SqlServer connection string via the ConnectionStrings__valtuutusmssql env var or as the first argument."));

var targetBuilder = new SqlConnectionStringBuilder(connectionString);
var databaseName = targetBuilder.InitialCatalog;
targetBuilder.InitialCatalog = "master";

await using (var master = new SqlConnection(targetBuilder.ConnectionString))
{
    await master.OpenAsync();
    await using var createDb = master.CreateCommand();
    createDb.CommandText = $"""
        IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = '{databaseName}')
            CREATE DATABASE [{databaseName}];
        """;
    await createDb.ExecuteNonQueryAsync();
}

await using (var setup = new SqlConnection(connectionString))
{
    await setup.OpenAsync();

    string[] batches =
    [
        """
        IF OBJECT_ID('relation_tuples', 'U') IS NOT NULL DROP TABLE relation_tuples;
        IF OBJECT_ID('attributes', 'U') IS NOT NULL DROP TABLE attributes;
        IF OBJECT_ID('transactions', 'U') IS NOT NULL DROP TABLE transactions;
        IF TYPE_ID('dbo.TVP_ListIds') IS NOT NULL DROP TYPE dbo.TVP_ListIds;
        """,
        """
        CREATE TYPE dbo.TVP_ListIds AS TABLE (
            id nvarchar(64) NOT NULL,
            INDEX tvp_id (id)
        );
        """,
        """
        CREATE TABLE attributes (
            id bigint IDENTITY (1, 1) NOT NULL,
            entity_type nvarchar(256) NOT NULL,
            entity_id nvarchar(64) NOT NULL,
            attribute nvarchar(64) NOT NULL,
            value nvarchar(256) NOT NULL,
            created_tx_id nchar(26) NOT NULL,
            deleted_tx_id nchar(26),
            CONSTRAINT PK_attributes PRIMARY KEY CLUSTERED (id ASC)
        );
        """,
        "CREATE UNIQUE NONCLUSTERED INDEX IX_UniqueAttribute ON attributes (entity_id, entity_type, attribute) WHERE deleted_tx_id IS NULL;",
        """
        CREATE TABLE relation_tuples (
            id bigint IDENTITY (1, 1) NOT NULL,
            entity_type nvarchar(256) NOT NULL,
            entity_id nvarchar(64) NOT NULL,
            relation nvarchar(64) NOT NULL,
            subject_type nvarchar(256) NOT NULL,
            subject_id nvarchar(64) NOT NULL,
            subject_relation nvarchar(64) NULL,
            created_tx_id nchar(26) NOT NULL,
            deleted_tx_id nchar(26),
            CONSTRAINT PK_relation_tuples PRIMARY KEY CLUSTERED (id ASC)
        );
        """,
        """
        CREATE TABLE transactions (
            id nchar(26) NOT NULL,
            created_at datetime2(7) NOT NULL,
            CONSTRAINT PK_transactions PRIMARY KEY CLUSTERED (id ASC)
        );
        """,
    ];

    foreach (var batch in batches)
    {
        await using var cmd = setup.CreateCommand();
        cmd.CommandText = batch;
        await cmd.ExecuteNonQueryAsync();
    }
}

var assembly = Assembly.GetExecutingAssembly();
var resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith("schema.vtt"));
using var schemaStream = assembly.GetManifestResourceStream(resourceName)!;

await using var sp = new ServiceCollection()
    .AddValtuutusCore(schemaStream, SchemaFunctionsGen.All)
    .AddSqlServer(_ => () => new SqlConnection(connectionString))
    .Services
    .BuildServiceProvider();

var writer = sp.GetRequiredService<IDataWriterProvider>();
var engine = sp.GetRequiredService<ICheckEngine>();

await writer.Write(
    [new RelationTuple("document", "doc-1", "owner", "user", "alice")],
    [new AttributeTuple("document", "doc-1", "archived", JsonValue.Create(false))],
    default);

var aliceCanEdit = await engine.Check(new CheckRequest("document", "doc-1", "edit", "user", "alice"), default);
var bobCanView = await engine.Check(new CheckRequest("document", "doc-1", "view", "user", "bob"), default);

Console.WriteLine($"alice can edit doc-1: {aliceCanEdit}");
Console.WriteLine($"bob can view doc-1: {bobCanView}");

if (aliceCanEdit != true || bobCanView != false)
{
    Console.Error.WriteLine("FAIL: unexpected authorization result under AOT.");
    return 1;
}

Console.WriteLine("SqlServer AOT smoke check passed.");
return 0;
