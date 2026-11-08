using System.Runtime.CompilerServices;

namespace Valtuutus.Data;

/// <summary>
/// Generates the ULID a writer stamps its transaction with. The ids one clock hands out strictly
/// increase: an id taken in the same millisecond as the previous one, or after the clock went
/// back, is the previous id plus one. The latest snap token is the greatest transaction id, so a
/// write that follows another always becomes the latest token, and reading at its token sees the
/// earlier write too.
/// </summary>
public static class TransactionIds
{
    private sealed class State
    {
        public Ulid Last;
    }

    private static readonly ConditionalWeakTable<TimeProvider, State> States = new();

    /// <summary>
    /// Returns the next transaction id for <paramref name="clock"/>, taken at <paramref name="now"/>
    /// unless an id at or after <paramref name="now"/> was already handed out.
    /// </summary>
    public static Ulid Next(TimeProvider clock, DateTimeOffset now)
    {
        var state = States.GetValue(clock, static _ => new State());
        lock (state)
        {
            var id = Ulid.NewUlid(now);
            if (id.CompareTo(state.Last) <= 0)
                id = Increment(state.Last);
            state.Last = id;
            return id;
        }
    }

    private static Ulid Increment(Ulid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        // Bytes 6 to 15 are the randomness; a carry out of them moves into the timestamp.
        for (var i = 15; i >= 0; i--)
        {
            if (++bytes[i] != 0)
                break;
        }

        return new Ulid(bytes);
    }
}
