using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native logical device entry points: device creation and destruction, queue retrieval, and
/// waiting for the device to become idle.
/// </summary>
public interface IVulkanLogicalDeviceApi {
    /// <summary>Creates a logical device and its queues.</summary>
    /// <param name="request">The logical device creation parameters.</param>
    /// <param name="deviceHandle">When this method returns, the created native <c>VkDevice</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the device was created successfully.</returns>
    VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out nint deviceHandle);
    /// <summary>Destroys a logical device.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle to destroy.</param>
    void DestroyDevice(nint deviceHandle);
    /// <summary>Retrieves a queue from a logical device.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="queueFamilyIndex">The index of the queue family.</param>
    /// <param name="queueIndex">The index of the queue within the family.</param>
    /// <returns>The native <c>VkQueue</c> handle.</returns>
    nint GetDeviceQueue(nint deviceHandle, uint queueFamilyIndex, uint queueIndex);
    /// <summary>Waits for all queues on the device to become idle.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the wait completed successfully.</returns>
    VkResult WaitIdle(nint deviceHandle);
}
