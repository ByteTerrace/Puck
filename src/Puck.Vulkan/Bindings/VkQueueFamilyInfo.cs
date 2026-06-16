namespace Puck.Vulkan.Bindings;

/// <summary>
/// A condensed, managed view of a physical device's queue family: its index together with the capability
/// flags and queue count distilled from <see cref="VkQueueFamilyProperties"/>.
/// </summary>
/// <param name="Index">The index of the queue family in the physical device's family list.</param>
/// <param name="Flags">The capabilities of the queues in the family.</param>
/// <param name="QueueCount">The number of queues in the family.</param>
public readonly record struct VkQueueFamilyInfo(
    uint Index,
    VkQueueFlags Flags,
    uint QueueCount
);
