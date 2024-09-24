#define postgres


using System.Diagnostics;
using Valtuutus.Api;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Schemas;
using Valtuutus.Data.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using System.Reflection;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Lang;
using Valtuutus.Data;
using Valtuutus.Data.Caching;
using Valtuutus.Data.SqlServer;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")!;

var muxxer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
builder.Services.AddFusionCache()
    .WithSerializer(
        new FusionCacheSystemTextJsonSerializer()
        )
    .WithDistributedCache(
        new RedisCache(new RedisCacheOptions()
        {
            ConnectionMultiplexerFactory = () => Task.FromResult((IConnectionMultiplexer)muxxer)
        }))
    .WithBackplane(
        new RedisBackplane(new RedisBackplaneOptions()
        {
            ConnectionMultiplexerFactory = () => Task.FromResult((IConnectionMultiplexer)muxxer)
        }));


var schemaFilePath = Assembly.GetExecutingAssembly()
    .GetManifestResourceNames()
    .First(c => c.EndsWith("schema.vtt"));

var schema = Assembly.GetExecutingAssembly().GetManifestResourceStream(schemaFilePath)!;

builder.Services.AddValtuutusCore(schema)
#if postgres
    .AddPostgres(_ => () => new NpgsqlConnection(builder.Configuration.GetConnectionString("PostgresDb")!))
#else
.AddSqlServer(_ => () => new SqlConnection(builder.Configuration.GetConnectionString("SqlServerDb")!))
#endif
    .AddConcurrentQueryLimit(3)
    //.AddCaching()
;

Activity.DefaultIdFormat = ActivityIdFormat.W3C;


builder.Services
    .AddOpenTelemetry()
    .ConfigureResource((rb) => rb
        .AddService(serviceName: DefaultActivitySource.SourceName)
        .AddTelemetrySdk()
        .AddEnvironmentVariableDetector())
    .WithTracing(telemetry =>
    {
        telemetry
            .AddSource(DefaultActivitySource.SourceName)
            .AddSource(DefaultActivitySource.SourceNameInternal)
            .AddNpgsql()
            .AddFusionCacheInstrumentation()
            .AddAspNetCoreInstrumentation(o =>
            {
                o.RecordException = true;
            })
            .AddOtlpExporter();
    })
    .WithMetrics(telemetry =>
    {
        telemetry
            .AddMeter("Microsoft.AspNetCore.Hosting")
            .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
            .AddView("http-server-request-duration",
                new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = new double[] { 0, 0.005, 0.01, 0.025, 0.05,
                        0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 }
                })
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter();
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapGet("/check",
        ([FromQuery] string entityType, 
        [FromQuery] string entityId, 
        [FromQuery] string permission, 
        [FromQuery] string subjectType, 
        [FromQuery] string subjectId, 
        [FromQuery] string? subjectRelation, 
        [FromQuery] string? snapToken, 
        [FromServices] ICheckEngine service, CancellationToken ct) => service.Check(new CheckRequest(entityType, entityId, permission
            ,subjectType, subjectId, subjectRelation, snapToken is null ? null : new SnapToken(snapToken)), ct))
    .WithName("Check Relation")
    .WithOpenApi();

app.MapPost("/lookup-entity",
        ([FromBody] LookupEntityRequest req, [FromServices] ILookupEntityEngine service, CancellationToken ct) => service.LookupEntity(req, ct))
    .WithName("Lookup entity")
    .WithOpenApi();

app.MapPost("/lookup-subject",
        ([FromBody] LookupSubjectRequest req, [FromServices] ILookupSubjectEngine service, CancellationToken ct) => service.Lookup(req, ct))
    .WithName("Lookup subject")
    .WithOpenApi();

app.MapPost("/subject-permission",
        ([FromBody] SubjectPermissionRequest req, [FromServices] ICheckEngine service, CancellationToken ct) => service.SubjectPermission(req, ct))
    .WithName("Subject permission")
    .WithOpenApi();

app.MapPost("/write",
        ([FromBody] WriteRequest request, [FromServices] IDataWriterProvider writer, CancellationToken ct) => writer.Write(request.Relations, request.Attributes, ct))
    .WithName("Write data")
    .WithOpenApi();


app.MapPost("/delete",
    ([FromBody] DeleteFilter request, [FromServices] IDataWriterProvider writer, CancellationToken ct) =>
        writer.Delete(request, ct));



#if postgres
_ = Task.Run(async () => await Seeder.SeedPostgres(app.Services)); 
#else
_ = Task.Run(async () => await Seeder.SeedSqlServer(app.Services)); 
#endif
app.Run();

record WriteRequest(List<RelationTuple> Relations, List<AttributeTuple> Attributes);
