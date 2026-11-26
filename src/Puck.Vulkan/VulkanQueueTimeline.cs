using Puck.Vulkan.Interop;
using Puck.Vulkan.Bindings;

namespace Puck.Vulkan;

// Owned by VulkanLogicalDevice, not by the replaceable queue-service wrappers.
internal sealed class VulkanQueueTimeline(VulkanLogicalDevice device) : GpuQueueTimeline {
    private readonly nint m_semaphore = VulkanSharedFence.CreateTimeline(device: device.Commands);
    private readonly VulkanQueueSubmitter m_submitter = new();

    private bool m_bindingFailed;

    // Whether every binding this timeline issued has a known outcome, so its values certify retirement.
    internal bool Certifies => !m_bindingFailed;
    // Whether a value is taken and its signal not yet on the queue, when the queue's door issues no sparse operation,
    // since those signal values of their own (VulkanLogicalDevice.IssueSparse).
    internal bool IsSignaling => Signaling;

    // The queue's door: hands over the open batch and issues the queued sparse operations, whose values come before the
    // checkpoint's.
    protected override void BeforeCheckpoint() => _ = device.GraphicsQueue;
    protected override void Signal(ulong value) {
        RequireOrdered();
        m_submitter.Signal(device: device.Commands,
            graphicsQueue: device.GraphicsQueue, commandBufferHandles: [], semaphore: m_semaphore, value: value);
    }
    protected override ulong ReadCompleted() {
        RequireOrdered();
        return VulkanSharedFence.ReadCompletedValue(device: device.Commands, semaphore: m_semaphore);
    }

    // Binds each batch in order on the binding queue: a graphics checkpoint N after every earlier submission, each batch
    // waiting the value before it and signaling the next, and one graphics wait on the last, which it returns. Only the
    // device's sparse issue (VulkanLogicalDevice.IssueSparse) calls it, inside the door read that issues.
    internal ulong BindSparse(ReadOnlySpan<(nint Buffer, VkSparseMemoryBind[] Bindings)> batches) {
        var wait = Checkpoint(); // This queue door flushes an open graphics batch before signaling N.

        try {
            foreach (var (buffer, bindings) in batches) {
                var after = wait;

                wait = SubmitCheckpoint(submit: value => m_submitter.BindSparse(device.Commands, device.SparseQueue!.Value,
                    buffer, bindings, m_semaphore, after, value));
            }
            // An empty submission with a wait is intentional: all later graphics work, including batched work
            // and the next timeline signal, follows the last bind even when the binding queue is a different family.
            m_submitter.Submit(device.Commands, device.GraphicsQueue, [], fenceHandle: 0,
                waitSemaphores: [m_semaphore], waitValues: [wait]);
            return wait;
        } catch {
            // An unknown bind or join result cannot be bypassed by signaling a larger value on graphics.
            // Retain all physical placements until generation teardown instead of guessing completion.
            m_bindingFailed = true;
            throw;
        }
    }

    private void RequireOrdered() {
        if (m_bindingFailed) { throw new InvalidOperationException(message: "A sparse binding submission has an unknown outcome; this generation cannot certify retirement."); }
    }

    protected override void DisposeNative() {
        unsafe { device.Commands.Destroy(destroy: device.Commands.DestroySemaphore, handle: m_semaphore); }
    }
}
