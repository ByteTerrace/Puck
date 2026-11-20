using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Abstractions.Memory;

/// <summary>
/// Workspace arrays for builds that grow scratch storage and copy out what they keep. Arrays up to 1 MiB of payload
/// come from the shared pool's buckets. Larger arrays of reference-free elements come from a bounded pool of their own
/// (<see cref="LargePool{T}"/>) in size classes a quarter of a doubling apart, so a build that rents the same large
/// scratch again (a bake group's contour lists, a merge's outputs) reuses an array a finished build returned instead of
/// leaving each one for the collector on the large object heap. The pool holds at most
/// <see cref="MaxRetainedBytes"/> and gives everything back to the collector on <see cref="TrimLarge"/>.
/// </summary>
public static class RentedArrays {
    private const int MaxPooledBytes = (1 << 20);
    // The most bytes of large arrays the pool keeps for reuse, over every element type.
    private const long MaxRetainedBytes = (512L << 20);
    // The largest single array the pool keeps; a bigger one is a one-off the collector reclaims once it is returned.
    private const long MaxRetainedArrayBytes = (128L << 20);
    // The arrays of one element type the pool keeps.
    private const int MaxRetainedArrays = 16;

    private static readonly Lock TrimGate = new();
    private static readonly List<Action> Trimmers = [];
    private static long RetainedBytes;

    private static int PooledLengthLimit<T>() {
        // The shared pool rounds requests to a power-of-two bucket with at least 16 elements. Count the bucket's
        // capacity in bytes, including for element widths that are not powers of two.
        var maxLength = (MaxPooledBytes / Unsafe.SizeOf<T>());

        return ((maxLength < 16) ? 0 : (1 << BitOperations.Log2(value: (uint)maxLength)));
    }
    // The length a large rental of at least minimumLength rounds up to: a multiple of a quarter of the power of two below
    // it, so no array is more than a quarter longer than asked for and the same request always finds the same class.
    private static int ClassLengthOf(int minimumLength) {
        var step = (1 << Math.Max(val1: 0, val2: (BitOperations.Log2(value: ((uint)(minimumLength - 1))) - 2)));
        var length = ((((long)minimumLength + step) - 1) & -((long)step));

        return ((int)Math.Min(val1: length, val2: Math.Max(val1: minimumLength, val2: Array.MaxLength)));
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
        if (minimumLength <= PooledLengthLimit<T>()) { return ArrayPool<T>.Shared.Rent(minimumLength: minimumLength); }
        return (RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? GC.AllocateUninitializedArray<T>(length: minimumLength) : LargePool<T>.Rent(minimumLength: minimumLength));
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
    /// <summary>Returns an array to the shared pool, clearing it first when its elements hold references, or a large
    /// array of reference-free elements to the large pool while it has room. Any other large array is eligible for
    /// collection once its caller releases it.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="array">The array <see cref="Rent{T}(int)"/> or <see cref="Grow{T}"/> gave; the caller holds no reference to
    /// it afterward. An empty array is accepted and kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> is <see langword="null"/>.</exception>
    public static void Return<T>(T[] array) {
        ArgumentNullException.ThrowIfNull(argument: array);

        if (array.Length == 0) { return; }
        if (array.Length > PooledLengthLimit<T>()) {
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>()) { LargePool<T>.Return(array: array); }
            return;
        }
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
    /// <summary>Gives every array the large pool holds back to the collector. Arrays callers still hold are
    /// unaffected, and each returns to the pool, or to the collector, when its caller returns it.</summary>
    public static void TrimLarge() {
        Action[] trimmers;

        lock (TrimGate) { trimmers = [.. Trimmers]; }
        foreach (var trim in trimmers) { trim(); }
    }
    /// <summary>Gets the bytes of arrays the large pool holds for reuse, over every element type.</summary>
    public static long LargeRetainedBytes => Interlocked.Read(location: ref RetainedBytes);

    // The arrays of one reference-free element type past the shared pool's limit that finished builds returned. Every
    // array it holds has exactly a size class's length (ClassLengthOf), which is what finds one a later rental can use.
    private static class LargePool<T> {
        private static readonly Lock Gate = new();
        private static readonly List<T[]> Arrays = [];

        static LargePool() {
            lock (TrimGate) { Trimmers.Add(item: Trim); }
        }

        public static T[] Rent(int minimumLength) {
            var length = ClassLengthOf(minimumLength: minimumLength);

            lock (Gate) {
                for (var index = (Arrays.Count - 1); (index >= 0); index--) {
                    var array = Arrays[index];

                    if (array.Length != length) { continue; }
                    Arrays.RemoveAt(index: index);
                    _ = Interlocked.Add(location1: ref RetainedBytes, value: -Bytes(array: array));
                    return array;
                }
            }
            return GC.AllocateUninitializedArray<T>(length: length);
        }
        public static void Return(T[] array) {
            var bytes = Bytes(array: array);

            if ((array.Length != ClassLengthOf(minimumLength: array.Length)) || (bytes > MaxRetainedArrayBytes)) { return; }
            lock (Gate) {
                if ((Arrays.Count >= MaxRetainedArrays) || ((Interlocked.Read(location: ref RetainedBytes) + bytes) > MaxRetainedBytes)) { return; }
                Arrays.Add(item: array);
                _ = Interlocked.Add(location1: ref RetainedBytes, value: bytes);
            }
        }
        private static void Trim() {
            lock (Gate) {
                foreach (var array in Arrays) { _ = Interlocked.Add(location1: ref RetainedBytes, value: -Bytes(array: array)); }
                Arrays.Clear();
            }
        }
        private static long Bytes(T[] array) => (((long)array.Length) * Unsafe.SizeOf<T>());
    }
}
