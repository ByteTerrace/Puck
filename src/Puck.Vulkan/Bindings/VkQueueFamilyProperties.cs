using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports the properties of a single queue family of a physical device: its capabilities, queue count,
/// timestamp precision, and minimum image transfer granularity.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkQueueFamilyProperties (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkQueueFamilyProperties {
    /// <summary>A bitmask of <c>VkQueueFlagBits</c> describing the capabilities of the queues in this family.</summary>
    public uint QueueFlags;
    /// <summary>The number of queues in the family.</summary>
    public uint QueueCount;
    /// <summary>The number of meaningful high-order bits in timestamps written by queues in this family, or zero if timestamps are unsupported.</summary>
    public uint TimestampValidBits;
    /// <summary>The minimum granularity, in texels, supported for image transfer operations on queues in this family.</summary>
    public VkExtent3D MinImageTransferGranularity;
}
