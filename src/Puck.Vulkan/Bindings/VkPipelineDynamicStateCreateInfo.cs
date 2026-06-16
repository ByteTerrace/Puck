using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Lists the pieces of pipeline state that are set dynamically through command buffer commands rather than
/// being baked into the pipeline at creation.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineDynamicStateCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineDynamicStateCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The number of entries in the <see cref="PDynamicStates"/> array.</summary>
    public uint DynamicStateCount;
    /// <summary>A pointer to an array of <c>VkDynamicState</c> values naming the state that is set dynamically.</summary>
    public nint PDynamicStates;
}
