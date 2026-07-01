namespace Puck.Abstractions.Memory;

/// <summary>
/// An unmanaged memory allocator. The engine's components depend on this abstraction and receive a concrete
/// implementation (e.g. mimalloc) via dependency injection, so no library hard-codes a specific allocator or the
/// assembly that hosts it.
/// </summary>
public unsafe interface IAllocator {
    /// <summary>Allocates a block of unmanaged memory. Implementations do not throw on failure — a failed
    /// allocation surfaces as <see langword="null"/>, exactly as the underlying native allocator reports it.</summary>
    /// <param name="size">The number of bytes to allocate. A zero-byte request is forwarded to the underlying
    /// allocator; whether it yields <see langword="null"/> or a unique zero-length block is implementation-defined,
    /// and any non-null result must still be released with <see cref="Free"/>.</param>
    /// <param name="alignment">The required alignment in bytes, or 0 for the allocator's default. The default is
    /// implementation-defined: the mimalloc-backed implementation uses <c>mi_malloc</c>'s natural alignment, while
    /// the <c>NativeMemory</c>-backed implementation aligns to 16 bytes.</param>
    /// <returns>A pointer to the allocated block, or <see langword="null"/> if the allocation failed.</returns>
    void* Allocate(nuint size, nuint alignment = 0);

    /// <summary>Frees a block previously returned by <see cref="Allocate"/> or <see cref="Reallocate"/>. Passing
    /// <see langword="null"/> is safe — every engine implementation treats it as a no-op.</summary>
    /// <param name="ptr">The block to free, or <see langword="null"/> to do nothing.</param>
    void Free(void* ptr);

    /// <summary>Resizes a previously allocated block, preserving its contents up to the smaller of the two sizes.</summary>
    /// <param name="ptr">The block to resize.</param>
    /// <param name="newSize">The new size in bytes.</param>
    /// <param name="alignment">The required alignment in bytes, or 0 for the allocator's default (see
    /// <see cref="Allocate"/>).</param>
    /// <returns>A pointer to the resized block (which may differ from <paramref name="ptr"/>), or
    /// <see langword="null"/> if the resize failed (the original block is then left as the implementation left
    /// it — implementation-defined).</returns>
    void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0);
}
