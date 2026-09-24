using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Messages;

/// <summary>
/// Describes a logical device to create from a physical device: the queues, extensions, and features to enable.
/// </summary>
/// <param name="Instance">The command table of the instance.</param>
/// <param name="PhysicalDevice">The physical device the logical device is created from.</param>
/// <param name="Queues">The queues to create, one entry per queue.</param>
/// <param name="ExtensionNames">The names of the device extensions to enable.</param>
/// <param name="EnabledFeatureIndices">The indices, within the base <c>VkPhysicalDeviceFeatures</c> block, of the features to enable.</param>
/// <param name="EnabledFeatureStructureTypes">The <c>VkStructureType</c> values of the chained feature structures to enable.</param>
public readonly record struct VulkanLogicalDeviceCreateRequest(
    VulkanInstanceCommands Instance,
    VkPhysicalDevice PhysicalDevice,
    IReadOnlyList<VulkanDeviceQueueCreateRequest> Queues,
    IReadOnlyList<string> ExtensionNames,
    IReadOnlyList<uint> EnabledFeatureIndices,
    IReadOnlyList<uint> EnabledFeatureStructureTypes
);
