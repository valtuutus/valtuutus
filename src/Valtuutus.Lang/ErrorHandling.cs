using Antlr4.Runtime;

namespace Valtuutus.Lang;

public class ParserSchemaErrorListener : BaseErrorListener, IAntlrErrorListener<int>
{
    private readonly List<string> _errors = new();

    public override void SyntaxError(
        TextWriter _,
        IRecognizer recognizer, 
        IToken offendingSymbol, 
        int line, 
        int charPositionInLine, 
        string msg, 
        RecognitionException e)
    {
        LogError(recognizer, line, charPositionInLine, msg);
    }

    public void ThrowIfErrors()
    {
        if (_errors.Count > 0)
        {
            throw new SchemaParseException(_errors);
        }
    }

    public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine,
        string msg, RecognitionException e)
    {
        LogError(recognizer, line, charPositionInLine, msg);
    }

    private void LogError(IRecognizer recognizer, int line, int charPositionInLine, string msg)
    {
        string sourceName = recognizer.InputStream.SourceName;
        var errorMessage = $"Line {line}:{charPositionInLine} src:{sourceName} - {msg}";
        _errors.Add(errorMessage);
    }
}

public class SchemaParseException : Exception
{
    public SchemaParseException(IEnumerable<string> errors) 
        : base("Parsing errors occurred:\n" + string.Join("\n", errors))
    {
    }
}