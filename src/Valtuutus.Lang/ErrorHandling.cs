using Antlr4.Runtime;

namespace Valtuutus.Lang;

public class ParserSchemaErrorListener : BaseErrorListener, IAntlrErrorListener<int>
{
    public List<string> Errors { get; } = new();
    public bool HasErrors => Errors.Count > 0;

    public override void SyntaxError(
        TextWriter output,
        IRecognizer recognizer, 
        IToken offendingSymbol, 
        int line, 
        int charPositionInLine, 
        string msg, 
        RecognitionException e)
    {
        LogError(recognizer, line, charPositionInLine, msg);
    }

    public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine,
        string msg, RecognitionException e)
    {
        LogError(recognizer, line, charPositionInLine, msg);
    }

    private void LogError(IRecognizer recognizer, int line, int charPositionInLine, string msg)
    {
        var errorMessage = $"Line {line}:{charPositionInLine} src:{recognizer.GetType().Name} - {msg}";
        Errors.Add(errorMessage);
    }
}

public class SchemaParseException : Exception
{
    public SchemaParseException(IEnumerable<string> errors) 
        : base("Parsing errors occurred:\n" + string.Join("\n", errors))
    {
    }
}