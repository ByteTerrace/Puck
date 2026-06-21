namespace Puck.Abstractions;

/// <summary>
/// Convenience <see cref="IAllocator"/> overloads that mirror the surface of the engine's static allocator
/// facade — chiefly <c>nint</c>-based <c>Alloc</c>/<c>Free</c> — so callers can use an injected allocator with the
/// same ergonomics. The pointer-typed operations are on <see cref="IAllocator"/> itself.
/// </summary>
public static unsafe class AllocatorExtensions {
    /// <summary>Allocates a block and returns it as an <see cref="nint"/>.</summary>
    /// <param name="allocator">The allocator.</param>
    /// <param name="size">The number of bytes to allocate.</param>
    /// <returns>A pointer to the allocated block.</returns>
    public static nint Alloc(this IAllocator allocator, nint size) {
        ArgumentNullException.ThrowIfNull(allocator);

        return ((nint)allocator.Allocate(size: (nuint)size));
    }

    /// <summary>Allocates an aligned block and returns it as an <see cref="nint"/>.</summary>
    /// <param name="allocator">The allocator.</param>
    /// <param name="size">The number of bytes to allocate.</param>
    /// <param name="alignment">The required alignment in bytes.</param>
    /// <returns>A pointer to the allocated block.</returns>
    public static nint Alloc(this IAllocator allocator, nint size, nint alignment) {
        ArgumentNullException.ThrowIfNull(allocator);

        return ((nint)allocator.Allocate(size: (nuint)size, alignment: (nuint)alignment));
    }

    /// <summary>Allocates a block, returning a raw pointer.</summary>
    /// <param name="allocator">The allocator.</param>
    /// <param name="size">The number of bytes to allocate.</param>
    /// <param name="alignment">The required alignment in bytes, or 0 for the allocator's default.</param>
    /// <returns>A pointer to the allocated block.</returns>
    public static void* Alloc(this IAllocator allocator, nuint size, nuint alignment = 0) {
        ArgumentNullException.ThrowIfNull(allocator);

        return allocator.Allocate(size: size, alignment: alignment);
    }

    /// <summary>Frees a block addressed by an <see cref="nint"/>.</summary>
    /// <param name="allocator">The allocator.</param>
    /// <param name="ptr">The block to free.</param>
    public static void Free(this IAllocator allocator, nint ptr) {
        ArgumentNullException.ThrowIfNull(allocator);

        allocator.Free(ptr: (void*)ptr);
    }
}
