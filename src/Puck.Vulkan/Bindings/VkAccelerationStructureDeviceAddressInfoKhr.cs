using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Identifies the acceleration structure whose device address is queried by
/// <c>vkGetAccelerationStructureDeviceAddressKHR</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkAccelerationStructureDeviceAddressInfoKHR (vulkan_core.h, SDK 1.4): byte-identical layout,
/// C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkAccelerationStructureDeviceAddressInfoKhr {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_ACCELERATION_STRUCTURE_DEVICE_ADDRESS_INFO_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The acceleration structure whose device address is queried (a <c>VkAccelerationStructureKHR</c> handle).</summary>
    public nint AccelerationStructure;
}
