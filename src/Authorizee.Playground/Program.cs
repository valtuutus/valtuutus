using Authorizee.Core;
using Authorizee.Core.Data;
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapGet("/check", ([AsParameters] CheckRequest req, [FromServices]PermissionService service) =>
    {
        return service.Check(req);
    })
.WithName("Check Relation")
.WithOpenApi();

app.Run();