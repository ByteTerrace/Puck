using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Receives a physical device's driver identity through a <c>VkPhysicalDeviceProperties2</c> <c>pNext</c> chain
/// (core in Vulkan 1.2, <c>VK_KHR_driver_properties</c> before it).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkPhysicalDeviceDriverProperties {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRIVER_PROPERTIES</c>).</summary>
    public uint SType;
    /// <summary>A pointer to the next structure in the chain, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The driver's identifier, as a <c>VkDriverId</c> value.</summary>
    public uint DriverId;
    /// <summary>The driver's name, a NUL-terminated UTF-8 <c>char[VK_MAX_DRIVER_NAME_SIZE]</c>.</summary>
    public fixed byte DriverName[256];
    /// <summary>The driver's version text as its vendor displays it, a NUL-terminated UTF-8 <c>char[VK_MAX_DRIVER_INFO_SIZE]</c>.</summary>
    public fixed byte DriverInfo[256];
    /// <summary>The major part of the conformance-test version the driver passed.</summary>
    public byte ConformanceMajor;
    /// <summary>The minor part of the conformance-test version.</summary>
    public byte ConformanceMinor;
    /// <summary>The subminor part of the conformance-test version.</summary>
    public byte ConformanceSubminor;
    /// <summary>The patch part of the conformance-test version.</summary>
    public byte ConformancePatch;
}
