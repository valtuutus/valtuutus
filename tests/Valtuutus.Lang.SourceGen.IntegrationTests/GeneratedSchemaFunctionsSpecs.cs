using FluentAssertions;
using Valtuutus.Core.Lang;

namespace Valtuutus.Lang.SourceGen.IntegrationTests;

public class GeneratedSchemaFunctionsSpecs
{
    [Fact]
    public void ShouldExposeCompiledFunctionMethod()
    {
        SchemaFunctionsGen.IsActiveStatus([new LiteralValueUnion { LiteralType = LangType.Int, IntValue = 1 }])
            .Should().BeTrue();

        SchemaFunctionsGen.IsActiveStatus([new LiteralValueUnion { LiteralType = LangType.Int, IntValue = 2 }])
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldExposeFunctionInAllDictionaryByOriginalName()
    {
        SchemaFunctionsGen.All.Should().ContainKey("isActiveStatus");

        SchemaFunctionsGen.All["isActiveStatus"]([new LiteralValueUnion { LiteralType = LangType.Int, IntValue = 1 }])
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldHandleNullAttributeValueWithoutThrowing()
    {
        SchemaFunctionsGen.IsActiveStatus([new LiteralValueUnion { LiteralType = LangType.Int, IntValue = null }])
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldCompileAndExecuteFunctionWithKeywordParameterName()
    {
        SchemaFunctionsGen.CheckClass([new LiteralValueUnion { LiteralType = LangType.String, StringValue = "admin" }])
            .Should().BeTrue();

        SchemaFunctionsGen.CheckClass([new LiteralValueUnion { LiteralType = LangType.String, StringValue = "guest" }])
            .Should().BeFalse();
    }
}
