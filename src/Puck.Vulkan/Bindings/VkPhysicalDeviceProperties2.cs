using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Retrieves physical device properties extended through a <c>pNext</c> chain. Only the chained structures
/// (for example acceleration-structure properties) are consumed; the embedded base properties are treated
/// as an opaque blob.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): the embedded VkPhysicalDeviceProperties payload is bound as an opaque fixed-byte blob because
/// this query only consumes the pNext-chained acceleration-structure struct. See the Properties field comment for the
/// 824 -> 1024 size margin.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkPhysicalDeviceProperties2 {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the chained structure that receives the extended properties, or <see langword="null"/>.</summary>
    public nint PNext;
    // VkPhysicalDeviceProperties payload: this query only consumes the chained
    // acceleration-structure struct, so an opaque blob suffices. sizeof is 824 on
    // 64-bit; padded to 1024 as a safety margin (oversize is invisible to the
    // driver, undersize would be stack corruption).
    /// <summary>The embedded <c>VkPhysicalDeviceProperties</c> payload, bound as an opaque byte blob (see remarks).</summary>
    public fixed byte Properties[1024];
}
