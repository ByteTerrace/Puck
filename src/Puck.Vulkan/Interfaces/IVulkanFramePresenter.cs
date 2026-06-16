using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Runs the per-frame acquire → record → submit → present cycle, translating swapchain lifecycle codes into
/// an actionable <see cref="VulkanFramePresentationOutcome"/> instead of throwing.
/// </summary>
public interface IVulkanFramePresenter {
    /// <summary>Acquires a swapchain image, lets the caller (re)record exactly that
    /// image's command buffer via <paramref name="recordAcquiredImage"/>, then submits
    /// and presents it. The callback runs inside the post-fence-wait window — every
    /// prior submission has retired — so per-buffer recording cannot race the GPU.</summary>
    /// <param name="commandResources">The command pool and per-image command buffers used for the frame.</param>
    /// <param name="frameSynchronization">The fences and semaphores coordinating the frame.</param>
    /// <param name="logicalDevice">The logical device the frame is rendered on.</param>
    /// <param name="recordAcquiredImage">A callback that records the command buffer for the acquired image, given its index.</param>
    /// <param name="swapchain">The swapchain presented to.</param>
    /// <returns>An outcome describing whether the frame was presented, skipped, or requires swapchain or device recreation.</returns>
    VulkanFramePresentationOutcome Present(
        VulkanCommandResources commandResources,
        VulkanFrameSynchronization frameSynchronization,
        VulkanLogicalDevice logicalDevice,
        Action<uint> recordAcquiredImage,
        VulkanSwapchain swapchain
    );
}
