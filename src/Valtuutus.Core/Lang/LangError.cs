namespace Valtuutus.Core.Lang;

public record LangError
{
    public required string Message { get; init; }
    public required int Line {get; init; }
    public required int PositionInLine { get; init; }

    public override string ToString()
    {
        return $"Line {Line}:{PositionInLine} {Message}";
    }
}