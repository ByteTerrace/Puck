using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Selects the most suitable physical device for rendering to a given surface.
/// </summary>
public interface IVulkanPhysicalDeviceSelector {
    /// <summary>Selects a physical device that can both render and present to the surface.</summary>
    /// <param name="instance">The Vulkan instance whose devices are considered.</param>
    /// <param name="surface">The surface the device must be able to present to.</param>
    /// <returns>The selected physical device, including its chosen queue families.</returns>
    VkPhysicalDevice Select(VulkanInstance instance, VulkanSurface surface);
}
