using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a pipeline cache to be created with <c>vkCreatePipelineCache</c>: the data a previous cache
/// serialized, if any.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPipelineCacheCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPipelineCacheCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_CACHE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkPipelineCacheCreateFlagBits</c>; zero leaves the cache internally synchronized.</summary>
    public uint Flags;
    /// <summary>The number of bytes at <see cref="PInitialData"/>, or zero for an empty cache.</summary>
    public nuint InitialDataSize;
    /// <summary>A pointer to data <c>vkGetPipelineCacheData</c> produced, or <see langword="null"/>.</summary>
    public nint PInitialData;
}
