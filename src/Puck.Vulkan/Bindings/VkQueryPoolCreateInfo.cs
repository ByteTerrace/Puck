using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>Creates a query pool. A 1:1 ABI mirror of VkQueryPoolCreateInfo from vulkan_core.h.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkQueryPoolCreateInfo {
    /// <summary>VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO (11).</summary>
    public uint SType;
    /// <summary>The extension chain, or zero.</summary>
    public nint PNext;
    /// <summary>Reserved flags, zero.</summary>
    public uint Flags;
    /// <summary>The query type; timestamps use 2.</summary>
    public uint QueryType;
    /// <summary>The number of queries.</summary>
    public uint QueryCount;
    /// <summary>Pipeline statistics flags, zero for timestamps.</summary>
    public uint PipelineStatistics;
}
