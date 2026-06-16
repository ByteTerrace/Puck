using Puck.Vulkan.Interop;

namespace Puck.Vulkan.Interfaces;

/// <summary>
/// Creates a <see cref="VulkanFrameSynchronization"/> — the fences and semaphores that coordinate a frame's
/// acquire, submit, and present steps.
/// </summary>
public interface IVulkanFrameSynchronizationFactory {
    /// <summary>Creates the per-frame synchronization primitives.</summary>
    /// <param name="logicalDevice">The logical device the primitives are created on.</param>
    /// <param name="renderFinishedSemaphoreCount">The number of render-finished semaphores to create (typically one per swapchain image).</param>
    /// <returns>A new, owning <see cref="VulkanFrameSynchronization"/>.</returns>
    VulkanFrameSynchronization Create(VulkanLogicalDevice logicalDevice, int renderFinishedSemaphoreCount);
}
