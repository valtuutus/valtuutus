using FluentAssertions;

namespace Valtuutus.Lang.SourceGen.IntegrationTests;

public class GeneratedSchemaFunctionsSpecs
{
    [Fact]
    public void ShouldExposeCompiledFunctionMethod()
    {
        SchemaFunctionsGen.IsActiveStatus(new Dictionary<string, object?> { ["status"] = 1 })
            .Should().BeTrue();

        SchemaFunctionsGen.IsActiveStatus(new Dictionary<string, object?> { ["status"] = 2 })
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldExposeFunctionInAllDictionaryByOriginalName()
    {
        SchemaFunctionsGen.All.Should().ContainKey("isActiveStatus");

        SchemaFunctionsGen.All["isActiveStatus"](new Dictionary<string, object?> { ["status"] = 1 })
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldHandleNullAttributeValueWithoutThrowing()
    {
        SchemaFunctionsGen.IsActiveStatus(new Dictionary<string, object?> { ["status"] = null })
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldCompileAndExecuteFunctionWithKeywordParameterName()
    {
        SchemaFunctionsGen.CheckClass(new Dictionary<string, object?> { ["class"] = "admin" })
            .Should().BeTrue();

        SchemaFunctionsGen.CheckClass(new Dictionary<string, object?> { ["class"] = "guest" })
            .Should().BeFalse();
    }
}
