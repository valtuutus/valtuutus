namespace Valtuutus.Core.Data;

public record struct SnapToken(string Value)
{
    /// <summary>
    /// The minimum possible ULID value. Used as fallback when the transactions table is empty.
    /// A query with this token returns zero rows, which is correct for a fresh database.
    /// </summary>
    public static readonly SnapToken MinValue = new("00000000000000000000000000");

    public static implicit operator SnapToken?(string? token)
    {
        return token is null ? null : new SnapToken(token);
    }
};
