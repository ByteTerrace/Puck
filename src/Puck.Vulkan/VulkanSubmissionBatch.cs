using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// The submissions one logical device defers while a batch is open (<see cref="IGpuQueueSubmitter.BeginBatch"/>): the
/// shared <see cref="GpuSubmissionBatch{TEntry}"/>, whose entries reach the device's graphics queue in one
/// <c>vkQueueSubmit</c>, one submit info each, in their order, each with the waits it was given. A <c>vkQueueSubmit</c>
/// signals one fence, so the batch's owner fence (its last fenced submission's) is the one the submit arms. Every other
/// operation on the device's queue reads it through <see cref="VulkanLogicalDevice.GraphicsQueue"/> or
/// <see cref="VulkanLogicalDevice.PresentQueue"/>, which split the batch first.
/// <para>A deferred submission's command buffers and waits are copied into lists the batch keeps, and its entry names
/// its ranges there, so deferring and handing off a batch allocate nothing once the lists have grown to the frame's
/// size. Single-threaded, like the base batch.</para>
/// </summary>
/// <param name="device">The command table of the device that owns the queue.</param>
/// <param name="queue">The device's graphics queue.</param>
internal sealed class VulkanSubmissionBatch(VulkanDeviceCommands device, VkQueue queue) : GpuSubmissionBatch<VulkanBatchedSubmission> {
    private Ranges m_deferred = new();
    // The ranges a hand-off is submitting, swapped out with the base batch's entries so a submission deferred while it
    // submits lands in the other lists.
    private Ranges m_handing = new();

    /// <summary>Defers a submission when the batch is open and the submission is not empty, as
    /// <see cref="GpuSubmissionBatch{TEntry}.TryDefer"/> does, copying its command buffers and waits.</summary>
    /// <param name="commandBuffers">The native <c>VkCommandBuffer</c> handles.</param>
    /// <param name="waitSemaphores">The timeline semaphores the submission waits on before any of its stages run.</param>
    /// <param name="waitValues">The value each of <paramref name="waitSemaphores"/> must reach, in the same order.</param>
    /// <param name="fence">The fence the submission arms, or <see langword="null"/>; it must have no submission
    /// outstanding.</param>
    /// <returns><see langword="true"/> when the submission was deferred; otherwise the caller submits it now.</returns>
    /// <exception cref="ArgumentException"><paramref name="waitSemaphores"/> and <paramref name="waitValues"/> differ in
    /// length.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="fence"/> has a submission outstanding.</exception>
    public bool TryDefer(ReadOnlySpan<nint> commandBuffers, ReadOnlySpan<nint> waitSemaphores, ReadOnlySpan<ulong> waitValues, GpuBatchedSubmissionFence? fence) {
        if (waitSemaphores.Length != waitValues.Length) {
            throw new ArgumentException(
                message: $"{waitSemaphores.Length} wait semaphores need as many values, not {waitValues.Length}.",
                paramName: nameof(waitValues)
            );
        }
        if (!IsOpen || commandBuffers.IsEmpty) { return false; }

        var entry = new VulkanBatchedSubmission(
            CommandBufferCount: commandBuffers.Length,
            CommandBufferStart: m_deferred.CommandBuffers.Count,
            WaitCount: waitSemaphores.Length,
            WaitStart: m_deferred.Waits.Count
        );

        // An entry names its ranges by where they start, so lists a refused defer extended only hold words no entry names.
        m_deferred.CommandBuffers.AddRange(source: commandBuffers);
        m_deferred.Waits.AddRange(source: waitSemaphores);
        m_deferred.Values.AddRange(source: waitValues);

        return TryDefer(entries: [entry], fence: fence, lists: commandBuffers.Length);
    }

    /// <inheritdoc/>
    /// <remarks>Arms the owner only once <c>vkQueueSubmit</c> returned success: a failed submit leaves its fence
    /// untouched, so the batch abandons it rather than leaving a wait on a fence nothing signals.</remarks>
    protected override void Submit(ReadOnlySpan<VulkanBatchedSubmission> entries, GpuBatchedSubmissionFence? owner) {
        var fence = ((VulkanGpuSubmissionFence?)owner);
        var ranges = m_deferred;

        (m_deferred, m_handing) = (m_handing, ranges);
        try {
            ranges.Stages.Clear();
            for (var wait = 0; (wait < ranges.Waits.Count); wait++) { ranges.Stages.Add(item: VulkanPipelineStageFlags.AllCommands); }
            CollectionsMarshal.SetCount(list: ranges.Timelines, count: entries.Length);
            CollectionsMarshal.SetCount(list: ranges.Submits, count: entries.Length);
            VulkanQueueSubmitter.SubmitBatch(
                commandBuffers: CollectionsMarshal.AsSpan(list: ranges.CommandBuffers),
                device: device,
                entries: entries,
                fenceHandle: (fence?.Handle ?? 0),
                graphicsQueue: queue,
                stages: CollectionsMarshal.AsSpan(list: ranges.Stages),
                submits: CollectionsMarshal.AsSpan(list: ranges.Submits),
                timelines: CollectionsMarshal.AsSpan(list: ranges.Timelines),
                waitSemaphores: CollectionsMarshal.AsSpan(list: ranges.Waits),
                waitValues: CollectionsMarshal.AsSpan(list: ranges.Values)
            );
        } finally {
            ranges.Clear();
        }
        fence?.Arm();
    }

    // The command buffers and waits of a batch's deferred submissions, and the submit structures a hand-off fills.
    private sealed class Ranges {
        public List<nint> CommandBuffers { get; } = [];
        public List<uint> Stages { get; } = [];
        public List<VkSubmitInfo> Submits { get; } = [];
        public List<VkTimelineSemaphoreSubmitInfo> Timelines { get; } = [];
        public List<ulong> Values { get; } = [];
        public List<nint> Waits { get; } = [];

        public void Clear() {
            CommandBuffers.Clear();
            Stages.Clear();
            Submits.Clear();
            Timelines.Clear();
            Values.Clear();
            Waits.Clear();
        }
    }
}
/// <summary>One deferred submission of a <see cref="VulkanSubmissionBatch"/>: its ranges in the batch's lists.</summary>
/// <param name="CommandBufferStart">The index of its first command buffer.</param>
/// <param name="CommandBufferCount">The number of its command buffers.</param>
/// <param name="WaitStart">The index of its first wait semaphore and value.</param>
/// <param name="WaitCount">The number of timeline semaphores it waits on.</param>
internal readonly record struct VulkanBatchedSubmission(int CommandBufferStart, int CommandBufferCount, int WaitStart, int WaitCount);
