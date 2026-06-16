using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Identifies a single pipeline executable whose statistics or internal representations are queried with
/// <c>vkGetPipelineExecutableStatisticsKHR</c> (and related commands).
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineExecutableInfoKHR (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineExecutableInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_EXECUTABLE_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The pipeline that contains the executable (a <c>VkPipeline</c> handle).</summary>
    public nint Pipeline;
    /// <summary>The index of the executable within the pipeline being queried.</summary>
    public uint ExecutableIndex;
}
