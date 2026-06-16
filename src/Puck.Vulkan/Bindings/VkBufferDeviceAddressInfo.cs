using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Identifies the buffer whose device address is queried by <c>vkGetBufferDeviceAddress</c>.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkBufferDeviceAddressInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field
/// names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkBufferDeviceAddressInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_BUFFER_DEVICE_ADDRESS_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>The buffer whose device address is queried (a <c>VkBuffer</c> handle).</summary>
    public nint Buffer;
}
