using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a <see cref="VulkanLogicalDevice"/> from a physical device, enabling the queues and optional
/// features the renderer requires.
/// </summary>
public interface IVulkanLogicalDeviceFactory {
    /// <summary>Creates a logical device and retrieves its queues.</summary>
    /// <param name="instance">The Vulkan instance the device is created under.</param>
    /// <param name="physicalDevice">The selected physical device, including its chosen queue families.</param>
    /// <returns>A new, owning <see cref="VulkanLogicalDevice"/>.</returns>
    VulkanLogicalDevice Create(VulkanInstance instance, VkPhysicalDevice physicalDevice);
}
