using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports or requests the buffer-device-address features a physical device supports; chained into the
/// feature/device-creation query via <c>pNext</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkPhysicalDeviceBufferDeviceAddressFeatures (vulkan_core.h, SDK 1.4): byte-identical layout,
/// C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkPhysicalDeviceBufferDeviceAddressFeatures {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_BUFFER_DEVICE_ADDRESS_FEATURES</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A <c>VkBool32</c>; whether querying a buffer's device address and using it in shaders is supported.</summary>
    public uint BufferDeviceAddress;
    /// <summary>A <c>VkBool32</c>; whether saving and reusing buffer device addresses for capture/replay is supported.</summary>
    public uint BufferDeviceAddressCaptureReplay;
    /// <summary>A <c>VkBool32</c>; whether buffer device addresses can be used across multiple physical devices in a device group.</summary>
    public uint BufferDeviceAddressMultiDevice;
}
