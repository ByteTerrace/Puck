namespace Puck.Abstractions;

/// <summary>
/// An unmanaged memory allocator. The engine's components depend on this abstraction and receive a concrete
/// implementation (e.g. mimalloc) via dependency injection, so no library hard-codes a specific allocator or the
/// assembly that hosts it.
/// </summary>
public unsafe interface IAllocator {
    /// <summary>Allocates a block of unmanaged memory.</summary>
    /// <param name="size">The number of bytes to allocate.</param>
    /// <param name="alignment">The required alignment in bytes, or 0 for the allocator's default.</param>
    /// <returns>A pointer to the allocated block.</returns>
    void* Allocate(nuint size, nuint alignment = 0);

    /// <summary>Frees a block previously returned by <see cref="Allocate"/> or <see cref="Reallocate"/>.</summary>
    /// <param name="ptr">The block to free.</param>
    void Free(void* ptr);

    /// <summary>Resizes a previously allocated block, preserving its contents up to the smaller of the two sizes.</summary>
    /// <param name="ptr">The block to resize.</param>
    /// <param name="newSize">The new size in bytes.</param>
    /// <param name="alignment">The required alignment in bytes, or 0 for the allocator's default.</param>
    /// <returns>A pointer to the resized block (which may differ from <paramref name="ptr"/>).</returns>
    void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0);
}
