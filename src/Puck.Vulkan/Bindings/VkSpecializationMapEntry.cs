using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Maps a single specialization constant to its location within the supplied specialization data block.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkSpecializationMapEntry (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkSpecializationMapEntry {
    /// <summary>The ID of the specialization constant, as declared in the SPIR-V module.</summary>
    public uint ConstantId;
    /// <summary>The byte offset of the constant's value within the specialization data block.</summary>
    public uint Offset;
    /// <summary>The size, in bytes, of the constant's value within the specialization data block.</summary>
    public nuint Size;
}
