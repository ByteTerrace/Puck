using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports the general properties of a physical device: its supported API and driver versions, vendor and
/// device identity, type, name, pipeline cache UUID, and device limits.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): truncated prefix through limits, declared so the compiler computes limits.timestampPeriod's
/// offset from the field ABI. Sequential layout with default packing honours natural alignment, matching the Vulkan C
/// struct on x64 (the ulong / nuint members carry the 8-byte alignment exactly as the C header does). Trailing limits
/// fields and sparseProperties are omitted; only the offset up to timestampPeriod matters, and the source buffer is the
/// full native struct.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkPhysicalDeviceProperties {
    /// <summary>The highest version of the Vulkan API the device supports, packed as by <c>VK_MAKE_API_VERSION</c>.</summary>
    public uint ApiVersion;
    /// <summary>The vendor-specific version of the driver.</summary>
    public uint DriverVersion;
    /// <summary>The PCI vendor ID, or another vendor identifier, of the device.</summary>
    public uint VendorId;
    /// <summary>The vendor-assigned identifier of the device.</summary>
    public uint DeviceId;
    /// <summary>The kind of device, as a <see cref="VkPhysicalDeviceType"/> value.</summary>
    public uint DeviceType;
    /// <summary>The name of the device, as a null-terminated UTF-8 string in a fixed 256-byte buffer.</summary>
    public fixed byte DeviceName[256];
    /// <summary>A universally unique identifier for the device's pipeline cache (16 bytes).</summary>
    public fixed byte PipelineCacheUuid[16];
    /// <summary>The device-specific limits of the physical device.</summary>
    public VkPhysicalDeviceLimits Limits;
}
