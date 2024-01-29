using Authorizee.Core;
using Authorizee.Core.Configuration;
using Authorizee.Core.Data;
using Authorizee.Core.Schemas;
using Authorizee.Data;
using Authorizee.Data.Configuration;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDatabaseSetup(() => new NpgsqlConnection(builder.Configuration.GetConnectionString("Db")!));

builder.Services.AddSchemaConfiguration(c =>
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
            .WithAttribute("public", typeof(bool))
            .WithPermission("view",  
            PermissionNode.Union(
                PermissionNode.Leaf("org.admin"), 
                PermissionNode.Leaf("team.member"), 
                PermissionNode.Intersect("public", "org.member"))
            )
            .WithPermission("edit", PermissionNode.Union("org.admin", "team.member"))
            .WithPermission("delete", PermissionNode.Leaf("team.member"));
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
        async ([AsParameters] CheckRequest req, [FromServices] PermissionEngine service, CancellationToken ct) => await service.Check(req, ct))
    .WithName("Check Relation")
    .WithOpenApi();

app.Run();