using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing a query pool to be created with <c>vkCreateQueryPool</c>: the query type, the
/// number of queries, and, for pipeline-statistics pools, which statistics are gathered.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkQueryPoolCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkQueryPoolCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>Reserved for future use; must be zero.</summary>
    public uint Flags;
    /// <summary>The kind of queries managed by the pool, as a <c>VkQueryType</c> value.</summary>
    public uint QueryType;
    /// <summary>The number of queries managed by the pool.</summary>
    public uint QueryCount;
    /// <summary>A bitmask of <c>VkQueryPipelineStatisticFlagBits</c>; used only when <see cref="QueryType"/> is pipeline statistics.</summary>
    public uint PipelineStatistics;
}
