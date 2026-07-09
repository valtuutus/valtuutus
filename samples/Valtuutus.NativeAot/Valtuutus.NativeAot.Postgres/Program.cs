using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
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
var checkEngine = sp.GetRequiredService<ICheckEngine>();
var lookupEntityEngine = sp.GetRequiredService<ILookupEntityEngine>();
var lookupSubjectEngine = sp.GetRequiredService<ILookupSubjectEngine>();

await writer.Write(
    [
        new RelationTuple("document", "doc-1", "owner", "user", "alice"),
        new RelationTuple("document", "doc-2", "viewer", "user", "alice"),
        // diamond: folder-1.admin := owner and editor, both branches resolve through group#member
        new RelationTuple("folder", "folder-1", "owner", "group", "group-1", "member"),
        new RelationTuple("folder", "folder-1", "editor", "group", "group-1", "member"),
        new RelationTuple("group", "group-1", "member", "user", "alice"),
    ],
    [
        new AttributeTuple("document", "doc-1", "archived", JsonValue.Create(false)),
        new AttributeTuple("document", "doc-2", "archived", JsonValue.Create(false)),
    ],
    default);

var aliceCanEdit = await checkEngine.Check(new CheckRequest("document", "doc-1", "edit", "user", "alice"), default);
var bobCanView = await checkEngine.Check(new CheckRequest("document", "doc-1", "view", "user", "bob"), default);

var alicePermissionsOnDoc1 = await checkEngine.SubjectPermission(new SubjectPermissionRequest
{
    EntityType = "document", EntityId = "doc-1", SubjectType = "user", SubjectId = "alice",
}, default);

var docsAliceCanView = await lookupEntityEngine.LookupEntity(
    new LookupEntityRequest("document", "view", "user", "alice"), default);

var usersWhoCanViewDoc1 = await lookupSubjectEngine.Lookup(
    new LookupSubjectRequest("document", "view", "user", "doc-1"), default);

// diamond: two branches (owner, editor) both resolve group-1's member relation for the same subject
var aliceIsFolderAdmin = await checkEngine.Check(new CheckRequest("folder", "folder-1", "admin", "user", "alice"), default);
var foldersAliceCanAdmin = await lookupEntityEngine.LookupEntity(
    new LookupEntityRequest("folder", "admin", "user", "alice"), default);

Console.WriteLine($"alice can edit doc-1: {aliceCanEdit}");
Console.WriteLine($"bob can view doc-1: {bobCanView}");
Console.WriteLine($"alice's permissions on doc-1: {string.Join(", ", alicePermissionsOnDoc1.Select(kv => $"{kv.Key}={kv.Value}"))}");
Console.WriteLine($"docs alice can view: {string.Join(", ", docsAliceCanView.EntityIds)}");
Console.WriteLine($"users who can view doc-1: {string.Join(", ", usersWhoCanViewDoc1)}");
Console.WriteLine($"alice is folder-1 admin (diamond): {aliceIsFolderAdmin}");
Console.WriteLine($"folders alice can admin (diamond): {string.Join(", ", foldersAliceCanAdmin.EntityIds)}");

if (aliceCanEdit != true
    || bobCanView != false
    || alicePermissionsOnDoc1["view"] != true
    || alicePermissionsOnDoc1["edit"] != true
    || !docsAliceCanView.EntityIds.Contains("doc-1")
    || !docsAliceCanView.EntityIds.Contains("doc-2")
    || !usersWhoCanViewDoc1.Contains("alice")
    || aliceIsFolderAdmin != true
    || !foldersAliceCanAdmin.EntityIds.Contains("folder-1"))
{
    Console.Error.WriteLine("FAIL: unexpected authorization result under AOT.");
    return 1;
}

Console.WriteLine("Postgres AOT smoke check passed.");
return 0;
