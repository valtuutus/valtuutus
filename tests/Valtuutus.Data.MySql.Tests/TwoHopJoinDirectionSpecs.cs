using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// The 2-hop userset / tuple-to-userset check is one free join, and the optimizer picks its
    /// direction per execution: from the entity's tuples when the entity has few, from the subject's
    /// memberships when the entity has many. Both shapes, hit and miss, for the userset join
    /// (folder.owner), the TTU fast path (doc.edit) and the fused TTU leaf (doc.edit_or_own), each
    /// tokenless and with an explicit token.
    /// </summary>
    public abstract class TwoHopJoinDirectionSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
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

        // Enough tuples that these entities favour starting from the subject, and that these
        // subjects' many memberships are what a walk starting from the entity must not scan.
        private const int Many = 100;

        public Task InitializeAsync() => fixture.ResetDatabaseAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        public static readonly TheoryData<string, string, string, string, bool> Cases = new()
        {
            // folder with many owner groups
            { "folder", "big", "view", "member-of-last-owner", true },
            { "folder", "big", "view", "member-of-nothing", false },
            { "folder", "big", "view", "member-of-every-owner", true },
            // folder with few owner groups, subject in many groups
            { "folder", "small", "view", "in-many-groups-incl-owner", true },
            { "folder", "small", "view", "in-many-groups-not-owner", false },
            // doc with many parents, TTU fast path and fused TTU leaf
            { "doc", "big", "edit", "admin-of-last-parent", true },
            { "doc", "big", "edit", "admin-of-nothing", false },
            { "doc", "big", "edit_or_own", "admin-of-last-parent", true },
            { "doc", "big", "edit_or_own", "admin-of-nothing", false },
            // doc with one parent, subject admin of many orgs
            { "doc", "small", "edit", "admin-of-many-incl-parent", true },
            { "doc", "small", "edit", "admin-of-many-not-parent", false },
            { "doc", "small", "edit_or_own", "admin-of-many-incl-parent", true },
            { "doc", "small", "edit_or_own", "admin-of-many-not-parent", false },
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task Check_matches_expected_result_in_both_join_directions(string entityType, string entityId,
            string permission, string subjectId, bool expected)
        {
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options).AddConcurrentQueryLimit(3);
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
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class TwoHopJoinDirectionSpecs(MySqlFixture fixture) : TwoHopJoinDirectionSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class TwoHopJoinDirectionSpecs(MariaDbFixture fixture) : TwoHopJoinDirectionSpecsBase(fixture);
}
