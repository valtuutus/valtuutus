using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data;
using Valtuutus.Data.Caching;
using Valtuutus.Data.InMemory;
using Valtuutus.Data.MySql;
using Valtuutus.Data.Postgres;
using Valtuutus.Data.SqlServer;
using Valtuutus.Lang;

// Exercises the library the way a Native AOT app uses it and exits non-zero on any wrong answer.
var failures = new List<string>();
void Expect<T>(string what, T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected)) failures.Add($"{what}: expected {expected}, got {actual}");
}

await using var schema = typeof(Program).Assembly.GetManifestResourceStream("Valtuutus.AotSmoke.schema.vtt")
    ?? throw new InvalidOperationException("schema.vtt is not embedded");
var services = new ServiceCollection();
services.AddFusionCache();
services.AddValtuutusCore(schema, SchemaFunctionsGen.All).AddInMemory().AddCaching();
await using var provider = services.BuildServiceProvider();

await using (var scope = provider.CreateAsyncScope())
{
    var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
    await writer.Write(
        [
            new RelationTuple(SchemaConstsGen.Workspace.Name, "1", SchemaConstsGen.Workspace.Relations.Owner, SchemaConstsGen.User.Name, "ana"),
            new RelationTuple(SchemaConstsGen.Team.Name, "ops", SchemaConstsGen.Team.Relations.Member, SchemaConstsGen.User.Name, "bia"),
            new RelationTuple(SchemaConstsGen.Workspace.Name, "1", SchemaConstsGen.Workspace.Relations.Member, SchemaConstsGen.Team.Name, "ops", SchemaConstsGen.Team.Relations.Member),
        ],
        [
            new AttributeTuple(SchemaConstsGen.Workspace.Name, "1", SchemaConstsGen.Workspace.Attributes.Public, System.Text.Json.Nodes.JsonValue.Create(true)),
            new AttributeTuple(SchemaConstsGen.Workspace.Name, "1", SchemaConstsGen.Workspace.Attributes.Status, System.Text.Json.Nodes.JsonValue.Create(1)),
        ], default);
}

await using (var scope = provider.CreateAsyncScope())
{
    var check = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
    CheckRequest Request(string permission, string user) =>
        new(SchemaConstsGen.Workspace.Name, "1", permission, SchemaConstsGen.User.Name, user);

    Expect("owner views", (await check.Check(Request(SchemaConstsGen.Workspace.Permissions.View, "ana"), default)).Allowed, true);
    Expect("team member views a public workspace", (await check.Check(Request(SchemaConstsGen.Workspace.Permissions.View, "bia"), default)).Allowed, true);
    Expect("stranger can't view", (await check.Check(Request(SchemaConstsGen.Workspace.Permissions.View, "caio"), default)).Allowed, false);
    Expect("owner edits an active workspace (fn)", (await check.Check(Request(SchemaConstsGen.Workspace.Permissions.Edit, "ana"), default)).Allowed, true);
    Expect("cached answer", (await check.Check(Request(SchemaConstsGen.Workspace.Permissions.View, "ana"), default)).Allowed, true);
    Expect("explain", (await check.Explain(Request(SchemaConstsGen.Workspace.Permissions.View, "bia"), default)).Result, true);

    var permissions = await check.SubjectPermission(new SubjectPermissionRequest
    {
        EntityType = SchemaConstsGen.Workspace.Name, EntityId = "1",
        SubjectType = SchemaConstsGen.User.Name, SubjectId = "ana",
    }, default);
    Expect("subject permission edit", permissions.Permissions.GetValueOrDefault(SchemaConstsGen.Workspace.Permissions.Edit), true);

    var entities = await scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>().LookupEntity(
        new LookupEntityRequest(SchemaConstsGen.Workspace.Name, SchemaConstsGen.Workspace.Permissions.View, SchemaConstsGen.User.Name, "bia"), default);
    Expect("lookup entity", string.Join(",", entities.EntityIds), "1");

    var subjects = await scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>().Lookup(
        new LookupSubjectRequest(SchemaConstsGen.Workspace.Name, SchemaConstsGen.Workspace.Permissions.View, SchemaConstsGen.User.Name, "1"), default);
    Expect("lookup subject", string.Join(",", subjects.SubjectIds.Order()), "ana,bia");
}

// The database providers build their readers and writers without touching a server; under AOT that
// still runs their catalogs, statement compilation and data source setup.
ResolveProvider(s => s.AddPostgres(_ => () => new NpgsqlConnection("Host=localhost;Username=u;Password=p;Database=d")), "postgres");
ResolveProvider(s => s.AddMySql(_ => () => new MySqlConnection("Server=localhost;User Id=u;Password=p;Database=d"), new ValtuutusMySqlOptions(MySqlServerType.MySql)), "mysql");
ResolveProvider(s => s.AddSqlServer(_ => () => new SqlConnection("Server=localhost;User Id=u;Password=p;Database=d;TrustServerCertificate=true")), "sqlserver");

void ResolveProvider(Func<IServiceCollection, IValtuutusDataBuilder> add, string name)
{
    try
    {
        var dbServices = new ServiceCollection();
        dbServices.AddValtuutusCore("entity user {} entity doc { relation owner @user; permission view := owner; }");
        add(dbServices);
        using var dbProvider = dbServices.BuildServiceProvider();
        using var scope = dbProvider.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<IDataReaderProvider>();
        _ = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        _ = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
    }
    catch (Exception e)
    {
        failures.Add($"{name} provider: {e.GetType().Name}: {e.Message}");
    }
}

// With a connection string, each database provider installs its migration scripts on that (empty)
// database and answers real checks and lookups. CI passes them from service containers.
await CheckDatabase("postgres", "VALTUUTUS_AOT_POSTGRES", cs => new NpgsqlConnection(cs),
    (s, cs) => s.AddPostgres(_ => () => new NpgsqlConnection(cs)), typeof(ValtuutusPostgresOptions));
await CheckDatabase("mysql", "VALTUUTUS_AOT_MYSQL", cs => new MySqlConnection(cs),
    (s, cs) => s.AddMySql(_ => () => new MySqlConnection(cs), new ValtuutusMySqlOptions(MySqlServerType.MySql)), typeof(ValtuutusMySqlOptions));
await CheckDatabase("sqlserver", "VALTUUTUS_AOT_SQLSERVER", cs => new SqlConnection(cs),
    (s, cs) => s.AddSqlServer(_ => () => new SqlConnection(cs)), typeof(ValtuutusSqlServerOptions));

async Task CheckDatabase(string name, string variable, Func<string, System.Data.Common.DbConnection> connect,
    Func<IServiceCollection, string, IValtuutusDataBuilder> add, Type providerType)
{
    var connectionString = Environment.GetEnvironmentVariable(variable);
    if (string.IsNullOrEmpty(connectionString))
    {
        Console.WriteLine($"{name}: skipped ({variable} not set)");
        return;
    }

    try
    {
        await using (var connection = connect(connectionString))
        {
            await connection.OpenAsync();
            var assembly = providerType.Assembly;
            foreach (var script in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                await using var stream = assembly.GetManifestResourceStream(script)!;
                using var reader = new StreamReader(stream);
                await using var command = connection.CreateCommand();
                command.CommandText = await reader.ReadToEndAsync();
                await command.ExecuteNonQueryAsync();
            }
        }

        await using var dbSchema = typeof(Program).Assembly.GetManifestResourceStream("Valtuutus.AotSmoke.schema.vtt")!;
        var dbServices = new ServiceCollection();
        dbServices.AddValtuutusCore(dbSchema, SchemaFunctionsGen.All);
        add(dbServices, connectionString);
        await using var dbProvider = dbServices.BuildServiceProvider();
        await using var scope = dbProvider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>().Write(
            [new RelationTuple(SchemaConstsGen.Workspace.Name, "1", SchemaConstsGen.Workspace.Relations.Owner, SchemaConstsGen.User.Name, "ana")],
            [new AttributeTuple(SchemaConstsGen.Workspace.Name, "1", SchemaConstsGen.Workspace.Attributes.Status, System.Text.Json.Nodes.JsonValue.Create(1))],
            default);

        var check = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
        CheckRequest Request(string permission, string user) =>
            new(SchemaConstsGen.Workspace.Name, "1", permission, SchemaConstsGen.User.Name, user);
        Expect($"{name}: owner views", (await check.Check(Request(SchemaConstsGen.Workspace.Permissions.View, "ana"), default)).Allowed, true);
        Expect($"{name}: stranger can't view", (await check.Check(Request(SchemaConstsGen.Workspace.Permissions.View, "caio"), default)).Allowed, false);
        Expect($"{name}: owner edits an active workspace (fn)", (await check.Check(Request(SchemaConstsGen.Workspace.Permissions.Edit, "ana"), default)).Allowed, true);
        var entities = await scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>().LookupEntity(
            new LookupEntityRequest(SchemaConstsGen.Workspace.Name, SchemaConstsGen.Workspace.Permissions.View, SchemaConstsGen.User.Name, "ana"), default);
        Expect($"{name}: lookup entity", string.Join(",", entities.EntityIds), "1");
        Console.WriteLine($"{name}: checked");
    }
    catch (Exception e)
    {
        failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
    }
}

if (failures.Count == 0)
{
    Console.WriteLine("AOT smoke test passed.");
    return 0;
}

foreach (var failure in failures) Console.Error.WriteLine(failure);
return 1;
