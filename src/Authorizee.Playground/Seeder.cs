using System.Text.Json.Nodes;
using Authorizee.Core;
using Authorizee.Data;
using Bogus;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Authorizee.Api;

public record User
{
    public Guid Id { get; set; }
}

public record Organization
{
    public Guid Id { get; set; }
    public User[] Admins { get; set; } = [];
    public User[] Members { get; set; } = [];
}

public record Team
{
    public Guid Id { get; set; }
    public Organization Org { get; set; }
    public User Owner { get; set; }
    public User[] Members { get; set; } = [];
}

public record Project
{
    public Guid Id { get; set; }
    public Organization Org { get; set; }
    public Team Team { get; set; }
    public bool Public { get; set; }
    public User[] Members { get; set; } = [];
}

public static class Seeder
{
    public static async Task Seed(IConfiguration configuration)
    {
        Randomizer.Seed = new Random(1500);

        var users = new Faker<User>()
            .RuleFor(x => x.Id, f => f.Random.Guid())
            .Generate(100_000);

        var organizations = new Faker<Organization>()
            .RuleFor(x => x.Id, f => f.Random.Guid())
            .RuleFor(x => x.Admins, f => f.PickRandom(users, f.Random.Int(5, 25)).ToArray())
            .RuleFor(x => x.Members, f => f.PickRandom(users, f.Random.Int(1000, 50_000)).ToArray())
            .Generate(50);

        var teams = new Faker<Team>()
            .RuleFor(x => x.Id, f => f.Random.Guid())
            .RuleFor(x => x.Org, f => f.PickRandom(organizations))
            .RuleFor(x => x.Owner, (f, o) => f.PickRandom(o.Org.Members.Concat(o.Org.Admins).ToArray()))
            .RuleFor(x => x.Members, (f, o) => f.PickRandom(o.Org.Members.Concat(o.Org.Admins).ToArray(), f.Random.Int(20, 100)).ToArray())
            .Generate(organizations.Count * 20);

        var projects = new Faker<Project>()
            .RuleFor(x => x.Id, f => f.Random.Guid())
            .RuleFor(x => x.Org, f => f.PickRandom(organizations))
            .RuleFor(x => x.Public, f => f.Random.Bool())
            .RuleFor(x => x.Team, (f, o) => f.PickRandom(teams.Where(t => t.Org.Id == o.Org.Id)))
            .RuleFor(x => x.Members, (f, o) => f.PickRandom(o.Org.Members.Concat(o.Org.Admins).ToArray(), f.Random.Int(50, 1000)).ToArray())
            .Generate(organizations.Count * 100);


        var relations = new List<RelationTuple>(500_000);
        var attributes = new List<AttributeTuple>(organizations.Count * 100);

        foreach (var org in organizations)
        {
            relations.AddRange(org.Admins.Select(u =>
                new RelationTuple("organization", org.Id.ToString(), "admin", "user", u.Id.ToString())));

            relations.AddRange(org.Members.Select(u =>
                new RelationTuple("organization", org.Id.ToString(), "member", "user", u.Id.ToString())));
        }

        foreach (var team in teams)
        {
            relations.Add(new RelationTuple("team", team.Id.ToString(), "owner", "user", team.Owner.Id.ToString()));
            relations.Add(new RelationTuple("team", team.Id.ToString(), "org", "organization", team.Org.Id.ToString()));
            relations.AddRange(team.Members.Select(u =>
                new RelationTuple("team", team.Id.ToString(), "member", "user", u.Id.ToString())));
        }

        foreach (var project in projects)
        {
            relations.Add(new RelationTuple("project", project.Id.ToString(), "org", "organization",
                project.Org.Id.ToString()));
            relations.Add(new RelationTuple("project", project.Id.ToString(), "team", "team",
                project.Team.Id.ToString()));
            relations.Add(new RelationTuple("project", project.Id.ToString(), "member", "team",
                project.Team.Id.ToString(), "member"));
            relations.AddRange(project.Members.Select(u =>
                new RelationTuple("project", project.Id.ToString(), "member", "user", u.Id.ToString())));
            attributes.Add(new AttributeTuple("project", project.Id.ToString(), "public",
                JsonValue.Create(project.Public)));
        }

        var connString = configuration.GetConnectionString("Db");
        
        await using var connection = new NpgsqlConnection(connString);
        await connection.OpenAsync();
        
        await using (var writer = await connection.BeginBinaryImportAsync(
                   "copy public.relation_tuples (entity_type, entity_id, relation, subject_type, subject_id, subject_relation) from STDIN (FORMAT BINARY)"))
        {
            foreach (var record in relations)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(record.EntityType);
                await writer.WriteAsync(record.EntityId);
                await writer.WriteAsync(record.Relation);
                await writer.WriteAsync(record.SubjectType);
                await writer.WriteAsync(record.SubjectId);
                await writer.WriteAsync(record.SubjectRelation);
            }
        
            await writer.CompleteAsync();
        }
        
        await using (var writer = await connection.BeginBinaryImportAsync(
                         "copy public.attributes (entity_type, entity_id, attribute, value) from STDIN (FORMAT BINARY)"))
        {
            foreach (var record in attributes)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(record.EntityType);
                await writer.WriteAsync(record.EntityId);
                await writer.WriteAsync(record.Attribute);
                await writer.WriteAsync(record.Value.ToJsonString(), NpgsqlDbType.Jsonb);
            }
        
            await writer.CompleteAsync();
        }
        
    }
}