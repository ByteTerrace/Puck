using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a range of the push constant block and the shader stages that access it.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPushConstantRange (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPushConstantRange {
    /// <summary>A bitmask of <c>VkShaderStageFlagBits</c> identifying the shader stages that access this range.</summary>
    public uint StageFlags;
    /// <summary>The start offset, in bytes, of the range. Must be a multiple of 4.</summary>
    public uint Offset;
    /// <summary>The size, in bytes, of the range. Must be a multiple of 4.</summary>
    public uint Size;
}
