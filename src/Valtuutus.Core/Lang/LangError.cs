using System.Diagnostics.CodeAnalysis;

namespace Valtuutus.Core.Lang;

public record LangError
{
    public LangError()
    {
    }
    
    [SetsRequiredMembers]
    public LangError(string message, int line, int startPos)
    {
        Message = message;
        Line = line;
        StartPos = startPos;
    }

    public required string Message { get; init; }
    public required int Line {get; init; }
    public required int StartPos { get; init; }

    public override string ToString()
    {
        return $"Line {Line}:{StartPos} {Message}";
    }
}

public class LangException : Exception
{
    public LangException(string message, int line, int startPos) : base(message)
    {
        Line = line;
        StartPos = startPos;
    }

    private int Line {get;  }
    private int StartPos { get; }

    public LangError ToLangError()
    {
        return new LangError() { Message = Message, Line = Line, StartPos = StartPos, };
    }
}