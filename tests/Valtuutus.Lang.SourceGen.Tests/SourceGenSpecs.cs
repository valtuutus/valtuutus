using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;
using System.Text;

namespace Valtuutus.Lang.SourceGen.Tests;

public class SourceGenSpecs
{
    [Theory]
    [InlineData("schema1.vtt")]
    [InlineData("schema2.vtt")]
    public Task TestSchemaGen(string fileName)
    {
        return TestHelper.Verify(fileName);
    }
}

public class InMemoryAdditionalText : AdditionalText
{
    private readonly string _path;
    private readonly string _text;

    public InMemoryAdditionalText(string path, string text)
    {
        _path = path;
        _text = text;
    }

    public override string Path => _path;

    public override SourceText GetText(System.Threading.CancellationToken cancellationToken = default)
    {
        return SourceText.From(_text, Encoding.UTF8);
    }
}