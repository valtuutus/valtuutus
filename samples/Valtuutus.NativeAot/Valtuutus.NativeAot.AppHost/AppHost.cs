var builder = DistributedApplication.CreateBuilder(args);

var pgPassword = builder.AddParameter("postgres-password", secret: true);
var postgres = builder.AddPostgres("postgres", password: pgPassword, port: 5532)
    .WithImageTag("18-alpine");
var pgDb = postgres.AddDatabase("valtuutuspg");

var sqlPassword = builder.AddParameter("sqlserver-password", secret: true);
var sqlserver = builder.AddSqlServer("sqlserver", password: sqlPassword, port: 1533)
    .WithImageTag("2022-CU13-ubuntu-22.04");
var sqlDb = sqlserver.AddDatabase("valtuutusmssql");

// Core has no DB dependency (InMemory provider) -- runs standalone.
builder.AddProject<Projects.Valtuutus_NativeAot_Core>("core");

builder.AddProject<Projects.Valtuutus_NativeAot_Postgres>("aot-postgres")
    .WithReference(pgDb)
    .WaitFor(postgres);

builder.AddProject<Projects.Valtuutus_NativeAot_SqlServer>("aot-sqlserver")
    .WithReference(sqlDb)
    .WaitFor(sqlserver);

builder.Build().Run();
