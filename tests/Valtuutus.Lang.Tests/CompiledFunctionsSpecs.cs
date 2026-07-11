using FluentAssertions;
using Valtuutus.Core.Lang.SchemaReaders;

namespace Valtuutus.Lang.Tests;

public class CompiledFunctionsSpecs
{
    [Fact]
    public void Should_use_compiled_function_delegate_when_name_matches()
    {
        var compiledFunctions = new Dictionary<string, Func<IDictionary<string, object?>, bool>>
        {
            // Deliberately returns the opposite of what the DSL body says, to prove
            // the compiled delegate is what actually executes, not the Expression-tree fallback.
            ["isActiveStatus"] = _ => false
        };

        var schema = new SchemaReader(compiledFunctions).Parse(@"
            fn isActiveStatus(status int) => status == 1;
        ");

        schema.Should().NotBeNull();
        schema.AsT0.Functions["isActiveStatus"]
            .Execute(new Dictionary<string, object?> { ["status"] = 1 })
            .Should().BeFalse();
    }

    [Fact]
    public void Should_fall_back_to_expression_compile_when_name_not_in_map()
    {
        var compiledFunctions = new Dictionary<string, Func<IDictionary<string, object?>, bool>>
        {
            ["someOtherFunction"] = _ => false
        };

        var schema = new SchemaReader(compiledFunctions).Parse(@"
            fn isActiveStatus(status int) => status == 1;
        ");

        schema.Should().NotBeNull();
        schema.AsT0.Functions["isActiveStatus"]
            .Execute(new Dictionary<string, object?> { ["status"] = 1 })
            .Should().BeTrue();
    }

    [Fact]
    public void Should_behave_identically_to_parameterless_constructor_when_no_map_given()
    {
        var schema = new SchemaReader().Parse(@"
            fn isActiveStatus(status int) => status == 1;
        ");

        schema.Should().NotBeNull();
        schema.AsT0.Functions["isActiveStatus"]
            .Execute(new Dictionary<string, object?> { ["status"] = 1 })
            .Should().BeTrue();
    }
}
