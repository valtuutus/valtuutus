using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Valtuutus.Data;

/// <summary>
/// Generates ULIDs that strictly increase per <see cref="TimeProvider"/>, including ULIDs taken in
/// the same millisecond or after the clock went back. Writers stamp transactions with it: the latest
/// snap token is the greatest transaction id, so a write that follows another becomes the latest
/// token and reading at its token sees the earlier write too.
/// </summary>
/// <remarks>
/// The first 16 bits after the timestamp are a counter that restarts at 0 each millisecond; the
/// remaining 64 bits are random, so ULIDs stay unique across processes. When the counter runs out
/// within a millisecond, it carries into the timestamp, which then runs slightly ahead of the clock.
/// Ordering holds within one process only; ULIDs from different processes order by their clocks.
/// </remarks>
public static class MonotonicUlid
{
    private const int CounterBits = 16;

    private sealed class State
    {
        // Unix milliseconds in the high 48 bits, the counter in the low 16.
        public long Last;
    }

    private static readonly ConditionalWeakTable<TimeProvider, State> States = new();

    /// <summary>
    /// Returns a ULID taken at the current time of <paramref name="clock"/>, or just after the last
    /// one generated for <paramref name="clock"/> when that one is not older.
    /// </summary>
    public static Ulid Next(TimeProvider clock) =>
        Take(clock, clock.GetUtcNow().ToUnixTimeMilliseconds() << CounterBits);

    /// <summary>
    /// Returns a ULID greater than <paramref name="floor"/> and than every ULID generated for
    /// <paramref name="clock"/> so far; ULIDs generated for <paramref name="clock"/> afterwards
    /// follow it. A writer whose clock is behind a ULID another process already committed takes its
    /// id from here.
    /// </summary>
    public static Ulid NextAfter(TimeProvider clock, Ulid floor)
    {
        Span<byte> bytes = stackalloc byte[16];
        floor.TryWriteBytes(bytes);
        var milliseconds = ((long)BinaryPrimitives.ReadUInt16BigEndian(bytes) << 32)
                           | BinaryPrimitives.ReadUInt32BigEndian(bytes[2..]);
        var position = (milliseconds << CounterBits) | BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        return Take(clock, Math.Max(clock.GetUtcNow().ToUnixTimeMilliseconds() << CounterBits, position + 1));
    }

    private static Ulid Take(TimeProvider clock, long floor)
    {
        var state = States.GetValue(clock, static _ => new State());
        long last, next;
        do
        {
            last = Volatile.Read(ref state.Last);
            next = Math.Max(floor, last + 1);
        } while (Interlocked.CompareExchange(ref state.Last, next, last) != last);

        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = next >> CounterBits;
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)(milliseconds >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(bytes[2..], (uint)milliseconds);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[6..], (ushort)next);
        RandomNumberGenerator.Fill(bytes[8..]);
        return new Ulid(bytes);
    }
}
