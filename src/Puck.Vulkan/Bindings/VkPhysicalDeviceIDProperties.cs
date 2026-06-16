using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Receives a physical device's identity through a <c>VkPhysicalDeviceProperties2</c> <c>pNext</c> chain — in
/// particular its adapter LUID, which a Direct3D 12 device must be created on to share GPU resources with the
/// Vulkan device.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkPhysicalDeviceIDProperties {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the next structure in the chain, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The universally unique identifier of the device.</summary>
    public fixed byte DeviceUuid[16];
    /// <summary>The universally unique identifier of the driver.</summary>
    public fixed byte DriverUuid[16];
    /// <summary>The locally unique identifier of the device's adapter (valid only when <see cref="DeviceLuidValid"/> is non-zero).</summary>
    public fixed byte DeviceLuid[8];
    /// <summary>A bitmask identifying the nodes of a linked device the LUID corresponds to.</summary>
    public uint DeviceNodeMask;
    /// <summary>Whether <see cref="DeviceLuid"/> (and <see cref="DeviceNodeMask"/>) are valid, as a <c>VkBool32</c>.</summary>
    public uint DeviceLuidValid;
}
