using Puck.Vulkan.Bindings;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Wraps the native entry points that drive a frame's acquire → submit → present cycle.
/// </summary>
public interface IVulkanFramePresentationApi {
    /// <summary>Acquires the index of the next presentable image from a swapchain.</summary>
    /// <param name="request">The acquire parameters, including the swapchain and the semaphore/fence signaled when the image is ready.</param>
    /// <param name="imageIndex">When this method returns, the index of the acquired swapchain image.</param>
    /// <returns>A <see cref="VkResult"/>; success or a swapchain status code such as <see cref="VkResult.SuboptimalKhr"/> or <see cref="VkResult.ErrorOutOfDateKhr"/>.</returns>
    VkResult AcquireNextImage(VulkanFrameAcquireRequest request, out uint imageIndex);
    /// <summary>Queues an acquired image for presentation.</summary>
    /// <param name="request">The present parameters, including the swapchain, image index, and wait semaphores.</param>
    /// <returns>A <see cref="VkResult"/>; success or a swapchain status code such as <see cref="VkResult.SuboptimalKhr"/> or <see cref="VkResult.ErrorOutOfDateKhr"/>.</returns>
    VkResult Present(VulkanPresentRequest request);
    /// <summary>Submits a frame's command buffer to the graphics queue with full wait/signal synchronization.</summary>
    /// <param name="request">The submit parameters, including the command buffer, wait/signal semaphores, and the fence signaled on completion.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the submission succeeded.</returns>
    VkResult Submit(VulkanFrameSubmitRequest request);
    /// <summary>Submits a single command buffer to a queue, signaling a fence on completion, with no semaphore synchronization.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="graphicsQueueHandle">The native <c>VkQueue</c> handle to submit to.</param>
    /// <param name="commandBufferHandle">The native <c>VkCommandBuffer</c> handle to execute.</param>
    /// <param name="fenceHandle">The native <c>VkFence</c> handle signaled when the submission completes.</param>
    /// <returns>A <see cref="VkResult"/> indicating whether the submission succeeded.</returns>
    VkResult Submit(nint deviceHandle, nint graphicsQueueHandle, nint commandBufferHandle, nint fenceHandle);
}
