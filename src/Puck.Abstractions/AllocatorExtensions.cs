namespace Puck.Abstractions;

/// <summary>
/// Convenience <see cref="IAllocator"/> overloads that mirror the surface of the engine's static allocator
/// facade — the <c>nint</c>-based <c>Alloc</c>/<c>Free</c> pair — so callers can use an injected allocator with the
/// same ergonomics. The pointer-typed operations are on <see cref="IAllocator"/> itself.
/// </summary>
public static unsafe class AllocatorExtensions {
    /// <summary>Allocates a block with the allocator's default alignment and returns it as an <see cref="nint"/>.</summary>
    /// <param name="allocator">The allocator.</param>
    /// <param name="size">The number of bytes to allocate.</param>
    /// <returns>A pointer to the allocated block.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is negative.</exception>
    public static nint Alloc(this IAllocator allocator, nint size) {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentOutOfRangeException.ThrowIfNegative(size);

        return ((nint)allocator.Allocate(size: (nuint)size));
    }

    /// <summary>Frees a block addressed by an <see cref="nint"/>.</summary>
    /// <param name="allocator">The allocator.</param>
    /// <param name="ptr">The block to free.</param>
    public static void Free(this IAllocator allocator, nint ptr) {
        ArgumentNullException.ThrowIfNull(allocator);

        allocator.Free(ptr: (void*)ptr);
    }
}
