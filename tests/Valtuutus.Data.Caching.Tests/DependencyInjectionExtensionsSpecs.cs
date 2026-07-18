using FluentAssertions;
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
    public void AddCaching_without_CheckV2_wraps_the_V1_engine()
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema);
        services.AddValtuutusData().AddCaching();

        GetUnkeyed<ICheckEngine>(services).ImplementationType.Should().Be(typeof(CachedCheckEngine));
        GetKeyed<ICheckEngine>(services, Consts.InnerCheckEngineKey).KeyedImplementationType.Should().Be(typeof(CheckEngine));
    }

    [Fact]
    public void AddCaching_after_CheckV2_wraps_CheckEngineV2_instead_of_V1()
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema);
        services.AddValtuutusCheckV2();
        services.AddValtuutusData().AddCaching();

        GetUnkeyed<ICheckEngine>(services).ImplementationType.Should().Be(typeof(CachedCheckEngine));

        var innerType = GetKeyed<ICheckEngine>(services, Consts.InnerCheckEngineKey).KeyedImplementationType;
        innerType.Should().NotBeNull();
        innerType!.Name.Should().Be("CheckEngineV2");
        innerType.Should().NotBe(typeof(CheckEngine));
    }

    [Fact]
    public void AddCaching_before_CheckV2_loses_caching_documenting_the_required_order()
    {
        // AddValtuutusCheckV2 replaces the unkeyed ICheckEngine descriptor outright, so calling it after
        // AddCaching wipes CachedCheckEngine. AddCaching must be the last call in the chain.
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema);
        services.AddValtuutusData().AddCaching();
        services.AddValtuutusCheckV2();

        GetUnkeyed<ICheckEngine>(services).ImplementationType.Should().NotBe(typeof(CachedCheckEngine));
    }

    [Fact]
    public void AddCaching_wraps_lookup_entity_and_lookup_subject_engines()
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema);
        services.AddValtuutusData().AddCaching();

        GetUnkeyed<ILookupEntityEngine>(services).ImplementationType.Should().Be(typeof(CachedLookupEntityEngine));
        GetKeyed<ILookupEntityEngine>(services, Consts.InnerLookupEntityEngineKey).KeyedImplementationType.Should().Be(typeof(LookupEntityEngine));

        GetUnkeyed<ILookupSubjectEngine>(services).ImplementationType.Should().Be(typeof(CachedLookupSubjectEngine));
        GetKeyed<ILookupSubjectEngine>(services, Consts.InnerLookupSubjectEngineKey).KeyedImplementationType.Should().Be(typeof(LookupSubjectEngine));
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
