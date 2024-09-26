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
        sb.AppendLine("public class GeneratedVttClass");
        sb.AppendLine("{");
        sb.AppendLine("    public static string GetVttContent()");
        sb.AppendLine("    {");
        sb.AppendLine($"        return @\"{vttContent}\";");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
    
    

    

}