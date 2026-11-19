using System.Buffers;
using System.Runtime.CompilerServices;

namespace Puck.Abstractions.Memory;

/// <summary>
/// Workspace arrays rented from <see cref="ArrayPool{T}.Shared"/>: a build that fills an array of unknown final length
/// grows it here and returns it once it has copied out what it keeps, so its workspace never becomes garbage.
/// </summary>
public static class RentedArrays {
    /// <summary>Rents an array of at least <paramref name="minimumLength"/> elements; an empty request gets the shared
    /// empty array, which <see cref="Return{T}"/> accepts.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="minimumLength">The fewest elements the array holds.</param>
    /// <returns>The array, rented from the shared pool; its elements are unspecified.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
    public static T[] Rent<T>(int minimumLength) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: minimumLength);

        return ((minimumLength == 0) ? [] : ArrayPool<T>.Shared.Rent(minimumLength: minimumLength));
    }
    /// <summary>Rents an array for the scope of a <see langword="using"/> declaration: disposing the lease returns it.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="minimumLength">The fewest elements the array holds.</param>
    /// <param name="array">The rented array, longer than asked for and with unspecified elements; the caller holds no
    /// reference to it once the lease is disposed.</param>
    /// <returns>The lease, which returns the array when disposed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
    public static Lease<T> Rent<T>(int minimumLength, out T[] array) {
        array = Rent<T>(minimumLength: minimumLength);
        return new Lease<T>(array: array);
    }

    /// <summary>One rented array, returned to the pool when the lease is disposed.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="array">The rented array.</param>
    public readonly struct Lease<T>(T[] array) : IDisposable {
        /// <summary>Returns the array to the pool.</summary>
        public void Dispose() => Return(array: array);
    }
    /// <summary>Returns a rented array to the shared pool, clearing it first when its elements hold references, so a
    /// pooled array keeps nothing it held alive.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="array">The array <see cref="Rent{T}(int)"/> or <see cref="Grow{T}"/> gave; the caller holds no reference to
    /// it afterward. An empty array is accepted and kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> is <see langword="null"/>.</exception>
    public static void Return<T>(T[] array) {
        ArgumentNullException.ThrowIfNull(argument: array);

        if (array.Length == 0) { return; }
        ArrayPool<T>.Shared.Return(array: array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
    }
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

        var grown = Rent<T>(minimumLength: minimumLength);

        array.AsSpan(start: 0, length: keep).CopyTo(destination: grown);
        Return(array: array);
        return grown;
    }
}
