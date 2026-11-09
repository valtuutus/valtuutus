using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// A relation may point at entities of different types: a document is assigned to people or to
/// service accounts. Each subject is evaluated as what it is, and an arrow over such a relation asks
/// every target for the member, which each defines its own way.
/// </summary>
public abstract class BaseMixedRelationTargetsSpecs : IAsyncLifetime
{
    private const string Schema = """
        entity user {}
        entity service_account {
            relation owner @user;
        }
        entity team {
            relation member @user @service_account;
            permission owner := member;
        }
        entity doc {
            relation assignee @user @service_account;
            relation editor @user @team#member;
            relation holder @service_account @team;
            permission edit := assignee or editor;
            permission manage := holder.owner;
        }
        """;

    private static readonly RelationTuple[] Tuples =
    [
        new("doc", "1", "assignee", "user", "ana"),
        new("doc", "1", "assignee", "service_account", "deployer"),
        new("team", "ops", "member", "service_account", "builder"),
        new("team", "ops", "member", "user", "bia"),
        new("doc", "1", "editor", "team", "ops", "member"),
        new("service_account", "deployer", "owner", "user", "caio"),
        new("doc", "1", "holder", "service_account", "deployer"),
        new("doc", "2", "holder", "team", "ops"),
    ];

    private readonly List<IAsyncDisposable> _owned = [];

    protected BaseMixedRelationTargetsSpecs(IDatabaseFixture fixture) => Fixture = fixture;

    protected IDatabaseFixture Fixture { get; }

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public async Task DisposeAsync()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) await _owned[i].DisposeAsync();
    }

    [Theory]
    [InlineData("user", "ana", true)]
    [InlineData("service_account", "deployer", true)]
    [InlineData("service_account", "builder", true)]
    [InlineData("user", "bia", true)]
    [InlineData("user", "caio", false)]
    [InlineData("service_account", "nobody", false)]
    public async Task Check_evaluates_each_subject_as_its_own_type(string subjectType, string subjectId, bool expected)
    {
        var services = await Seeded();

        var result = await services.GetRequiredService<ICheckEngine>().Check(
            new CheckRequest("doc", "1", "edit", subjectType, subjectId), default);

        result.Allowed.Should().Be(expected);
    }

    [Theory]
    [InlineData("1", "caio", true)]
    [InlineData("2", "bia", true)]
    [InlineData("1", "bia", false)]
    [InlineData("2", "caio", false)]
    public async Task An_arrow_asks_each_target_for_the_member_it_defines(string doc, string user, bool expected)
    {
        var services = await Seeded();

        var result = await services.GetRequiredService<ICheckEngine>().Check(
            new CheckRequest("doc", doc, "manage", "user", user), default);

        result.Allowed.Should().Be(expected);
    }

    [Fact]
    public async Task LookupEntity_finds_entities_through_targets_of_every_type()
    {
        var services = await Seeded();
        var engine = services.GetRequiredService<ILookupEntityEngine>();

        var bySa = await engine.LookupEntity(new LookupEntityRequest("doc", "edit", "service_account", "builder"), default);
        var managedByCaio = await engine.LookupEntity(new LookupEntityRequest("doc", "manage", "user", "caio"), default);
        var managedByBia = await engine.LookupEntity(new LookupEntityRequest("doc", "manage", "user", "bia"), default);

        bySa.EntityIds.Should().BeEquivalentTo(["1"]);
        managedByCaio.EntityIds.Should().BeEquivalentTo(["1"]);
        managedByBia.EntityIds.Should().BeEquivalentTo(["2"]);
    }

    [Fact]
    public async Task LookupSubject_returns_only_subjects_of_the_requested_type()
    {
        var services = await Seeded();
        var engine = services.GetRequiredService<ILookupSubjectEngine>();

        var accounts = await engine.Lookup(new LookupSubjectRequest("doc", "edit", "service_account", "1"), default);
        var users = await engine.Lookup(new LookupSubjectRequest("doc", "edit", "user", "1"), default);
        var managers = await engine.Lookup(new LookupSubjectRequest("doc", "manage", "user", "1"), default);

        accounts.SubjectIds.Should().BeEquivalentTo(["deployer", "builder"]);
        users.SubjectIds.Should().BeEquivalentTo(["ana", "bia"]);
        managers.SubjectIds.Should().BeEquivalentTo(["caio"]);
    }

    private async Task<IServiceProvider> Seeded()
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        AddSpecificProvider(services);
        var provider = services.BuildServiceProvider();
        _owned.Add(provider);
        var scope = provider.CreateAsyncScope();
        _owned.Add(scope);
        await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>().Write(Tuples, [], default);
        return scope.ServiceProvider;
    }
}
