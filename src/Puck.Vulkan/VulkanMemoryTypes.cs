using Puck.Vulkan.Bindings;

namespace Puck.Vulkan;

/// <summary>
/// Selects a device memory type for a buffer or image from the physical device's reported types.
/// </summary>
public static class VulkanMemoryTypes {
    /// <summary>Selects a device memory-type index compatible with a resource's requirements and desired properties.</summary>
    /// <param name="memoryProperties">The physical device's reported memory types.</param>
    /// <param name="memoryTypeBits">The bitmask of memory types the resource's requirements permit.</param>
    /// <param name="preferredProperties">A bitmask of <c>VkMemoryPropertyFlagBits</c> a selected type should carry in full.</param>
    /// <param name="requireProperties"><see langword="true"/> to fail when no permitted type carries every preferred property; <see langword="false"/> to fall back to the first permitted type.</param>
    /// <param name="resourceDescription">A short noun phrase naming the resource, used in the failure message.</param>
    /// <returns>The index of a compatible memory type.</returns>
    /// <exception cref="InvalidOperationException">No permitted memory type carries the preferred properties and either <paramref name="requireProperties"/> is set or none is permitted at all.</exception>
    public static uint FindIndex(
        in VkPhysicalDeviceMemoryProperties memoryProperties,
        uint memoryTypeBits,
        uint preferredProperties,
        bool requireProperties,
        string resourceDescription
    ) {
        var fallbackIndex = -1;

        for (var index = 0; (index < memoryProperties.MemoryTypeCount); index++) {
            if (0 == (memoryTypeBits & (1u << index))) {
                continue;
            }

            if ((memoryProperties.MemoryTypePropertyFlags(memoryTypeIndex: index) & preferredProperties) == preferredProperties) {
                return ((uint)index);
            }

            if (fallbackIndex < 0) {
                fallbackIndex = index;
            }
        }

        if (
            requireProperties ||
            (fallbackIndex < 0)
        ) {
            throw new InvalidOperationException(message: $"The Vulkan physical device did not report a compatible memory type for {resourceDescription}.");
        }

        return ((uint)fallbackIndex);
    }
}
