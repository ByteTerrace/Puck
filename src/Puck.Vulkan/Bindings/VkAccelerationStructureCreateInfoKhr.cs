using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Parameters describing an acceleration structure to be created with
/// <c>vkCreateAccelerationStructureKHR</c> over a region of an existing buffer.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): the m_padN fields make the C compiler's implicit alignment padding explicit (no real field is
/// reordered). Offsets and size (64 B) match VkAccelerationStructureCreateInfoKHR exactly.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureCreateInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_CREATE_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkAccelerationStructureCreateFlagBitsKHR</c> specifying additional creation parameters.</summary>
    public uint CreateFlags;
    /// <summary>Explicit alignment padding; carries no meaningful value.</summary>
    public uint m_pad0;
    /// <summary>The buffer the acceleration structure is created on (a <c>VkBuffer</c> handle).</summary>
    public nint Buffer;
    /// <summary>The byte offset within <see cref="Buffer"/> at which the acceleration structure is placed.</summary>
    public ulong Offset;
    /// <summary>The size, in bytes, of the acceleration structure.</summary>
    public ulong Size;
    /// <summary>The level of the acceleration structure (top or bottom), as a <c>VkAccelerationStructureTypeKHR</c> value.</summary>
    public uint Type;
    /// <summary>Explicit alignment padding; carries no meaningful value.</summary>
    public uint m_pad1;
    /// <summary>The device address requested for the acceleration structure (capture/replay only), or zero.</summary>
    public ulong DeviceAddress;
}
