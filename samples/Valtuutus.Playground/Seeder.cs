using System.Text.Json.Nodes;
using Valtuutus.Core;
using Bogus;
using Dapper;
using Microsoft.Data.SqlClient;
using Npgsql;
using Valtuutus.Data.Db;

namespace Valtuutus.Api;

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
    public required Organization Org { get; set; }
    public required User Owner { get; set; }
    public User[] Members { get; set; } = [];
}

public record Project
{
    public Guid Id { get; set; }
    public required Organization Org { get; set; }
    public required Team Team { get; set; }
    public bool Public { get; set; }
    public User[] Members { get; set; } = [];
}

public static class Seeder
{
    private static (List<RelationTuple> Relations, List<AttributeTuple> Attribute) GenerateData()
    {
        Randomizer.Seed = new Random(1500);
        var users = new Faker<User>()
            .RuleFor(x => x.Id, f => f.Random.Guid())
            .Generate(50000);

        var organizations = new Faker<Organization>()
            .RuleFor(x => x.Id, f => f.Random.Guid())
            .RuleFor(x => x.Admins, f => f.PickRandom(users, f.Random.Int(5, 25)).ToArray())
            .RuleFor(x => x.Members, f => f.PickRandom(users, f.Random.Int(5, 100)).ToArray())
            .Generate(50);

        var teams = new Faker<Team>()
            .RuleFor(x => x.Id, f => f.Random.Guid())
            .RuleFor(x => x.Org, f => f.PickRandom(organizations))
            .RuleFor(x => x.Owner, (f, o) => f.PickRandom(o.Org.Members.Concat(o.Org.Admins).ToArray()))
            .RuleFor(x => x.Members,
                (f, o) => f.PickRandom(o.Org.Members.Concat(o.Org.Admins).ToArray(), f.Random.Int(5, o.Org.Members.Length)).ToArray())
            .Generate(organizations.Count * 20);

        var projects = new Faker<Project>()
            .RuleFor(x => x.Id, f => f.Random.Guid())
            .RuleFor(x => x.Org, f => f.PickRandom(organizations))
            .RuleFor(x => x.Public, f => f.Random.Bool())
            .RuleFor(x => x.Team, (f, o) => f.PickRandom(teams.Where(t => t.Org.Id == o.Org.Id)))
            .RuleFor(x => x.Members,
                (f, o) => f.PickRandom(o.Org.Members.Concat(o.Org.Admins).ToArray(), f.Random.Int(5, o.Org.Members.Length)).ToArray())
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
            relations.Add(new RelationTuple(" ", project.Id.ToString(), "member", "team",
                project.Team.Id.ToString(), "member"));
            relations.AddRange(project.Members.Select(u =>
                new RelationTuple("project", project.Id.ToString(), "member", "user", u.Id.ToString())));
            attributes.Add(new AttributeTuple("project", project.Id.ToString(), "public",
                JsonValue.Create(project.Public)));
        }

        return (relations, attributes);
    }

    public static async Task SeedPostgres(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();

        await using var connection = (NpgsqlConnection)factory();
        await connection.OpenAsync();

        var relationTuples = await connection.ExecuteScalarAsync<int>("select count(*) from public.relation_tuples");

        var attrs = await connection.ExecuteScalarAsync<int>("select count(*) from public.attributes");

        if (relationTuples > 0 && attrs > 0)
        {
            return;
        }

        var (relations, attributes) = GenerateData();

        var writer = scope.ServiceProvider.GetRequiredService<DataEngine>();
        await writer.Write(relations, attributes, default);

    }

    public static async Task SeedSqlServer(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();

        await using var connection = (SqlConnection)factory();
        await connection.OpenAsync();

        var relationTuples = await connection.ExecuteScalarAsync<int>("select count(*) from dbo.relation_tuples");

        var attrs = await connection.ExecuteScalarAsync<int>("select count(*) from dbo.attributes");

        if (relationTuples > 0 && attrs > 0)
        {
            return;
        }

        var (relations, attributes) = GenerateData();
        
        var writer = scope.ServiceProvider.GetRequiredService<DataEngine>();
        await writer.Write(relations, attributes, default);
    }

}