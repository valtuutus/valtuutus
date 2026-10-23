using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// The joined and multi-id statements join rows, so a dependency stored more than once (the same
    /// tuple written twice, a project naming the same team twice) multiplies the rows they return.
    /// Every engine still answers with each id once.
    /// </summary>
    public abstract class DuplicateDependencySpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
    {
        private const string Schema = """
            entity user {}
            entity team {
                relation member @user;
            }
            entity project {
                relation team @team;
                relation member @team#member;
                permission view := team.member;
                permission access := member;
            }
            """;

        private static readonly RelationTuple[] Tuples =
        [
            new("team", "t1", "member", "user", "u1"),
            new("team", "t1", "member", "user", "u1"),
            new("team", "t2", "member", "user", "u1"),
            new("team", "t2", "member", "user", "u2"),
            new("project", "p1", "team", "team", "t1"),
            new("project", "p1", "team", "team", "t1"),
            new("project", "p1", "team", "team", "t2"),
            new("project", "p2", "team", "team", "t1"),
            new("project", "p2", "team", "team", "t1"),
            new("project", "p1", "member", "team", "t1", "member"),
            new("project", "p1", "member", "team", "t1", "member"),
            new("project", "p1", "member", "team", "t2", "member"),
        ];

        public Task InitializeAsync() => fixture.ResetDatabaseAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        private async Task<(ServiceProvider Services, SnapToken Token)> Seed()
        {
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options)
                .AddConcurrentQueryLimit(3);
            var provider = services.BuildServiceProvider();
            var writer = provider.GetRequiredService<IDataWriterProvider>();
            // Written twice, so every tuple is stored at least twice.
            await writer.Write(Tuples, [], default);
            var token = await writer.Write(Tuples, [], default);
            return (provider, token);
        }

        [Fact]
        public async Task LookupEntity_returns_each_entity_once()
        {
            var (services, token) = await Seed();
            await using var _ = services;
            await using var scope = services.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

            foreach (var snapToken in new SnapToken?[] { null, token })
            {
                (await engine.LookupEntity(new LookupEntityRequest("project", "view", "user", "u1") { SnapToken = snapToken }, default))
                    .EntityIds.Should().Equal("p1", "p2");
                (await engine.LookupEntity(new LookupEntityRequest("project", "access", "user", "u1") { SnapToken = snapToken }, default))
                    .EntityIds.Should().Equal("p1");
                (await engine.LookupEntity(new LookupEntityRequest("project", "view", "user", "u2") { SnapToken = snapToken }, default))
                    .EntityIds.Should().Equal("p1");
            }
        }

        [Fact]
        public async Task LookupEntity_pages_over_each_entity_once()
        {
            var (services, token) = await Seed();
            await using var _ = services;
            await using var scope = services.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

            var seen = new List<string>();
            string? continuation = null;
            do
            {
                var page = await engine.LookupEntity(new LookupEntityRequest("project", "view", "user", "u1")
                {
                    SnapToken = token, PageSize = 1, ContinuationToken = continuation
                }, default);
                seen.AddRange(page.EntityIds);
                continuation = page.ContinuationToken;
            } while (continuation is not null && seen.Count <= 4);

            seen.Should().Equal("p1", "p2");
        }

        [Fact]
        public async Task LookupSubject_returns_each_subject_once()
        {
            var (services, token) = await Seed();
            await using var _ = services;
            await using var scope = services.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

            foreach (var snapToken in new SnapToken?[] { null, token })
            {
                (await engine.Lookup(new LookupSubjectRequest("project", "view", "user", "p1") { SnapToken = snapToken }, default))
                    .SubjectIds.Should().BeEquivalentTo(["u1", "u2"]);
                (await engine.Lookup(new LookupSubjectRequest("project", "view", "user", "p2") { SnapToken = snapToken }, default))
                    .SubjectIds.Should().BeEquivalentTo(["u1"]);
                (await engine.Lookup(new LookupSubjectRequest("project", "access", "user", "p1") { SnapToken = snapToken }, default))
                    .SubjectIds.Should().BeEquivalentTo(["u1", "u2"]);
            }
        }

        [Fact]
        public async Task Check_is_true_through_the_duplicated_dependency()
        {
            var (services, token) = await Seed();
            await using var _ = services;
            await using var scope = services.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();

            foreach (var snapToken in new SnapToken?[] { null, token })
            {
                (await engine.Check(new CheckRequest("project", "p1", "view", "user", "u1", snapToken: snapToken), default))
                    .Allowed.Should().BeTrue();
                (await engine.Check(new CheckRequest("project", "p2", "view", "user", "u1", snapToken: snapToken), default))
                    .Allowed.Should().BeTrue();
                (await engine.Check(new CheckRequest("project", "p1", "access", "user", "u1", snapToken: snapToken), default))
                    .Allowed.Should().BeTrue();
                (await engine.Check(new CheckRequest("project", "p2", "view", "user", "u2", snapToken: snapToken), default))
                    .Allowed.Should().BeFalse();
            }
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class DuplicateDependencySpecs(MySqlFixture fixture) : DuplicateDependencySpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class DuplicateDependencySpecs(MariaDbFixture fixture) : DuplicateDependencySpecsBase(fixture);
}
