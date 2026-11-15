using System.Buffers;

namespace Puck.Abstractions.Memory;

/// <summary>
/// Workspace arrays rented from <see cref="ArrayPool{T}.Shared"/>: a build that fills an array of unknown final length
/// grows it here and returns it once it has copied out what it keeps, so its workspace never becomes garbage.
/// </summary>
public static class RentedArrays {
    /// <summary>Rents an array of at least <paramref name="minimumLength"/> elements, copies the first
    /// <paramref name="keep"/> elements of <paramref name="array"/> into it and returns <paramref name="array"/> to the
    /// pool.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="array">The rented array to replace; the caller holds no reference to it afterward.</param>
    /// <param name="keep">The leading elements to carry over, at most the array's length.</param>
    /// <param name="minimumLength">The fewest elements the replacement holds.</param>
    /// <returns>The replacement, rented from the shared pool; its elements past <paramref name="keep"/> are
    /// unspecified.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keep"/> is negative or past the array's length, or
    /// <paramref name="minimumLength"/> is below it.</exception>
    public static T[] Grow<T>(T[] array, int keep, int minimumLength) {
        ArgumentNullException.ThrowIfNull(argument: array);
        ArgumentOutOfRangeException.ThrowIfNegative(value: keep);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: keep, other: array.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: minimumLength, other: keep);

        var grown = ArrayPool<T>.Shared.Rent(minimumLength: minimumLength);

        array.AsSpan(start: 0, length: keep).CopyTo(destination: grown);
        ArrayPool<T>.Shared.Return(array: array);
        return grown;
    }
}
