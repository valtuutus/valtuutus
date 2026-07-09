using Bogus;
using System.Text.Json.Nodes;
using Valtuutus.Core;

namespace Valtuutus.Seeder;

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

public enum ProjectStatus
{
    Inactive,
    Active,
}

public record Project
{
    public Guid Id { get; set; }
    public required Organization Org { get; set; }
    public required Team Team { get; set; }
    public bool Public { get; set; }
    
    public ProjectStatus Status { get; set; }
    public User[] Members { get; set; } = [];
}

public static class Seeder
{
    public static (List<RelationTuple> Relations, List<AttributeTuple> Attributes) GenerateData()
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
                (f, o) => f.PickRandom(o.Org.Members.Concat(o.Org.Admins).ToArray(),
                    f.Random.Int(5, o.Org.Members.Length)).ToArray())
            .Generate(organizations.Count * 20);

        var projects = new Faker<Project>()
            .RuleFor(x => x.Id, f => f.Random.Guid())
            .RuleFor(x => x.Org, f => f.PickRandom(organizations))
            .RuleFor(x => x.Public, f => f.Random.Bool())
            .RuleFor(x => x.Status, f => f.PickRandom<ProjectStatus>())
            .RuleFor(x => x.Team, (f, o) => f.PickRandom(teams.Where(t => t.Org.Id == o.Org.Id)))
            .RuleFor(x => x.Members,
                (f, o) => f.PickRandom(o.Org.Members.Concat(o.Org.Admins).ToArray(),
                    f.Random.Int(5, o.Org.Members.Length)).ToArray())
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
            attributes.Add(new AttributeTuple("project", project.Id.ToString(), "status", JsonValue.Create((int)project.Status)));
        }

        // Deterministic group for reflexive fast-path benchmark validation.
        // Uses hardcoded IDs to avoid disturbing the Faker random seed.
        const string reflexiveGroupId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string benchmarkUserId  = "3fca4119-3bda-4370-13cd-a3d317459c73";
        // NOTE: The reflexive group tuple below is now dead data. The schema was simplified
        // from relation member @user @group#member; to relation member @user; to fix a
        // StackOverflowException in the schema reader. This tuple (group-nested-in-group)
        // is no longer valid under the schema, and no benchmark queries reflexiveGroupId.
        // Kept for historical reference only.
        relations.Add(new RelationTuple("group", reflexiveGroupId, "member", "group", reflexiveGroupId, "member"));
        relations.Add(new RelationTuple("group", reflexiveGroupId, "member", "user", benchmarkUserId));

        // Diamond-pattern benchmark data.
        // One group whose member is benchmarkUser.
        // Five folders each having that group as both owner AND editor — creates a
        // TTU diamond: both branches resolve to (group, diamondGroupId, member, [benchmarkUserId]).
        const string diamondGroupId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        const string diamondFolder1 = "cccccccc-cccc-cccc-cccc-cccccccccc01";
        const string diamondFolder2 = "cccccccc-cccc-cccc-cccc-cccccccccc02";
        const string diamondFolder3 = "cccccccc-cccc-cccc-cccc-cccccccccc03";
        const string diamondFolder4 = "cccccccc-cccc-cccc-cccc-cccccccccc04";
        const string diamondFolder5 = "cccccccc-cccc-cccc-cccc-cccccccccc05";

        relations.Add(new RelationTuple("group", diamondGroupId, "member", "user", benchmarkUserId));

        foreach (var folderId in new[] { diamondFolder1, diamondFolder2, diamondFolder3, diamondFolder4, diamondFolder5 })
        {
            relations.Add(new RelationTuple("folder", folderId, "owner", "group", diamondGroupId, "member"));
            relations.Add(new RelationTuple("folder", folderId, "editor", "group", diamondGroupId, "member"));
        }

        // Fan-out benchmark data. project.reviewers has two indirect variants (team#member,
        // group#member) — neither is the "user" subject type directly, so both require a
        // dependent query, giving LookupRelationCore's inner loop two genuinely concurrent
        // branches to fire instead of one.
        const string fanoutProjectId = "dddddddd-dddd-dddd-dddd-dddddddddd01";
        const string fanoutTeamId    = "dddddddd-dddd-dddd-dddd-dddddddddd02";
        const string fanoutGroupId   = "dddddddd-dddd-dddd-dddd-dddddddddd03";

        relations.Add(new RelationTuple("project", fanoutProjectId, "reviewers", "team", fanoutTeamId, "member"));
        relations.Add(new RelationTuple("project", fanoutProjectId, "reviewers", "group", fanoutGroupId, "member"));
        relations.Add(new RelationTuple("team", fanoutTeamId, "member", "user", benchmarkUserId));
        relations.Add(new RelationTuple("group", fanoutGroupId, "member", "user", benchmarkUserId));

        return (relations, attributes);
    }
}