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
    /// <summary>Whether the device exposes <c>vkWaitForPresentKHR</c> (i.e. <c>VK_KHR_present_wait</c> was enabled at device creation).</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <returns><see langword="true"/> when present-wait is available, enabling closed-loop present timing.</returns>
    bool SupportsPresentWait(nint deviceHandle);
    /// <summary>Blocks until the present identified by <paramref name="presentId"/> has been displayed, or the bounded timeout elapses.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="swapchainHandle">The native <c>VkSwapchainKHR</c> the present was queued against.</param>
    /// <param name="presentId">The present id (from a prior present's <c>VkPresentIdKHR</c>) to wait for.</param>
    /// <param name="timeoutNanoseconds">A bound, in nanoseconds, so a missed present can never hang the caller.</param>
    /// <returns><see cref="VkResult.Success"/> when the present was confirmed; <see cref="VkResult.Timeout"/> or a swapchain status code otherwise.</returns>
    VkResult WaitForPresent(nint deviceHandle, nint swapchainHandle, ulong presentId, ulong timeoutNanoseconds);
    /// <summary>Drops any cached per-device function pointers for <paramref name="deviceHandle"/>, so a later device reusing
    /// the same handle value re-resolves them rather than calling through pointers bound to the destroyed device. Safe to
    /// call with a handle that was never cached (a no-op).</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle whose cached entry points should be discarded.</param>
    void InvalidateDevice(nint deviceHandle);
}
