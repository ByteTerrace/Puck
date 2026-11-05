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
/// </summary>
/// <param name="device">The command table of the device that owns the queue.</param>
/// <param name="queue">The device's graphics queue.</param>
internal sealed class VulkanSubmissionBatch(VulkanDeviceCommands device, VkQueue queue) : GpuSubmissionBatch<VulkanBatchedSubmission> {
    /// <inheritdoc/>
    /// <remarks>Arms the owner only once <c>vkQueueSubmit</c> returned success: a failed submit leaves its fence
    /// untouched, so the batch abandons it rather than leaving a wait on a fence nothing signals.</remarks>
    protected override void Submit(ReadOnlySpan<VulkanBatchedSubmission> entries, GpuBatchedSubmissionFence? owner) {
        var fence = ((VulkanGpuSubmissionFence?)owner);

        VulkanQueueSubmitter.SubmitBatch(device: device, entries: entries, fenceHandle: (fence?.Handle ?? 0), graphicsQueue: queue);
        fence?.Arm();
    }
}
/// <summary>One deferred submission of a <see cref="VulkanSubmissionBatch"/>.</summary>
/// <param name="CommandBuffers">Its command buffers.</param>
/// <param name="WaitSemaphores">The timeline semaphores it waits on.</param>
/// <param name="WaitValues">The value each must reach.</param>
internal readonly record struct VulkanBatchedSubmission(nint[] CommandBuffers, nint[] WaitSemaphores, ulong[] WaitValues);
