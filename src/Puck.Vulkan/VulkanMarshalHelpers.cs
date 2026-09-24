using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Vulkan;

/// <summary>
/// Helpers for marshaling managed values into unmanaged memory for the duration of a native Vulkan call.
/// </summary>
public static unsafe class VulkanMarshalHelpers {
    /// <summary>Gets a pointer to the NUL-terminated UTF-8 name <c>main</c>, the entry point of every shader stage the
    /// backend creates. It addresses the assembly's static data, so it stays valid for the life of the process and
    /// is never freed.</summary>
    public static nint MainEntryPoint => ((nint)Unsafe.AsPointer(value: ref MemoryMarshal.GetReference(span: "main"u8)));

    /// <summary>Allocates unmanaged memory and copies the given values into it as a contiguous native array.</summary>
    /// <typeparam name="T">The unmanaged element type.</typeparam>
    /// <param name="allocator">The unmanaged allocator that provides the memory.</param>
    /// <param name="values">The values to copy, in order.</param>
    /// <returns>A pointer to the first element, or zero when <paramref name="values"/> is empty. The caller owns a
    /// non-zero allocation and must free it with <paramref name="allocator"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> or <paramref name="values"/> is <see langword="null"/>.</exception>
    /// <exception cref="OutOfMemoryException">The allocator could not provide the memory; nothing was allocated.</exception>
    public static nint AllocateArray<T>(IAllocator allocator, IReadOnlyList<T> values)
        where T : unmanaged {
        ArgumentNullException.ThrowIfNull(argument: allocator);
        ArgumentNullException.ThrowIfNull(argument: values);

        if (0 == values.Count) {
            return 0;
        }

        var elements = ((T*)Allocate(
            allocator: allocator,
            sizeBytes: checked((((nuint)sizeof(T)) * ((nuint)values.Count)))
        ));

        for (var index = 0; (index < values.Count); index++) {
            elements[index] = values[index];
        }

        return ((nint)elements);
    }
    /// <summary>Allocates a block from <paramref name="allocator"/>, turning its <see langword="null"/> failure into an exception.</summary>
    /// <param name="allocator">The unmanaged allocator that provides the memory.</param>
    /// <param name="sizeBytes">The number of bytes to allocate; must be non-zero.</param>
    /// <returns>The allocated block, owned by the caller.</returns>
    /// <exception cref="OutOfMemoryException">The allocator returned <see langword="null"/>.</exception>
    public static void* Allocate(IAllocator allocator, nuint sizeBytes) {
        var block = allocator.Allocate(size: sizeBytes);

        if (null == block) {
            throw new OutOfMemoryException(message: $"The allocator could not provide {sizeBytes} bytes for a native Vulkan argument.");
        }

        return block;
    }
}
