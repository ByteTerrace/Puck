namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a single queue to create from a queue family, with its scheduling priority.
/// </summary>
/// <param name="FamilyIndex">The index of the queue family to create the queue from.</param>
/// <param name="Priority">The normalized priority of the queue, in <c>[0, 1]</c>.</param>
public readonly record struct VulkanDeviceQueueCreateRequest(uint FamilyIndex, float Priority);
