using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Engines.LookupSubject.V2;

namespace Valtuutus.Data.InMemory.Tests.LookupSubjectV2;

public class LookupSubjectPlanCacheSpecs
{
    private const string Schema = """
        entity user {}
        entity service {}
        entity group {
            relation member @user @group#member;
        }
        entity project {
            relation owner @user;
            relation bot @service;
            relation viewer @group#member;
            permission automate := bot or owner;
        }
        """;

    private static LookupSubjectPlanCache Cache()
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddInMemory();
        return services.BuildServiceProvider().GetRequiredService<LookupSubjectPlanCache>();
    }

    [Fact]
    public void Same_key_returns_the_same_plan()
    {
        var cache = Cache();

        cache.GetOrCompile("project", "automate", "user")
            .Should().BeSameAs(cache.GetOrCompile("project", "automate", "user"));
    }

    [Fact]
    public void Subject_type_is_part_of_the_key()
    {
        var cache = Cache();

        cache.GetOrCompile("project", "automate", "user")
            .Should().NotBeSameAs(cache.GetOrCompile("project", "automate", "service"));
    }

    [Fact]
    public void Recursive_userset_compiles_its_hop_as_a_reference()
    {
        var plan = Cache().GetOrCompile("group", "member", "user");

        var hop = plan.Should().BeOfType<SetPlan>().Subject.Children.OfType<ExpandPlan>()
            .Should().ContainSingle().Subject;
        hop.EntityType.Should().Be("group");
        hop.Name.Should().Be("member");
    }

    private sealed class RecordingRewriter(List<string> calls, string tag) : ILookupSubjectPlanRewriter
    {
        public LookupPlan Rewrite(LookupPlan root, Valtuutus.Core.Schemas.Schema schema, string entityType, string name, string subjectType)
        {
            calls.Add($"{tag}:{entityType}.{name}:{subjectType}");
            return root;
        }
    }

    [Fact]
    public void Rewriters_run_in_registration_order_once_per_key()
    {
        var calls = new List<string>();
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddInMemory();
        services.AddSingleton<ILookupSubjectPlanRewriter>(new RecordingRewriter(calls, "a"));
        services.AddSingleton<ILookupSubjectPlanRewriter>(new RecordingRewriter(calls, "b"));
        var cache = services.BuildServiceProvider().GetRequiredService<LookupSubjectPlanCache>();

        cache.GetOrCompile("project", "automate", "user");
        cache.GetOrCompile("project", "automate", "user");

        calls.Should().Equal("a:project.automate:user", "b:project.automate:user");
    }
}
