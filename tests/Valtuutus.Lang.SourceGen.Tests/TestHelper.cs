using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;

namespace Valtuutus.Lang.SourceGen.Tests;

public static class TestHelper
{
    public static Task Verify(string? fileName = null)
    {
        var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        
        // Create a Roslyn compilation for the syntax tree.
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "Tests",
            references: new[] { mscorlib },
            options: options);



        var generator = new SchemaConstGenerator();



        // The GeneratorDriver is used to run our generator against a compilation
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
            
            
                    
        if (fileName is not null)
        {
            var path = $"{fileName}";
            var additionalText = new InMemoryAdditionalText(path, File.ReadAllText(path));
            driver = driver.AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(additionalText));

        }

        // Run the source generator!
        driver = driver.RunGenerators(compilation);

        // Use verify to snapshot test the source generator output!
        return Verifier.Verify(driver)
            .UseParameters(fileName ?? "null");
    }
}