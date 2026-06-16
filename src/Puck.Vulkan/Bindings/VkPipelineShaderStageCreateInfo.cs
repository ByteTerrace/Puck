using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes a single programmable shader stage within a pipeline: which stage it is, the module and entry
/// point that supply its code, and any specialization constants.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineShaderStageCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic
/// field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineShaderStageCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkPipelineShaderStageCreateFlagBits</c> specifying how the stage is created.</summary>
    public uint Flags;
    /// <summary>The single pipeline stage this structure describes, as a <c>VkShaderStageFlagBits</c> value.</summary>
    public uint Stage;
    /// <summary>The shader module containing the code for this stage (a <c>VkShaderModule</c> handle), or <see langword="null"/> when the module is chained via <see cref="PNext"/>.</summary>
    public nint Module;
    /// <summary>A pointer to a null-terminated UTF-8 string naming the entry point of the shader for this stage.</summary>
    public nint PName;
    /// <summary>A pointer to a <c>VkSpecializationInfo</c> structure, or <see langword="null"/> if the stage has no specialization constants.</summary>
    public nint PSpecializationInfo;
}
