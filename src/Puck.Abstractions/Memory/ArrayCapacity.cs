using Puck.Abstractions.Gpu;
using Puck.Maths;

namespace Puck.Abstractions.Memory;

/// <summary>Capacity of managed arrays whose active length is tracked separately.</summary>
public static class ArrayCapacity {
    /// <summary>Doubles the current capacity on growth within the array limit and any smaller caller ceiling.</summary>
    public static int Grown(int current, int required, int minimum = 16, int ceiling = int.MaxValue) =>
        (int)GpuCapacity.Grown(current, required, Math.Min(ceiling, Array.MaxLength), divisor: 1, minimum: Math.Min(minimum, ceiling));

    /// <summary>Returns the slot count of an open-addressed table that holds <paramref name="count"/> entries at most half
    /// full: a power of two, at least sixteen, so a probe wraps by a mask.</summary>
    /// <param name="count">The entries the table holds before it grows; at least one is assumed.</param>
    /// <returns>The slot count.</returns>
    public static int TableSlots(int count) => (int)Math.Max(val1: 16UL, val2: (((ulong)Math.Max(val1: 1, val2: count)) * 2UL).NextPowerOfTwo());

    /// <summary>Ensures room for the active elements, retaining the previous contents.</summary>
    public static void EnsureLength<T>(ref T[] array, int required) {
        if (array.Length < required) { Array.Resize(ref array, Grown(array.Length, required)); }
    }

    /// <summary>Reads an element of a table that grows on demand, answering a fallback for an index the table has not
    /// grown to (or a negative one).</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="array">The table as currently grown.</param>
    /// <param name="index">The 0-based index to read.</param>
    /// <param name="fallback">The value an index outside the table reads.</param>
    /// <returns>The element, or <paramref name="fallback"/> when <paramref name="index"/> is outside the table.</returns>
    public static T ElementOr<T>(T[] array, int index, T fallback = default!) =>
        ((((uint)index) < ((uint)array.Length)) ? array[index] : fallback);

    /// <summary>Ensures room for the active elements, discarding the previous contents on growth.</summary>
    public static T[] EnsureScratch<T>(ref T[] array, int required, int ceiling = int.MaxValue) {
        if (array.Length < required) { array = new T[Grown(array.Length, required, ceiling: ceiling)]; }
        return array;
    }
}
