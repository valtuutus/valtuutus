using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

/// <summary>
/// The 2-hop userset / tuple-to-userset join picks its direction per execution: driven from the
/// entity's tuples when the entity has few, from the subject's memberships when the entity has many
/// (unless a membership turns up among the entity tuples scanned first).
/// Both branches, hit and miss, for the userset join (folder.owner), the TTU fast path (doc.edit)
/// and the fused TTU leaf (doc.edit_or_own), each tokenless (resolved in the first batch) and with
/// an explicit token (single-op path).
/// </summary>
[Collection("PostgreSqlSpec")]
public sealed class TwoHopJoinDirectionSpecs : IAsyncLifetime
{
    private const string Schema = """
        entity user {}
        entity group {
            relation member @user;
        }
        entity organization {
            relation admin @user;
        }
        entity folder {
            relation owner @group#member;
            permission view := owner;
        }
        entity doc {
            relation parent @organization;
            relation owner @user;
            permission edit := parent.admin;
            permission edit_or_own := parent.admin or owner;
        }
        """;

    // Well past the entity-side scan limit, so these entities take the subject-driven branch and
    // these subjects' many memberships are what the entity-driven branch must not scan.
    private const int Many = 100;

    public TwoHopJoinDirectionSpecs(PostgresFixture fixture) => Fixture = fixture;

    private PostgresFixture Fixture { get; }

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly object[][] Cases =
    [
        // folder with many owner groups (subject-driven branch)
        ["folder", "big", "view", "member-of-last-owner", true],
        ["folder", "big", "view", "member-of-nothing", false],
        ["folder", "big", "view", "member-of-every-owner", true],
        // folder with few owner groups, subject in many groups (entity-driven branch)
        ["folder", "small", "view", "in-many-groups-incl-owner", true],
        ["folder", "small", "view", "in-many-groups-not-owner", false],
        // doc with many parents (subject-driven branch), TTU fast path and fused TTU leaf
        ["doc", "big", "edit", "admin-of-last-parent", true],
        ["doc", "big", "edit", "admin-of-nothing", false],
        ["doc", "big", "edit_or_own", "admin-of-last-parent", true],
        ["doc", "big", "edit_or_own", "admin-of-nothing", false],
        // doc with one parent, subject admin of many orgs (entity-driven branch)
        ["doc", "small", "edit", "admin-of-many-incl-parent", true],
        ["doc", "small", "edit", "admin-of-many-not-parent", false],
        ["doc", "small", "edit_or_own", "admin-of-many-incl-parent", true],
        ["doc", "small", "edit_or_own", "admin-of-many-not-parent", false],
    ];

    // The default limit takes each branch as the comments above say; 1 sends every check with any
    // entity tuple to the subject's side, and a limit past every entity's tuples never does.
    public static IEnumerable<object?[]> CasesAtEachScanLimit() =>
        new int?[] { null, 1, 1000 }.SelectMany(limit => Cases.Select(c => c.Append(limit).ToArray()));

    [Theory]
    [MemberData(nameof(CasesAtEachScanLimit))]
    public async Task Check_matches_expected_result_in_both_join_directions(string entityType, string entityId,
        string permission, string subjectId, bool expected, int? scanLimit)
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        var options = new ValtuutusPostgresOptions("public", "transactions", "relation_tuples", "attributes");
        services.AddPostgres(_ => dbFactory, scanLimit is { } k ? options with { TwoHopEntityScanLimit = k } : options)
            .AddConcurrentQueryLimit(3);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var snapToken = await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>()
            .Write(Tuples(), [], default);
        var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();

        (await engine.Check(new CheckRequest(entityType, entityId, permission, "user", subjectId), default))
            .Allowed.Should().Be(expected, "tokenless");
        (await engine.Check(new CheckRequest(entityType, entityId, permission, "user", subjectId,
                snapToken: snapToken), default))
            .Allowed.Should().Be(expected, "explicit token");
    }

    private static List<RelationTuple> Tuples()
    {
        var tuples = new List<RelationTuple>();
        for (var i = 0; i < Many; i++)
        {
            tuples.Add(new RelationTuple("folder", "big", "owner", "group", $"big-owner-{i}", "member"));
            tuples.Add(new RelationTuple("group", $"other-{i}", "member", "user", "in-many-groups-incl-owner"));
            tuples.Add(new RelationTuple("group", $"other-{i}", "member", "user", "in-many-groups-not-owner"));
            tuples.Add(new RelationTuple("doc", "big", "parent", "organization", $"big-parent-{i}"));
            tuples.Add(new RelationTuple("organization", $"other-org-{i}", "admin", "user", "admin-of-many-incl-parent"));
            tuples.Add(new RelationTuple("organization", $"other-org-{i}", "admin", "user", "admin-of-many-not-parent"));
        }
        tuples.Add(new RelationTuple("group", $"big-owner-{Many - 1}", "member", "user", "member-of-last-owner"));
        for (var i = 0; i < Many; i++)
            tuples.Add(new RelationTuple("group", $"big-owner-{i}", "member", "user", "member-of-every-owner"));
        tuples.Add(new RelationTuple("organization", $"big-parent-{Many - 1}", "admin", "user", "admin-of-last-parent"));

        tuples.Add(new RelationTuple("folder", "small", "owner", "group", "small-owner-a", "member"));
        tuples.Add(new RelationTuple("folder", "small", "owner", "group", "small-owner-b", "member"));
        tuples.Add(new RelationTuple("group", "small-owner-b", "member", "user", "in-many-groups-incl-owner"));
        tuples.Add(new RelationTuple("doc", "small", "parent", "organization", "small-parent"));
        tuples.Add(new RelationTuple("organization", "small-parent", "admin", "user", "admin-of-many-incl-parent"));
        return tuples;
    }
}
