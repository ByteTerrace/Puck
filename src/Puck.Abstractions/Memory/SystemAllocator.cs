using System.Runtime.InteropServices;

namespace Puck.Abstractions.Memory;

/// <summary>
/// The operating system's native heap as an <see cref="IAllocator"/>: every block is aligned to at least 16 bytes, and
/// a request for a stricter alignment gets it. It is what owned storage uses in a process whose host installed no
/// allocator of its own (<see cref="ProcessAllocator"/>): a tool, a test or an offline bake.
/// </summary>
public sealed unsafe class SystemAllocator : IAllocator {
    private const nuint MinimumAlignment = 16;

    /// <summary>Gets the one instance a process shares; the type holds no state.</summary>
    public static SystemAllocator Instance { get; } = new();

    /// <inheritdoc/>
    public void* Allocate(nuint size, nuint alignment = 0) => NativeMemory.AlignedAlloc(
        alignment: Math.Max(val1: alignment, val2: MinimumAlignment),
        byteCount: Math.Max(val1: size, val2: 1)
    );
    /// <inheritdoc/>
    public void Free(void* ptr) => NativeMemory.AlignedFree(ptr: ptr);
    /// <inheritdoc/>
    public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => NativeMemory.AlignedRealloc(
        alignment: Math.Max(val1: alignment, val2: MinimumAlignment),
        byteCount: Math.Max(val1: newSize, val2: 1),
        ptr: ptr
    );
}
/// <summary>
/// The native allocator storage takes when its owner names none: the host's own once it installs one (the World's
/// mimalloc, through <c>Puck.Memory.Allocator</c>), and the system heap until then. Invariant: storage that names no
/// allocator is still native, so no owned buffer, list or map ever lies on the collector's large object heap; the one
/// place that holds it is <see cref="OwnedBuffer{T}"/>'s constructor, which reads <see cref="Current"/> once and frees
/// through the allocator it read, so replacing the allocator never frees a block through another's heap.
/// </summary>
public static class ProcessAllocator {
    private static IAllocator CurrentField = SystemAllocator.Instance;

    /// <summary>Gets or sets the process's allocator. A host sets it before it builds anything that owns storage.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public static IAllocator Current {
        get => Volatile.Read(location: ref CurrentField);
        set => Volatile.Write(location: ref CurrentField, value: (value ?? throw new ArgumentNullException(paramName: nameof(value))));
    }
}
