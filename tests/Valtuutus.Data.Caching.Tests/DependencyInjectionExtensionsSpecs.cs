using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Xunit;

namespace Valtuutus.Data.Caching.Tests;

public class DependencyInjectionExtensionsSpecs
{
    private const string Schema = @"
        entity user {}
        entity project {
            relation owner @user;
            permission edit := owner;
        }
    ";

    [Fact]
    public void AddCaching_by_default_wraps_the_V2_engine()
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema);
        services.AddValtuutusData().AddCaching();

        GetUnkeyed<ICheckEngine>(services).ImplementationType.Should().Be(typeof(CachedCheckEngine));
        var innerType = GetKeyed<ICheckEngine>(services, Consts.InnerCheckEngineKey).KeyedImplementationType;
        innerType!.Name.Should().Be("CheckEngineV2");
    }

    [Fact]
    public void AddCaching_wraps_lookup_entity_and_lookup_subject_engines()
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema);
        services.AddValtuutusData().AddCaching();

        GetUnkeyed<ILookupEntityEngine>(services).ImplementationType.Should().Be(typeof(CachedLookupEntityEngine));
        GetKeyed<ILookupEntityEngine>(services, Consts.InnerLookupEntityEngineKey).KeyedImplementationType!.Name.Should().Be("LookupEntityEngineV2");

        GetUnkeyed<ILookupSubjectEngine>(services).ImplementationType.Should().Be(typeof(CachedLookupSubjectEngine));
        GetKeyed<ILookupSubjectEngine>(services, Consts.InnerLookupSubjectEngineKey).KeyedImplementationType!.Name.Should().Be("LookupSubjectEngineV2");
    }

    [Fact]
    public void AddCaching_throws_a_clear_error_when_no_engine_is_registered_yet()
    {
        var services = new ServiceCollection();

        var act = () => services.AddValtuutusData().AddCaching();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ICheckEngine*");
    }

    private static ServiceDescriptor GetUnkeyed<TService>(IServiceCollection services)
        => services.Last(d => d.ServiceType == typeof(TService) && !d.IsKeyedService);

    private static ServiceDescriptor GetKeyed<TService>(IServiceCollection services, string key)
        => services.Last(d => d.ServiceType == typeof(TService) && d.IsKeyedService && Equals(d.ServiceKey, key));
}
