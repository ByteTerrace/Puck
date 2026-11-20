using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Memory;

/// <summary>Capacity of managed arrays whose active length is tracked separately.</summary>
public static class ArrayCapacity {
    /// <summary>Doubles the current capacity on growth within the array limit and any smaller caller ceiling.</summary>
    public static int Grown(int current, int required, int minimum = 16, int ceiling = int.MaxValue) =>
        (int)GpuCapacity.Grown(current, required, Math.Min(ceiling, Array.MaxLength), divisor: 1, minimum: Math.Min(minimum, ceiling));

    /// <summary>Ensures room for the active elements, retaining the previous contents.</summary>
    public static void EnsureLength<T>(ref T[] array, int required) {
        if (array.Length < required) { Array.Resize(ref array, Grown(array.Length, required)); }
    }

    /// <summary>Ensures room for the active elements, discarding the previous contents on growth.</summary>
    public static T[] EnsureScratch<T>(ref T[] array, int required, int ceiling = int.MaxValue) {
        if (array.Length < required) { array = new T[Grown(array.Length, required, ceiling: ceiling)]; }
        return array;
    }
}
