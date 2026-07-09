using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Valtuutus.Lang.SourceGen.Tests;

public class SchemaFunctionsEmitterSpecs
{
    [Fact]
    public void Should_emit_method_and_all_dictionary_for_single_function()
    {
        var diagnostics = new List<Diagnostic>();
        var source = SchemaFunctionsEmitter.Emit(
            "entity user {}\nfn isActiveStatus(status int) => status == 1;",
            diagnostics.Add);

        Assert.Empty(diagnostics);
        Assert.Contains("public static bool IsActiveStatus(IDictionary<string, object?> args)", source);
        Assert.Contains("var @status = (int?)args[\"status\"];", source);
        Assert.Contains("return (@status) == (1);", source);
        Assert.Contains("[\"isActiveStatus\"] = IsActiveStatus,", source);
    }

    [Fact]
    public void Should_emit_empty_all_dictionary_when_no_functions()
    {
        var diagnostics = new List<Diagnostic>();
        var source = SchemaFunctionsEmitter.Emit("entity user {}", diagnostics.Add);

        Assert.Empty(diagnostics);
        Assert.Contains("public static class SchemaFunctionsGen", source);
        Assert.DoesNotContain("public static bool", source);
    }

    [Fact]
    public void Should_report_diagnostic_and_skip_function_on_transpile_error()
    {
        var diagnostics = new List<Diagnostic>();
        var source = SchemaFunctionsEmitter.Emit(
            "fn broken(status int) => other == 1;\nfn ok(status int) => status == 1;",
            diagnostics.Add);

        Assert.Single(diagnostics);
        Assert.DoesNotContain("Broken", source);
        Assert.Contains("public static bool Ok(IDictionary<string, object?> args)", source);
        Assert.DoesNotContain("[\"broken\"]", source);
        Assert.Contains("[\"ok\"] = Ok,", source);
    }

    [Fact]
    public void Should_convert_snake_case_function_name_to_pascal_case_method_name()
    {
        var diagnostics = new List<Diagnostic>();
        var source = SchemaFunctionsEmitter.Emit(
            "fn is_active_status(status int) => status == 1;",
            diagnostics.Add);

        Assert.Empty(diagnostics);
        Assert.Contains("public static bool IsActiveStatus(IDictionary<string, object?> args)", source);
        Assert.Contains("[\"is_active_status\"] = IsActiveStatus,", source);
    }

    [Fact]
    public void Should_report_diagnostic_and_skip_function_on_pascal_case_name_collision()
    {
        var diagnostics = new List<Diagnostic>();
        var source = SchemaFunctionsEmitter.Emit(
            "fn isActive(status int) => status == 1;\nfn is_active(status int) => status == 1;",
            diagnostics.Add);

        Assert.Single(diagnostics);
        Assert.Contains("public static bool IsActive(IDictionary<string, object?> args)", source);
        Assert.Contains("[\"isActive\"] = IsActive,", source);
        Assert.DoesNotContain("[\"is_active\"]", source);
    }

    [Fact]
    public void Should_emit_compilable_code_for_csharp_keyword_parameter_name()
    {
        var diagnostics = new List<Diagnostic>();
        var source = SchemaFunctionsEmitter.Emit(
            "fn checkClass(class string) => class == \"admin\";",
            diagnostics.Add);

        Assert.Empty(diagnostics);
        Assert.Contains("var @class = (string?)args[\"class\"];", source);
        Assert.Contains("return (@class) == (\"admin\");", source);
    }
}
