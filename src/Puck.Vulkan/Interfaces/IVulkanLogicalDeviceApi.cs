using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native logical device entry points: device creation and destruction, queue retrieval, and
/// waiting for the device to become idle.
/// </summary>
public interface IVulkanLogicalDeviceApi {
    /// <summary>Creates a logical device and its queues.</summary>
    /// <param name="request">The logical device creation parameters.</param>
    /// <param name="device">When this method returns, the command table of the created device, resolved once here
    /// through <c>vkGetDeviceProcAddr</c>; <see langword="null"/> when creation failed or returned no handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the device was created successfully.</returns>
    VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device);
    /// <summary>Destroys a logical device.</summary>
    /// <param name="device">The command table of the device to destroy; it is never used again.</param>
    void DestroyDevice(VulkanDeviceCommands device);
    /// <summary>Retrieves a queue from a logical device.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="queueFamilyIndex">The index of the queue family.</param>
    /// <param name="queueIndex">The index of the queue within the family.</param>
    /// <returns>The native <c>VkQueue</c> handle.</returns>
    nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex);
    /// <summary>Waits for all queues on the device to become idle.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the wait completed successfully.</returns>
    VkResult WaitIdle(VulkanDeviceCommands device);
}
