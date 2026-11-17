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
/// <param name="AddressBindings">The instance's address record, which a device created with
/// <see cref="VulkanDeviceAddressBindings.ExtensionName"/> reports its bindings to, or <see langword="null"/>.</param>
/// <param name="MemorySegments">The physical device's memory segments, one per heap, which the device registers with its
/// memory counts before its first counted allocation, or <see langword="null"/> for a device whose allocations are not
/// counted.</param>
/// <param name="EnabledFeatureFlags">Flags past the first of chained feature structures to enable, each with its
/// structure, which is chained with only the flags named for it (and its first flag when
/// <paramref name="EnabledFeatureStructureTypes"/> names it too), or <see langword="null"/> for none.</param>
public readonly record struct VulkanLogicalDeviceCreateRequest(
    VulkanInstanceCommands Instance,
    VkPhysicalDevice PhysicalDevice,
    IReadOnlyList<VulkanDeviceQueueCreateRequest> Queues,
    IReadOnlyList<string> ExtensionNames,
    IReadOnlyList<uint> EnabledFeatureIndices,
    IReadOnlyList<uint> EnabledFeatureStructureTypes,
    VulkanDeviceAddressBindings? AddressBindings = null,
    GpuMemorySegments? MemorySegments = null,
    IReadOnlyList<VulkanFeatureFlag>? EnabledFeatureFlags = null
);
/// <summary>One <c>VkBool32</c> of a chained <c>VkPhysicalDevice*Features</c> structure: its structure type and its
/// zero-based index among the structure's flags, which follow <c>sType</c> and <c>pNext</c>.</summary>
/// <param name="StructureType">The <c>VkStructureType</c> of the feature structure.</param>
/// <param name="Flag">The flag's zero-based index among the structure's <c>VkBool32</c> members.</param>
public readonly record struct VulkanFeatureFlag(uint StructureType, int Flag);
