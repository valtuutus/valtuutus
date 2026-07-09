using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Postgres;
using Valtuutus.Lang;

// AOT spike (#218): exercises Npgsql 9.0.3 under NativeAOT for the exact paths this codebase
// uses -- connection open, binary COPY writes (relations + jsonb attributes via
// PostgresDataWriterProvider), and parameterized reads through the Check engine.
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__valtuutuspg")
    ?? (args.Length > 0 ? args[0] : throw new InvalidOperationException(
        "Provide a Postgres connection string via the ConnectionStrings__valtuutuspg env var or as the first argument."));

await using (var setup = new NpgsqlConnection(connectionString))
{
    await setup.OpenAsync();
    await using var cmd = setup.CreateCommand();
    cmd.CommandText = """
        DROP TABLE IF EXISTS relation_tuples, attributes, transactions CASCADE;

        CREATE TABLE attributes (
            id bigint NOT NULL GENERATED ALWAYS AS IDENTITY,
            entity_type character varying(256) NOT NULL,
            entity_id character varying(64) NOT NULL,
            attribute character varying(64) NOT NULL,
            value jsonb NOT NULL,
            created_tx_id char(26) NOT NULL,
            deleted_tx_id char(26),
            PRIMARY KEY (id)
        );
        CREATE UNIQUE INDEX unique_attributes ON attributes (entity_type, entity_id, attribute) WHERE deleted_tx_id IS NULL;

        CREATE TABLE relation_tuples (
            id bigint NOT NULL GENERATED ALWAYS AS IDENTITY,
            entity_type character varying(256) NOT NULL,
            entity_id character varying(64) NOT NULL,
            relation character varying(64) NOT NULL,
            subject_type character varying(256) NOT NULL,
            subject_id character varying(64) NOT NULL,
            subject_relation character varying(64) NOT NULL,
            created_tx_id char(26) NOT NULL,
            deleted_tx_id char(26),
            PRIMARY KEY (id)
        );

        CREATE TABLE transactions (
            id char(26) NOT NULL,
            created_at timestamptz NOT NULL,
            PRIMARY KEY (id)
        );
        """;
    await cmd.ExecuteNonQueryAsync();
}

var assembly = Assembly.GetExecutingAssembly();
var resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith("schema.vtt"));
using var schemaStream = assembly.GetManifestResourceStream(resourceName)!;

await using var sp = new ServiceCollection()
    .AddValtuutusCore(schemaStream, SchemaFunctionsGen.All)
    .AddPostgres(_ => () => new NpgsqlConnection(connectionString))
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

Console.WriteLine("Postgres AOT smoke check passed.");
return 0;
