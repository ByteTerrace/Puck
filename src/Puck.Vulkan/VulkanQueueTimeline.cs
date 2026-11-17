using Puck.Vulkan.Interop;
using Puck.Vulkan.Bindings;

namespace Puck.Vulkan;

// Owned by VulkanLogicalDevice, not by the replaceable queue-service wrappers.
internal sealed class VulkanQueueTimeline(VulkanLogicalDevice device) : GpuQueueTimeline {
    private readonly nint m_semaphore = VulkanSharedFence.CreateTimeline(device.Commands);
    private readonly VulkanQueueSubmitter m_submitter = new();
    private bool m_bindingFailed;

    protected override void Signal(ulong value) {
        RequireOrdered();
        m_submitter.Signal(device: device.Commands,
            graphicsQueue: device.GraphicsQueue, commandBufferHandles: [], semaphore: m_semaphore, value: value);
    }
    protected override ulong ReadCompleted() {
        RequireOrdered();
        return VulkanSharedFence.ReadCompletedValue(device.Commands, m_semaphore);
    }
    internal ulong BindSparse(nint buffer, VkSparseMemoryBind[] bindings) {
        var readers = Checkpoint(); // This queue door flushes an open graphics batch before signaling N.
        try {
            var bound = SubmitCheckpoint(value => m_submitter.BindSparse(device.Commands, device.SparseQueue!.Value,
                buffer, bindings, m_semaphore, readers, value));
            // An empty submission with a wait is intentional: all later graphics work, including batched work
            // and the next timeline signal, follows N+1 even when the binding queue is a different family.
            m_submitter.Submit(device.Commands, device.GraphicsQueue, [], fenceHandle: 0,
                waitSemaphores: [m_semaphore], waitValues: [bound]);
            return bound;
        } catch {
            // An unknown bind or join result cannot be bypassed by signaling a larger value on graphics.
            // Retain all physical placements until generation teardown instead of guessing completion.
            m_bindingFailed = true;
            throw;
        }
    }
    private void RequireOrdered() {
        if (m_bindingFailed) { throw new InvalidOperationException("A sparse binding submission has an unknown outcome; this generation cannot certify retirement."); }
    }
    protected override void DisposeNative() {
        unsafe { device.Commands.Destroy(destroy: device.Commands.DestroySemaphore, handle: m_semaphore); }
    }
}
