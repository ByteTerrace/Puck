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
    private const int MaxRetainedArrays = 64;

    private static readonly Lock TrimGate = new();
    private static readonly List<Action> Trimmers = [];
    private static long RetainedBytes;
    // The bytes of large rentals a fresh allocation answered and the pool answered, since the process began.
    private static long FreshLargeBytes;
    private static long ReusedLargeBytes;
    private static IAllocationObserver? LargeCache;

    /// <summary>Connects the process account to the large cache. Every element type shares its admission lock.</summary>
    /// <param name="owner">The process-lifetime owner of idle large arrays.</param>
    public static void ObserveLargeCache(IAllocationObserver owner) {
        ArgumentNullException.ThrowIfNull(owner);
        lock (TrimGate) {
            if (LargeCache is not null) { throw new InvalidOperationException("The large array cache already has an owner."); }
            if (RetainedBytes != 0L && !owner.TryAcquire(RetainedBytes, MemoryBacking.Managed)) {
                foreach (var trim in Trimmers) { trim(); }
            }
            LargeCache = owner;
        }
    }

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
        var references = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var capacity = shared ? (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, minimumLength))
            : references ? minimumLength : ClassLengthOf(minimumLength);
        // An idle array already occupies this memory. Drop its cache charge before the borrower takes it. It may be longer
        // than asked for, and the borrower is charged for the length it holds.
        var cached = !shared && !references ? LargePool<T>.Take(capacity) : null;
        if (cached is not null) { capacity = cached.Length; }
        var bytes = (long)capacity * Unsafe.SizeOf<T>();
        owner?.Acquire(bytes, MemoryBacking.Managed);
        try {
            var array = shared ? ArrayPool<T>.Shared.Rent(minimumLength)
                : cached ?? GC.AllocateUninitializedArray<T>(capacity);
            if (!shared) {
                if (cached is null) { _ = Interlocked.Add(location1: ref FreshLargeBytes, value: bytes); } else { _ = Interlocked.Add(location1: ref ReusedLargeBytes, value: bytes); }
            }
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
    /// <summary>Returns an array to the shared pool, clearing it first when its elements hold references, or a large
    /// array of reference-free elements to the large pool while it has room. Any other large array is eligible for
    /// collection once its caller releases it.</summary>
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
    /// <summary>Gives every array the large pool holds back to the collector. Arrays callers still hold are
    /// unaffected, and each returns to the pool, or to the collector, when its caller returns it.</summary>
    public static void TrimLarge() {
        Action[] trimmers;

        lock (TrimGate) { trimmers = [.. Trimmers]; }
        foreach (var trim in trimmers) { trim(); }
    }
    /// <summary>Gets the bytes of arrays the large pool holds for reuse, over every element type.</summary>
    public static long LargeRetainedBytes => Interlocked.Read(location: ref RetainedBytes);
    /// <summary>Gets the bytes of large rentals (past the shared pool's limit) answered by a new allocation, which the
    /// collector reclaims once it is returned past the pool's room, since the process began.</summary>
    public static long LargeFreshBytes => Interlocked.Read(location: ref FreshLargeBytes);
    /// <summary>Gets the bytes of large rentals answered by an array a finished build returned, since the process began.</summary>
    public static long LargeReusedBytes => Interlocked.Read(location: ref ReusedLargeBytes);

    // The arrays of one reference-free element type past the shared pool's limit that finished builds returned. Every
    // array it holds has exactly a size class's length (ClassLengthOf), which is what finds one a later rental can use.
    private static class LargePool<T> {
        private static readonly List<T[]> Arrays = new(capacity: MaxRetainedArrays);

        static LargePool() {
            lock (TrimGate) { Trimmers.Add(item: Trim); }
        }

        // The shortest idle array holding at least `length` elements and no more than a half again as many: bakes of
        // different sizes rent scratch in neighbouring classes, which an exact match would leave for the collector.
        public static T[]? Take(int length) {
            var most = (length + (length / 2L));

            lock (TrimGate) {
                var found = -1;

                for (var index = (Arrays.Count - 1); (index >= 0); index--) {
                    var candidate = Arrays[index].Length;

                    if ((candidate < length) || (candidate > most)) { continue; }
                    if ((found < 0) || (candidate < Arrays[found].Length)) { found = index; }
                }
                if (found < 0) { return null; }

                var array = Arrays[found];

                Arrays.RemoveAt(index: found);
                Release(bytes: Bytes(array));
                return array;
            }
        }
        public static void Return(T[] array) {
            var bytes = Bytes(array: array);

            if ((array.Length != ClassLengthOf(minimumLength: array.Length)) || (bytes > MaxRetainedArrayBytes)) { return; }
            lock (TrimGate) {
                if ((Arrays.Count >= MaxRetainedArrays) || ((RetainedBytes + bytes) > MaxRetainedBytes)) { return; }
                if (LargeCache is not null && !LargeCache.TryAcquire(bytes, MemoryBacking.Managed)) { return; }
                Arrays.Add(item: array);
                _ = Interlocked.Add(location1: ref RetainedBytes, value: bytes);
            }
        }
        private static void Trim() {
            lock (TrimGate) {
                foreach (var array in Arrays) { Release(bytes: Bytes(array)); }
                Arrays.Clear();
            }
        }
        private static long Bytes(T[] array) => (((long)array.Length) * Unsafe.SizeOf<T>());
    }

    // Under TrimGate at removal, whether a borrower took the array or a trim dropped it.
    private static void Release(long bytes) {
        LargeCache?.Change(-bytes, MemoryBacking.Managed);
        _ = Interlocked.Add(ref RetainedBytes, -bytes);
    }
}
