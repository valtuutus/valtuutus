using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.InMemory;
using Valtuutus.Lang;

// AOT spike (#218): exercises Valtuutus.Core + Valtuutus.Lang (Antlr4.Runtime.Standard) schema
// parsing/compilation and the Check engine, backed by the InMemory provider so this binary has
// no dependency on Npgsql/SqlClient. schema.vtt's custom `fn` is compiled at build time by
// Valtuutus.Lang.SourceGen (#216) into SchemaFunctionsGen, wired in below via AddValtuutusCore's
// compiledFunctions param -- this is the AOT-safe path (bypasses the runtime Expression-tree
// compilation that ParameterIdFnNode.GetExpression would otherwise use for `fn` bodies).
var assembly = Assembly.GetExecutingAssembly();
var resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith("schema.vtt"));
using var schemaStream = assembly.GetManifestResourceStream(resourceName)!;

await using var sp = new ServiceCollection()
    .AddValtuutusCore(schemaStream, SchemaFunctionsGen.All)
    .AddInMemory()
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

Console.WriteLine("Core AOT smoke check passed.");
return 0;
