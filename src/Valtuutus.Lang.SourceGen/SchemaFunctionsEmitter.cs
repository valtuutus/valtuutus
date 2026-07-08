using System;
using System.Collections.Generic;
using System.Text;
using Antlr4.Runtime;
using Microsoft.CodeAnalysis;

namespace Valtuutus.Lang.SourceGen;

/// <summary>
/// Walks all `fn` definitions in a .vtt schema's text and emits a complete generated
/// C# source file (a static <c>SchemaFunctionsGen</c> class) wrapping each transpiled
/// function body into a native method plus a name-keyed dispatch dictionary.
/// </summary>
public static class SchemaFunctionsEmitter
{
    private static readonly DiagnosticDescriptor TranspileErrorDescriptor = new(
        id: "VTTFN001",
        title: "Schema function transpile error",
        messageFormat: "{0}",
        category: "Valtuutus.Lang.SourceGen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static string Emit(string vttContent, Action<Diagnostic> reportDiagnostic)
    {
        var str = new AntlrInputStream(vttContent);
        var lexer = new ValtuutusLexer(str);
        var tokens = new CommonTokenStream(lexer);
        var parser = new ValtuutusParser(tokens);
        var tree = parser.schema();

        return Emit(tree, reportDiagnostic);
    }

    internal static string Emit(ValtuutusParser.SchemaContext tree, Action<Diagnostic> reportDiagnostic)
    {
        var methods = new StringBuilder();
        var dictionaryEntries = new StringBuilder();
        var emittedMethodNames = new HashSet<string>();

        foreach (var funcCtx in tree.functionDefinition())
        {
            var result = FunctionExpressionTranspiler.Transpile(funcCtx);
            if (!result.Success)
            {
                reportDiagnostic(Diagnostic.Create(TranspileErrorDescriptor, Location.None, result.Error));
                continue;
            }

            var methodName = ToPascalCase(result.FunctionName!);

            if (!emittedMethodNames.Add(methodName))
            {
                reportDiagnostic(Diagnostic.Create(
                    TranspileErrorDescriptor,
                    Location.None,
                    $"Function '{result.FunctionName}' generates a duplicate method name '{methodName}' (already used by a previous function in this schema)."));
                continue;
            }

            methods.AppendLine($"\tpublic static bool {methodName}(IDictionary<string, object?> args)");
            methods.AppendLine("\t{");
            foreach (var p in result.Parameters)
            {
                // Verbatim-escape the local var name unconditionally, mirroring FunctionExpressionTranspiler's
                // parameter references, since either may be a C# reserved keyword (e.g. "class").
                methods.AppendLine($"\t\tvar @{p.Name} = {CastExpression(p.Type)}args[\"{p.Name}\"];");
            }
            methods.AppendLine($"\t\treturn {result.BodyExpression};");
            methods.AppendLine("\t}");
            methods.AppendLine();

            dictionaryEntries.AppendLine($"\t\t[\"{result.FunctionName}\"] = {methodName},");
        }

        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine("namespace Valtuutus.Lang;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Auto-generated class containing schema DSL functions compiled to native C# at build time.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class SchemaFunctionsGen");
        sb.AppendLine("{");
        sb.Append(methods);
        sb.AppendLine("\tpublic static readonly IReadOnlyDictionary<string, Func<IDictionary<string, object?>, bool>> All = new Dictionary<string, Func<IDictionary<string, object?>, bool>>");
        sb.AppendLine("\t{");
        sb.Append(dictionaryEntries);
        sb.AppendLine("\t};");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Converts a schema function name (camelCase or snake_case) into a PascalCase C#
    /// method name, capitalizing the first letter of each underscore-separated segment
    /// while leaving the rest of each segment untouched (so "isActiveStatus" keeps its
    /// internal camelCasing rather than being lowercased by <see cref="System.Globalization.TextInfo.ToTitleCase(string)"/>).
    /// </summary>
    private static string ToPascalCase(string name)
    {
        var sb = new StringBuilder();
        foreach (var segment in name.Split('_'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            sb.Append(char.ToUpperInvariant(segment[0]));
            if (segment.Length > 1)
            {
                sb.Append(segment, 1, segment.Length - 1);
            }
        }

        return sb.ToString();
    }

    private static string CastExpression(FunctionParamType type) => type switch
    {
        FunctionParamType.Int => "(int?)",
        FunctionParamType.String => "(string?)",
        FunctionParamType.Decimal => "(decimal?)",
        FunctionParamType.Boolean => "(bool?)",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
