using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.Abstractions.Memory;

/// <summary>
/// Workspace arrays for a caller that needs an array: an element that holds references, or an interface that takes an
/// array. Arrays up to 1 MiB of payload come from the shared pool's buckets; a larger one is allocated for the rental and
/// left to the collector when it is returned. Large scratch of reference-free elements belongs in native storage
/// (<see cref="OwnedBuffer{T}"/>, <see cref="RentedScope.Buffer{T}"/>), which never reaches the large object heap.
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
    /// empty array, which <see cref="Return{T}(T[])"/> accepts.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="minimumLength">The fewest elements the array holds.</param>
    /// <returns>The array. Its elements are unspecified.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
    public static T[] Rent<T>(int minimumLength) => Rent<T>(minimumLength, owner: null);

    /// <summary>Rents under a workspace owner, charging the bucket capacity before asking the pool.</summary>
    /// <typeparam name="T">Array element.</typeparam>
    /// <param name="minimumLength">Fewest elements requested.</param>
    /// <param name="owner">Owner of the workspace, or null for an unmetered rental.</param>
    /// <returns>The rented array.</returns>
    public static T[] Rent<T>(int minimumLength, IAllocationObserver? owner) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: minimumLength);

        if (minimumLength == 0) { return []; }
        var shared = minimumLength <= PooledLengthLimit<T>();
        var capacity = shared ? (int)((uint)Math.Max(16, minimumLength)).NextPowerOfTwo() : minimumLength;
        var bytes = (long)capacity * Unsafe.SizeOf<T>();
        owner?.Acquire(bytes, MemoryBacking.Managed);
        try {
            var array = shared ? ArrayPool<T>.Shared.Rent(minimumLength) : GC.AllocateUninitializedArray<T>(capacity);
            if (owner is not null && array.Length != capacity) {
                // The shared pool promises a minimum length. Account the actual bucket if its policy changes.
                var extra = ((long)array.Length - capacity) * Unsafe.SizeOf<T>();
                if (extra > 0 && !owner.TryAcquire(extra, MemoryBacking.Managed)) {
                    Return(array);
                    throw new HostMemoryRefusedException(owner.Name, extra);
                }
                if (extra < 0) { owner.Change(extra, MemoryBacking.Managed); }
            }
            return array;
        } catch {
            owner?.Change(-bytes, MemoryBacking.Managed);
            throw;
        }
    }

    /// <summary>Rents an array for the scope of a <see langword="using"/> declaration: disposing the lease returns it
    /// and releases the workspace charge the rent took. The lease holds the owner, so a charge cannot outlive its array
    /// by a caller returning without it.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="minimumLength">The fewest elements the array holds.</param>
    /// <param name="array">The array, with at least the requested length and unspecified elements. The caller releases
    /// its reference to it when the lease is disposed.</param>
    /// <param name="owner">Owner of the workspace, or null for an unmetered rental.</param>
    /// <returns>The lease, which returns the array when disposed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
    public static Lease<T> Rent<T>(int minimumLength, out T[] array, IAllocationObserver? owner = null) {
        array = Rent<T>(minimumLength: minimumLength, owner: owner);
        return new Lease<T>(array: array, owner: owner);
    }

    /// <summary>One rented array, released when the lease is disposed.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="array">The rented array.</param>
    /// <param name="owner">The owner the array was rented under, or null.</param>
    public readonly struct Lease<T>(T[] array, IAllocationObserver? owner) : IDisposable {
        /// <summary>Releases the rented array.</summary>
        public void Dispose() => Return(array: array, owner: owner);
    }
    /// <summary>Returns an array to the shared pool, clearing it first when its elements hold references. A larger array
    /// is eligible for collection once its caller releases it.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="array">The array <see cref="Rent{T}(int)"/> or <see cref="Grow{T}"/> gave; the caller holds no reference to
    /// it afterward. An empty array is accepted and kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> is <see langword="null"/>.</exception>
    public static void Return<T>(T[] array) => Return(array, owner: null);

    /// <summary>Returns an array and releases its workspace charge.</summary>
    /// <typeparam name="T">Array element.</typeparam>
    /// <param name="array">The rented array.</param>
    /// <param name="owner">The owner supplied to the rent.</param>
    public static void Return<T>(T[] array, IAllocationObserver? owner) {
        ArgumentNullException.ThrowIfNull(argument: array);

        if (array.Length == 0) { return; }
        owner?.Change(-(array.LongLength * Unsafe.SizeOf<T>()), MemoryBacking.Managed);
        if (array.Length > PooledLengthLimit<T>()) { return; }
        ArrayPool<T>.Shared.Return(array: array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
    }
    /// <summary>Rents an array of at least <paramref name="minimumLength"/> elements, copies the first
    /// <paramref name="keep"/> elements of <paramref name="array"/> into it and releases <paramref name="array"/>.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="array">The rented array to replace; the caller holds no reference to it afterward.</param>
    /// <param name="keep">The leading elements to carry over, at most the array's length.</param>
    /// <param name="minimumLength">The fewest elements the replacement holds.</param>
    /// <param name="owner">The workspace owner supplied to the rent.</param>
    /// <returns>The replacement, with unspecified elements past <paramref name="keep"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keep"/> is negative or past the array's length, or
    /// <paramref name="minimumLength"/> is below it.</exception>
    public static T[] Grow<T>(T[] array, int keep, int minimumLength, IAllocationObserver? owner = null) {
        ArgumentNullException.ThrowIfNull(argument: array);
        ArgumentOutOfRangeException.ThrowIfNegative(value: keep);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: keep, other: array.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: minimumLength, other: keep);

        var grown = Rent<T>(minimumLength: minimumLength, owner: owner);

        array.AsSpan(start: 0, length: keep).CopyTo(destination: grown);
        Return(array: array, owner: owner);
        return grown;
    }
}
