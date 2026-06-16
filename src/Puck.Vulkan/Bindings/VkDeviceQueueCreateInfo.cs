using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Describes the queues to create from a single queue family when creating a logical device.
/// </summary>
/// <remarks>
/// 1:1 ABI mirror of VkDeviceQueueCreateInfo (vulkan_core.h, SDK 1.4): byte-identical layout, C#-idiomatic field names.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct VkDeviceQueueCreateInfo {
    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A bitmask of <c>VkDeviceQueueCreateFlagBits</c> specifying behavior of the queues.</summary>
    public uint Flags;
    /// <summary>The index of the queue family from which the queues are created.</summary>
    public uint QueueFamilyIndex;
    /// <summary>The number of queues to create from the family.</summary>
    public uint QueueCount;
    /// <summary>A pointer to an array of <see cref="QueueCount"/> normalized priorities (each in <c>[0, 1]</c>), one per queue.</summary>
    public nint PQueuePriorities;
}
