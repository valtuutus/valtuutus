using Authorizee.Core;
using Authorizee.Core.Configuration;
using Authorizee.Core.Data;
using Authorizee.Core.Schemas;
using Authorizee.Data;
using Authorizee.Data.Configuration;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDatabaseSetup(builder.Configuration.GetConnectionString("Db")!);

builder.Services.AddTransient<IRelationTupleReader, RelationTupleReader>();
builder.Services.AddTransient<PermissionService>();

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
            .WithPermission("edit", PermissionNode.Or("org.admin", "owner"))
            .WithPermission("delete", PermissionNode.Or("org.admin", "owner"))
            .WithPermission("invite", PermissionNode.And("org.admin", PermissionNode.Or("owner", "member")))
            .WithPermission("remove_user", new PermissionNode("owner"))
        .WithEntity("project")
            .WithRelation("org", rc => rc.WithEntityType("organization"))
            .WithRelation("team", rc => rc.WithEntityType("team"))
            .WithPermission("view",  PermissionNode.Or("org.admin", "team.member"))
            .WithPermission("edit", PermissionNode.Or("org.admin", "team.member"))
            .WithPermission("delete", new PermissionNode("team.member"));
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
        ([AsParameters] CheckRequest req, [FromServices] PermissionService service) => { return service.Check(req); })
    .WithName("Check Relation")
    .WithOpenApi();

app.Run();