namespace Puck.Abstractions.Collections;

/// <summary>
/// Growth for a bounded ring held as an array, a head and a count, read oldest first at <c>(head + index) % length</c>:
/// the one way such a ring takes more room as entries arrive, so a ring admitted with a large bound holds only what it
/// has been written.
/// </summary>
public static class Rings {
    /// <summary>Returns a ring of at least <paramref name="needed"/> slots holding the <paramref name="count"/> entries
    /// read from <paramref name="head"/> at slots zero onward, and sets <paramref name="head"/> to zero; the ring itself
    /// when it already has the room. A new ring doubles the old length (at least <paramref name="least"/>) and never
    /// passes <paramref name="capacity"/>.</summary>
    /// <typeparam name="T">The entry.</typeparam>
    /// <param name="ring">The ring's storage.</param>
    /// <param name="head">The slot of the oldest entry; zero once the ring was regrown.</param>
    /// <param name="count">The entries the ring holds.</param>
    /// <param name="needed">The slots the ring must hold, at most <paramref name="capacity"/>.</param>
    /// <param name="capacity">The ring's bound.</param>
    /// <param name="least">The fewest slots a grown ring holds.</param>
    /// <returns>The ring to keep.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="needed"/> passes <paramref name="capacity"/>, or
    /// <paramref name="count"/> passes the ring's length.</exception>
    public static T[] Regrown<T>(T[] ring, ref int head, int count, int needed, int capacity, int least = 8) {
        ArgumentNullException.ThrowIfNull(argument: ring);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: needed, other: capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: count, other: ring.Length);

        if (needed <= ring.Length) { return ring; }

        var grown = new T[Math.Min(val1: capacity, val2: Math.Max(val1: needed, val2: Math.Max(val1: least, val2: (ring.Length * 2))))];

        for (var index = 0; (index < count); index++) { grown[index] = ring[((head + index) % ring.Length)]; }
        head = 0;

        return grown;
    }
}
