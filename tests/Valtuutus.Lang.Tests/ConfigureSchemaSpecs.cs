using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Lang.Tests;

public class ConfigureSchemaSpecs
{
    [Fact]
    public void Should_use_compiled_function_when_registered_via_AddValtuutusCore()
    {
        var compiledFunctions = new Dictionary<string, Func<IDictionary<string, object?>, bool>>
        {
            // Deliberately returns the opposite of what the DSL body (`status == 1`) would produce,
            // to prove the compiled delegate registered via AddValtuutusCore is what actually executes.
            ["isActiveStatus"] = _ => false
        };

        var services = new ServiceCollection();
        services.AddValtuutusCore(@"
            entity user {}
            entity project {
                attribute status int;
                permission edit := isActiveStatus(status);
            }
            fn isActiveStatus(status int) => status == 1;
        ", compiledFunctions);

        var provider = services.BuildServiceProvider();
        var schema = provider.GetRequiredService<Schema>();

        schema.Functions["isActiveStatus"]
            .Execute(new Dictionary<string, object?> { ["status"] = 1 })
            .Should().BeFalse();
    }

    [Fact]
    public void Should_build_schema_without_compiledFunctions_parameter_unchanged()
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(@"
            entity user {}
            entity project {
                attribute status int;
                permission edit := isActiveStatus(status);
            }
            fn isActiveStatus(status int) => status == 1;
        ");

        var provider = services.BuildServiceProvider();
        var schema = provider.GetRequiredService<Schema>();

        schema.Functions["isActiveStatus"]
            .Execute(new Dictionary<string, object?> { ["status"] = 1 })
            .Should().BeTrue();
    }
}
