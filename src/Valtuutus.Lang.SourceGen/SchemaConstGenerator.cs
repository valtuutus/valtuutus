using Antlr4.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Valtuutus.Lang.SourceGen;


[Generator]
public class SchemaConstGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // We need a value provider for any addition file.
		// As soon as there is direct access to embedded resources we can change this.
		// All embedded resources are added as additional files through our build props integrated into the nuget.
        // Get additional files that are marked as embedded resources (these are passed from the consuming assembly)
        var vttFiles = context.AdditionalTextsProvider
            .Where(file => file.Path.EndsWith(".vtt"))
            .Select((file, cancellationToken) => new
            {
                FileName = Path.GetFileName(file.Path),
                Content = file.GetText(cancellationToken)?.ToString()
            })
            .Collect()
            .Select((files, ct) => files.FirstOrDefault());
        
        context.RegisterSourceOutput(vttFiles, (sourceProductionContext, file) =>
        {
            if (!string.IsNullOrEmpty(file.Content))
            {
                var generatedSource = ProcessVttFile(file.Content);
                sourceProductionContext.AddSource($"SchemaConsts.g.cs", SourceText.From(generatedSource, Encoding.UTF8));
            }
        });

    }
    
    private string ProcessVttFile(string vttContent)
    {
        // Custom logic to process the .vtt content
        StringBuilder sb = new StringBuilder();
        var str = new AntlrInputStream(vttContent);
        var lexer = new ValtuutusLexer(str);
        var tokens = new CommonTokenStream(lexer);
        var parser = new ValtuutusParser(tokens);
        
        var tree = parser.schema();

        var cultureInfo = CultureInfo.InvariantCulture;
        
        sb.AppendLine("""
                      namespace Valtuutus.Lang;

                      /// <summary>
                      /// Auto-generated class to access all schema members as consts.
                      /// </summary>
                      public static class SchemaConstsGen
                      """);
        sb.AppendLine("{");
        foreach (var entity in tree.entityDefinition())
        {
            var entityName = entity.ID().GetText();
            var entityBody = entity.entityBody();
            sb.AppendLine($"\tpublic static class {cultureInfo.TextInfo.ToTitleCase(entityName).Replace("_", "")}");
            sb.AppendLine("\t{");
            sb.AppendLine($"\t\tpublic const string Name = \"{entityName}\";");

            var attributes = entityBody.attributeDefinition();

            if (attributes.Length > 0)
            {
                sb.AppendLine("\t\tpublic static class Attributes");
                sb.AppendLine("\t\t{");
                foreach (var attribute in attributes)
                {
                    var attributeName = attribute.ID().GetText();
                    sb.AppendLine($"\t\t\tpublic const string {cultureInfo.TextInfo.ToTitleCase(attributeName).Replace("_", "")} = \"{attributeName}\";");
                }
                sb.AppendLine("\t\t}");    
            }

            var relations = entityBody.relationDefinition();

            if (relations.Length > 0)
            {
                sb.AppendLine("\t\tpublic static class Relations");
                sb.AppendLine("\t\t{");
                foreach (var relation in relations)
                {
                    var relationName = relation.ID().GetText();
                    sb.AppendLine($"\t\t\tpublic const string {cultureInfo.TextInfo.ToTitleCase(relationName).Replace("_", "")} = \"{relationName}\";");
                }
                sb.AppendLine("\t\t}");
            }
            
            var permissions = entityBody.permissionDefinition();

            if (permissions.Length > 0)
            {
                sb.AppendLine("\t\tpublic static class Permissions");
                sb.AppendLine("\t\t{");
                foreach (var perm in permissions)
                {
                    var permName = perm.ID().GetText();
                    sb.AppendLine(
                        $"\t\t\tpublic const string {cultureInfo.TextInfo.ToTitleCase(permName).Replace("_", "")} = \"{permName}\";");
                }

                sb.AppendLine("\t\t}");
            }

            sb.AppendLine("\t}");
        }
        sb.AppendLine("}");
        
        return sb.ToString();
    }
}