namespace Valtuutus.Core.Data;

public record struct SnapToken(string Value)
{
    public static SnapToken Empty => new(string.Empty);
}