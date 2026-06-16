using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Identifies a pipeline whose executables are enumerated with
/// <c>vkGetPipelineExecutablePropertiesKHR</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineInfoKHR (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The pipeline being queried (a <c>VkPipeline</c> handle).</summary>
    public nint Pipeline;
}
