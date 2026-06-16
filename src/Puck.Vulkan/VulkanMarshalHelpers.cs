using System.Runtime.InteropServices;

namespace Puck.Vulkan;

/// <summary>
/// Helpers for marshaling managed structures into unmanaged memory for the duration of a native Vulkan call.
/// </summary>
public static class VulkanMarshalHelpers {
    /// <summary>Allocates unmanaged memory and marshals the given structure into it.</summary>
    /// <typeparam name="T">The blittable structure type to marshal.</typeparam>
    /// <param name="value">The structure value to copy into unmanaged memory.</param>
    /// <returns>A pointer to the unmanaged copy. The caller owns the allocation and must free it.</returns>
    public static nint AllocateStruct<T>(T value)
        where T : struct {
        var pointer = Puck.Memory.Allocator.Alloc(size: Marshal.SizeOf<T>());

        Marshal.StructureToPtr(
            fDeleteOld: false,
            ptr: pointer,
            structure: value
        );
        return pointer;
    }
}
