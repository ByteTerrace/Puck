using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

// Owned by VulkanLogicalDevice, not by the replaceable queue-service wrappers.
internal sealed class VulkanQueueTimeline(VulkanLogicalDevice device) : GpuQueueTimeline {
    private readonly nint m_semaphore = VulkanSharedFence.CreateTimeline(device.Commands);
    private readonly VulkanQueueSubmitter m_submitter = new();

    protected override void Signal(ulong value) => m_submitter.Signal(device: device.Commands,
        graphicsQueue: device.GraphicsQueue, commandBufferHandles: [], semaphore: m_semaphore, value: value);
    protected override ulong ReadCompleted() {
        unsafe {
            device.Commands.GetSemaphoreCounterValue(device.Commands.Handle, m_semaphore, out var value)
                .ThrowIfFailed(device: device.Commands, operation: "vkGetSemaphoreCounterValue");
            return value;
        }
    }
    protected override void DisposeNative() {
        unsafe { device.Commands.Destroy(destroy: device.Commands.DestroySemaphore, handle: m_semaphore); }
    }
}
