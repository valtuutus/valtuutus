#define postgres


using System.Diagnostics;
using Valtuutus.Api;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Schemas;
using Valtuutus.Data.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data;
using Valtuutus.Data.Caching;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")!;

var muxxer = ConnectionMultiplexer.Connect(redisConnectionString);
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

builder.Services.AddValtuutusCore(c =>
    {
        c
            .WithEntity("user")
            .WithEntity("organization")
            .WithRelation("admin", rc => rc.WithEntityType("user"))
            .WithRelation("member", rc => rc.WithEntityType("user"))
            .WithEntity("team")
            .WithRelation("owner", rc => rc.WithEntityType("user"))
            .WithRelation("member", rc => rc.WithEntityType("user"))
            .WithRelation("org", rc => rc.WithEntityType("organization"))
            .WithPermission("edit", PermissionNode.Union("org.admin", "owner"))
            .WithPermission("delete", PermissionNode.Union("org.admin", "owner"))
            .WithPermission("invite", PermissionNode.Intersect("org.admin", PermissionNode.Union("owner", "member")))
            .WithPermission("remove_user", PermissionNode.Leaf("owner"))
            .WithEntity("project")
            .WithRelation("org", rc => rc.WithEntityType("organization"))
            .WithRelation("team", rc => rc.WithEntityType("team"))
            .WithRelation("member", rc => rc.WithEntityType("team", "member").WithEntityType("user"))
            .WithAttribute("public", typeof(bool))
            .WithAttribute("status", typeof(string))
            .WithPermission("view",
                PermissionNode.Union(
                    PermissionNode.Leaf("org.admin"),
                    PermissionNode.Leaf("member"),
                    PermissionNode.Intersect("public", "org.member"))
            )
            .WithPermission("edit", PermissionNode.Intersect(
                PermissionNode.Union("org.admin", "team.member"),
                PermissionNode.AttributeStringExpression("status", status => status == "ativo"))
            )
            .WithPermission("delete", PermissionNode.Leaf("team.member"));
    })
#if postgres
    .AddPostgres(_ => () => new NpgsqlConnection(builder.Configuration.GetConnectionString("PostgresDb")!))
#else
.AddSqlServer(_ => () => new SqlConnection(builder.Configuration.GetConnectionString("SqlServerDb")!))
#endif
    .AddConcurrentQueryLimit(3)
    .AddCaching();

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
        async ([AsParameters] CheckRequest req, [FromServices] ICheckEngine service, CancellationToken ct) => await service.Check(req, ct))
    .WithName("Check Relation")
    .WithOpenApi();

app.MapPost("/lookup-entity",
        async ([FromBody] LookupEntityRequest req, [FromServices] ILookupEntityEngine service, CancellationToken ct) => await service.LookupEntity(req, ct))
    .WithName("Lookup entity")
    .WithOpenApi();

app.MapPost("/lookup-subject",
        async ([FromBody] LookupSubjectRequest req, [FromServices] ILookupSubjectEngine service, CancellationToken ct) => await service.Lookup(req, ct))
    .WithName("Lookup subject")
    .WithOpenApi();

app.MapPost("/subject-permission",
        async ([FromBody] SubjectPermissionRequest req, [FromServices] ICheckEngine service, CancellationToken ct) => await service.SubjectPermission(req, ct))
    .WithName("Subject permission")
    .WithOpenApi();

app.MapPost("/write",
        async ([FromBody] WriteRequest request, [FromServices] IDataWriterProvider writer, CancellationToken ct) => await writer.Write(request.Relations, request.Attributes, ct))
    .WithName("Write data")
    .WithOpenApi();



#if postgres
_ = Task.Run(async () => await Seeder.SeedPostgres(app.Services)); 
#else
_ = Task.Run(async () => await Seeder.SeedSqlServer(app.Services)); 
#endif
app.Run();

record WriteRequest(List<RelationTuple> Relations, List<AttributeTuple> Attributes);
