using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Exposes the core Vulkan objects established during bootstrap — the instance, surface, physical device,
/// and logical device — as a single shared context for the layers built on top of them.
/// </summary>
public interface IVulkanDeviceContext {
    /// <summary>Gets the Vulkan instance.</summary>
    VulkanInstance Instance { get; }
    /// <summary>Gets the logical device.</summary>
    VulkanLogicalDevice LogicalDevice { get; }
    /// <summary>Gets the selected physical device.</summary>
    VkPhysicalDevice PhysicalDevice { get; }
    /// <summary>Gets the presentation surface.</summary>
    VulkanSurface Surface { get; }
}
