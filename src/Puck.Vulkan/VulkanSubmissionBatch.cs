using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// The submissions one device defers while a batch is open (<see cref="IGpuQueueSubmitter.BeginBatch"/>), handed to its
/// queue in one <c>vkQueueSubmit</c> by <see cref="Flush"/>, one submit info each, in their order, each with the waits it
/// was given. Every other submission to the device's queue (<see cref="VulkanQueueSubmitter"/>) and the device's wait for
/// idle flush it first, so nothing reaches the queue ahead of work submitted before it. A <c>vkQueueSubmit</c> signals one
/// fence, so the last fenced submission's own fence signals the batch and every other fenced submission's fence answers
/// from it (<see cref="VulkanGpuSubmissionFence"/>). Single-threaded, like every pump-thread GPU object.
/// </summary>
public sealed class VulkanSubmissionBatch {
    private readonly List<VulkanBatchedSubmission> m_entries = [];
    private VkQueue m_queue;

    /// <summary>Gets whether a batch is open: a submission made now is deferred.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Opens the batch.</summary>
    public void Begin() => IsOpen = true;
    /// <summary>Defers a submission to the open batch.</summary>
    /// <param name="queue">The queue it is bound for.</param>
    /// <param name="commandBufferHandles">Its command buffers.</param>
    /// <param name="waitSemaphores">The timeline semaphores it waits on.</param>
    /// <param name="waitValues">The value each must reach.</param>
    /// <param name="fence">The fence it arms, or <see langword="null"/>.</param>
    internal void Add(VkQueue queue, ReadOnlySpan<nint> commandBufferHandles, ReadOnlySpan<nint> waitSemaphores, ReadOnlySpan<ulong> waitValues, VulkanGpuSubmissionFence? fence) {
        m_queue = queue;
        m_entries.Add(item: new VulkanBatchedSubmission(CommandBuffers: commandBufferHandles.ToArray(), Fence: fence, WaitSemaphores: waitSemaphores.ToArray(), WaitValues: waitValues.ToArray()));
        fence?.Defer(batch: this);
    }
    /// <summary>Hands the deferred submissions to the queue and closes the batch.</summary>
    /// <param name="device">The device the batch belongs to.</param>
    public void Flush(VulkanDeviceCommands device) {
        IsOpen = false;
        if (m_entries.Count == 0) { return; }

        var entries = m_entries.ToArray();
        VulkanGpuSubmissionFence? owner = null;

        m_entries.Clear();
        for (var index = (entries.Length - 1); (index >= 0); index--) {
            if (entries[index].Fence is { } fence) {
                owner = fence;
                break;
            }
        }
        try {
            VulkanQueueSubmitter.SubmitBatch(device: device, entries: entries, fenceHandle: (owner?.Arm() ?? 0), graphicsQueue: m_queue);
        } finally {
            foreach (var entry in entries) {
                if ((entry.Fence is { } fence) && !ReferenceEquals(objA: fence, objB: owner)) { fence.Alias(owner: owner!); }
            }
        }
    }
}
/// <summary>One deferred submission of a <see cref="VulkanSubmissionBatch"/>.</summary>
/// <param name="CommandBuffers">Its command buffers.</param>
/// <param name="WaitSemaphores">The timeline semaphores it waits on.</param>
/// <param name="WaitValues">The value each must reach.</param>
/// <param name="Fence">The fence it arms, or <see langword="null"/>.</param>
internal readonly record struct VulkanBatchedSubmission(nint[] CommandBuffers, nint[] WaitSemaphores, ulong[] WaitValues, VulkanGpuSubmissionFence? Fence);
