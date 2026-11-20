using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Abstractions.Memory;

/// <summary>
/// Workspace arrays for builds that grow scratch storage and copy out what they keep. Shared pool buckets hold up to
/// 1 MiB of payload per array. Larger rentals become eligible for collection when the caller releases them.
/// </summary>
public static class RentedArrays {
    private const int MaxPooledBytes = (1 << 20);

    private static int PooledLengthLimit<T>() {
        // The shared pool rounds requests to a power-of-two bucket with at least 16 elements. Count the bucket's
        // capacity in bytes, including for element widths that are not powers of two.
        var maxLength = (MaxPooledBytes / Unsafe.SizeOf<T>());

        return ((maxLength < 16) ? 0 : (1 << BitOperations.Log2(value: (uint)maxLength)));
    }

    /// <summary>Rents an array of at least <paramref name="minimumLength"/> elements; an empty request gets the shared
    /// empty array, which <see cref="Return{T}"/> accepts.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="minimumLength">The fewest elements the array holds.</param>
    /// <returns>The array. Its elements are unspecified.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
    public static T[] Rent<T>(int minimumLength) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: minimumLength);

        if (minimumLength == 0) { return []; }
        return ((minimumLength <= PooledLengthLimit<T>())
            ? ArrayPool<T>.Shared.Rent(minimumLength: minimumLength)
            : GC.AllocateUninitializedArray<T>(length: minimumLength));
    }
    /// <summary>Rents an array for the scope of a <see langword="using"/> declaration: disposing the lease returns it.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="minimumLength">The fewest elements the array holds.</param>
    /// <param name="array">The array, with at least the requested length and unspecified elements. The caller releases
    /// its reference to it when the lease is disposed.</param>
    /// <returns>The lease, which returns the array when disposed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
    public static Lease<T> Rent<T>(int minimumLength, out T[] array) {
        array = Rent<T>(minimumLength: minimumLength);
        return new Lease<T>(array: array);
    }

    /// <summary>One rented array, released when the lease is disposed.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="array">The rented array.</param>
    public readonly struct Lease<T>(T[] array) : IDisposable {
        /// <summary>Releases the rented array.</summary>
        public void Dispose() => Return(array: array);
    }
    /// <summary>Returns an eligible array to the shared pool, clearing it first when its elements hold references.
    /// Larger arrays are eligible for collection once their caller releases them.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="array">The array <see cref="Rent{T}(int)"/> or <see cref="Grow{T}"/> gave; the caller holds no reference to
    /// it afterward. An empty array is accepted and kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> is <see langword="null"/>.</exception>
    public static void Return<T>(T[] array) {
        ArgumentNullException.ThrowIfNull(argument: array);

        if ((array.Length == 0) || (array.Length > PooledLengthLimit<T>())) { return; }
        ArrayPool<T>.Shared.Return(array: array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
    }
    /// <summary>Rents an array of at least <paramref name="minimumLength"/> elements, copies the first
    /// <paramref name="keep"/> elements of <paramref name="array"/> into it and releases <paramref name="array"/>.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="array">The rented array to replace; the caller holds no reference to it afterward.</param>
    /// <param name="keep">The leading elements to carry over, at most the array's length.</param>
    /// <param name="minimumLength">The fewest elements the replacement holds.</param>
    /// <returns>The replacement, with unspecified elements past <paramref name="keep"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keep"/> is negative or past the array's length, or
    /// <paramref name="minimumLength"/> is below it.</exception>
    public static T[] Grow<T>(T[] array, int keep, int minimumLength) {
        ArgumentNullException.ThrowIfNull(argument: array);
        ArgumentOutOfRangeException.ThrowIfNegative(value: keep);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: keep, other: array.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: minimumLength, other: keep);

        var grown = Rent<T>(minimumLength: minimumLength);

        array.AsSpan(start: 0, length: keep).CopyTo(destination: grown);
        Return(array: array);
        return grown;
    }
}
