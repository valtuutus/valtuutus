using Xunit;
using Antlr4.Runtime;
using System.Collections.Generic;

namespace Valtuutus.Lang.SourceGen.Tests;

public class FunctionExpressionTranspilerSpecs
{
    private static ValtuutusParser.FunctionDefinitionContext ParseFunction(string fnText)
    {
        var input = new AntlrInputStream(fnText);
        var lexer = new ValtuutusLexer(input);
        var tokens = new CommonTokenStream(lexer);
        var parser = new ValtuutusParser(tokens);
        return parser.schema().functionDefinition(0);
    }

    [Fact]
    public void Should_emit_int_literal_equality()
    {
        var funcCtx = ParseFunction("fn isActiveStatus(status int) => status == 1;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.True(result.Success);
        Assert.Equal("isActiveStatus", result.FunctionName);
        Assert.Single(result.Parameters);
        Assert.Equal("status", result.Parameters[0].Name);
        Assert.Equal(FunctionParamType.Int, result.Parameters[0].Type);
        Assert.Equal("(@status) == (1)", result.BodyExpression);
    }

    [Fact]
    public void Should_emit_string_literal_with_escaping()
    {
        var funcCtx = ParseFunction("fn notDeleted(status string) => status != \"deleted\";");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.True(result.Success);
        Assert.Equal("(@status) != (\"deleted\")", result.BodyExpression);
    }

    [Fact]
    public void Should_emit_decimal_literal_with_m_suffix()
    {
        var funcCtx = ParseFunction("fn checkPrice(price decimal) => price == 99.99;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.True(result.Success);
        Assert.Equal("(@price) == (99.99m)", result.BodyExpression);
    }

    [Fact]
    public void Should_emit_boolean_literal_equality()
    {
        var funcCtx = ParseFunction("fn checkActive(is_active bool) => is_active == true;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.True(result.Success);
        Assert.Equal("(@is_active) == (true)", result.BodyExpression);
    }

    [Fact]
    public void Should_fail_on_undefined_parameter_reference()
    {
        var funcCtx = ParseFunction("fn broken(status int) => other == 1;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.False(result.Success);
        Assert.Contains("other", result.Error);
    }

    [Fact]
    public void Should_fail_on_comparison_type_mismatch()
    {
        var funcCtx = ParseFunction("fn broken(status int, name string) => status == name;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.False(result.Success);
        Assert.Contains("Incompatible types", result.Error);
    }

    [Fact]
    public void Should_fail_on_ordering_comparison_type_mismatch()
    {
        var funcCtx = ParseFunction("fn broken(status int, name string) => status < name;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.False(result.Success);
        Assert.Contains("Incompatible types", result.Error);
    }

    [Fact]
    public void Should_emit_int_ordering_via_nullable_compare()
    {
        var funcCtx = ParseFunction("fn withinThreshold(value int, threshold int) => value < threshold;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.True(result.Success);
        Assert.Equal("global::System.Nullable.Compare(@value, @threshold) < 0", result.BodyExpression);
    }

    [Fact]
    public void Should_emit_decimal_ordering_via_nullable_compare()
    {
        var funcCtx = ParseFunction("fn checkPrice(price decimal, maxPrice decimal) => price <= maxPrice;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.True(result.Success);
        Assert.Equal("global::System.Nullable.Compare(@price, @maxPrice) <= 0", result.BodyExpression);
    }

    [Fact]
    public void Should_emit_string_ordering_via_string_compare_ordinal()
    {
        var funcCtx = ParseFunction("fn checkTitle(a string, b string) => a > b;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.True(result.Success);
        Assert.Equal("string.Compare(@a, @b, global::System.StringComparison.Ordinal) > 0", result.BodyExpression);
    }

    [Fact]
    public void Should_fail_on_boolean_ordering_comparison()
    {
        var funcCtx = ParseFunction("fn broken(a bool, b bool) => a < b;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.False(result.Success);
        Assert.Contains("not supported for boolean", result.Error);
    }

    [Fact]
    public void Should_emit_and_or_not_paren()
    {
        var funcCtx = ParseFunction(
            "fn checkTransaction(is_verified bool, is_public bool) => is_verified == true or (is_public == true and not(is_verified == true));");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.True(result.Success);
        Assert.Equal(
            "((@is_verified) == (true)) || (((@is_public) == (true)) && (!((@is_verified) == (true))))",
            result.BodyExpression);
    }

    [Fact]
    public void Should_emit_implicit_boolean_identifier_as_equality_true()
    {
        var funcCtx = ParseFunction("fn checkActive(is_active bool) => is_active;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.True(result.Success);
        Assert.Equal("(@is_active) == (true)", result.BodyExpression);
    }

    [Fact]
    public void Should_fail_implicit_boolean_on_non_boolean_parameter()
    {
        var funcCtx = ParseFunction("fn broken(status int) => status;");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.False(result.Success);
        Assert.Contains("Expected boolean parameter", result.Error);
    }

    [Fact]
    public void Should_escape_csharp_keyword_parameter_name_with_at_prefix()
    {
        var funcCtx = ParseFunction("fn checkClass(class string) => class == \"admin\";");

        var result = FunctionExpressionTranspiler.Transpile(funcCtx);

        Assert.True(result.Success);
        Assert.Equal("(@class) == (\"admin\")", result.BodyExpression);
    }
}
