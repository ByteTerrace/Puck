using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// One change to the device's GPU virtual address space, chained into a debug-utils message's callback data by
/// <c>VK_EXT_device_address_binding_report</c>: a range bound to or unbound from the object the message names.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDeviceAddressBindingCallbackDataEXT (vulkan_core.h, SDK 1.4): byte-identical layout, only ever
/// read through a pointer the loader supplies.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDeviceAddressBindingCallbackDataExt {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value.</summary>
    public uint StructureType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint Next;
    /// <summary>A bitmask of <c>VkDeviceAddressBindingFlagBitsEXT</c>: bit 0 marks a driver-internal object.</summary>
    public uint Flags;
    /// <summary>The first GPU virtual address of the range.</summary>
    public ulong BaseAddress;
    /// <summary>The range's size in bytes.</summary>
    public ulong Size;
    /// <summary>The <c>VkDeviceAddressBindingTypeEXT</c>: zero for a bind, one for an unbind.</summary>
    public uint BindingType;
}
