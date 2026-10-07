using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>The counts returned by <c>vkGetDeviceFaultInfoEXT</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkDeviceFaultCountsExt {
    /// <summary>The structure type, <c>VK_STRUCTURE_TYPE_DEVICE_FAULT_COUNTS_EXT</c>.</summary>
    public uint SType;
    /// <summary>The next structure, or zero.</summary>
    public nint PNext;
    /// <summary>The number of address records.</summary>
    public uint AddressInfoCount;
    /// <summary>The number of vendor records.</summary>
    public uint VendorInfoCount;
    /// <summary>The binary dump size in bytes.</summary>
    public ulong VendorBinarySize;
}
/// <summary>A fault address and the precision with which the driver identifies it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VkDeviceFaultAddressInfoExt {
    /// <summary>The <c>VkDeviceFaultAddressTypeEXT</c> value.</summary>
    public uint AddressType;
    /// <summary>The reported GPU virtual address.</summary>
    public ulong ReportedAddress;
    /// <summary>The address range's alignment and size in bytes.</summary>
    public ulong AddressPrecision;
}
/// <summary>A driver-specific fault description, code and associated data.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkDeviceFaultVendorInfoExt {
    /// <summary>The null-terminated UTF-8 description.</summary>
    public fixed byte Description[256];
    /// <summary>The vendor fault code.</summary>
    public ulong VendorFaultCode;
    /// <summary>The vendor fault data.</summary>
    public ulong VendorFaultData;
}
/// <summary>The description and output arrays filled by <c>vkGetDeviceFaultInfoEXT</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkDeviceFaultInfoExt {
    /// <summary>The structure type, <c>VK_STRUCTURE_TYPE_DEVICE_FAULT_INFO_EXT</c>.</summary>
    public uint SType;
    /// <summary>The next structure, or zero.</summary>
    public nint PNext;
    /// <summary>The null-terminated UTF-8 fault description.</summary>
    public fixed byte Description[256];
    /// <summary>The address output array.</summary>
    public VkDeviceFaultAddressInfoExt* PAddressInfos;
    /// <summary>The vendor output array.</summary>
    public VkDeviceFaultVendorInfoExt* PVendorInfos;
    /// <summary>The optional vendor binary output buffer.</summary>
    public void* PVendorBinaryData;
}
