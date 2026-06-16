using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes the input assembly state of a graphics pipeline: the primitive topology vertices are
/// assembled into, and whether primitive restart is enabled.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineInputAssemblyStateCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout,
/// C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineInputAssemblyStateCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The primitive topology that input vertices are assembled into, as a <c>VkPrimitiveTopology</c> value.</summary>
    public uint Topology;
    /// <summary>A <c>VkBool32</c>; <c>VK_TRUE</c> enables restarting strip and fan primitives at the special restart index.</summary>
    public uint PrimitiveRestartEnable;
}
