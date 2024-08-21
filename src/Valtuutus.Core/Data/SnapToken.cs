namespace Valtuutus.Core.Data;

public record struct SnapToken(string Value)
{
    public static implicit operator SnapToken(string token) => new SnapToken(token);
};