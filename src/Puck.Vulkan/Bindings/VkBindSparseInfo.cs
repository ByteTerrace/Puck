using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>ABI mirror of VkSparseMemoryBind: one aligned virtual range and its backing, or null memory to unbind.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkSparseMemoryBind {
    /// <summary>The first resource byte.</summary>
    public ulong ResourceOffset;
    /// <summary>The number of bytes.</summary>
    public ulong Size;
    /// <summary>The VkDeviceMemory, or zero for an unbind.</summary>
    public nint Memory;
    /// <summary>The first physical byte.</summary>
    public ulong MemoryOffset;
    /// <summary>The VkSparseMemoryBindFlags; zero for buffers.</summary>
    public uint Flags;
}
/// <summary>ABI mirror of VkSparseBufferMemoryBindInfo.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkSparseBufferMemoryBindInfo {
    /// <summary>The sparse VkBuffer.</summary>
    public nint Buffer;
    /// <summary>The number of ranges.</summary>
    public uint BindCount;
    /// <summary>A pointer to VkSparseMemoryBind ranges.</summary>
    public nint PBinds;
}
/// <summary>ABI mirror of VkBindSparseInfo. Buffer-only submissions leave every image count and pointer zero.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkBindSparseInfo {
    /// <summary>VK_STRUCTURE_TYPE_BIND_SPARSE_INFO.</summary>
    public const uint StructureType = 7;

    /// <summary>The structure type.</summary>
    public uint SType;
    /// <summary>The extension chain, including timeline values.</summary>
    public nint PNext;
    /// <summary>The number of wait semaphores.</summary>
    public uint WaitSemaphoreCount;
    /// <summary>The wait semaphores.</summary>
    public nint PWaitSemaphores;
    /// <summary>The number of buffer bind entries.</summary>
    public uint BufferBindCount;
    /// <summary>The buffer bind entries.</summary>
    public nint PBufferBinds;
    /// <summary>The number of opaque image bind entries.</summary>
    public uint ImageOpaqueBindCount;
    /// <summary>The opaque image bind entries.</summary>
    public nint PImageOpaqueBinds;
    /// <summary>The number of image bind entries.</summary>
    public uint ImageBindCount;
    /// <summary>The image bind entries.</summary>
    public nint PImageBinds;
    /// <summary>The number of signal semaphores.</summary>
    public uint SignalSemaphoreCount;
    /// <summary>The signal semaphores.</summary>
    public nint PSignalSemaphores;
}
