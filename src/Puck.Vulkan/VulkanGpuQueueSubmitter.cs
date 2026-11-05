using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuQueueSubmitter"/> by forwarding to <see cref="VulkanQueueSubmitter"/>, resolving the
/// graphics queue from the device context by downcasting to <see cref="IVulkanDeviceContext"/>. Submission fences
/// are plain <c>VkFence</c>s created through the frame-synchronization API. External waits join the next submission's
/// wait list as timeline semaphores (<see cref="VulkanSharedFence"/>) with their values, at every stage. While a batch is
/// open a submission joins the device's batch (<see cref="VulkanSubmissionBatch"/>) instead of reaching the queue.
/// </summary>
public sealed class VulkanGpuQueueSubmitter(IVulkanDeviceContext deviceContext, VulkanQueueSubmitter queueSubmitter, IVulkanFrameSynchronizationApi frameSynchronizationApi) : IGpuQueueSubmitter {
    private int m_waitCount;
    private nint[] m_waitSemaphores = [];
    private ulong[] m_waitValues = [];

    private ReadOnlySpan<nint> WaitSemaphores => m_waitSemaphores.AsSpan(
        length: m_waitCount,
        start: 0
    );
    private ReadOnlySpan<ulong> WaitValues => m_waitValues.AsSpan(
        length: m_waitCount,
        start: 0
    );

    // Defers a submission to the device's open batch with the waits it carries, spending them; false when no batch is
    // open or the submission is empty.
    private bool Defers(ReadOnlySpan<nint> commandBufferHandles, VulkanGpuSubmissionFence? fence) {
        var batch = deviceContext.LogicalDevice.Batch;

        if (!batch.IsOpen || commandBufferHandles.IsEmpty) { return false; }
        _ = batch.TryDefer(entries: [new VulkanBatchedSubmission(CommandBuffers: commandBufferHandles.ToArray(), WaitSemaphores: WaitSemaphores.ToArray(), WaitValues: WaitValues.ToArray())], fence: fence);
        m_waitCount = 0;

        return true;
    }
    // A submission carries the list and spends it, even when it fails, so a device loss never carries a lost device's
    // semaphore onward; an empty submission reaches no queue, so it keeps the list.
    private void ClearWaitsAfter(ReadOnlySpan<nint> commandBufferHandles) {
        if (!commandBufferHandles.IsEmpty) {
            m_waitCount = 0;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Takes a <see cref="VulkanSharedFence"/> of this device.</remarks>
    public void AddExternalWait(GpuExternalWait wait) {
        ArgumentOutOfRangeException.ThrowIfZero(wait.Value);

        if (wait.Fence is not VulkanSharedFence fence) {
            throw new ArgumentException(
                message: $"A Vulkan submission waits only on an imported timeline semaphore, not a {(wait.Fence?.GetType().Name ?? "null")}.",
                paramName: nameof(wait)
            );
        }

        if (m_waitCount == m_waitSemaphores.Length) {
            var capacity = Math.Max(
                val1: 4,
                val2: (m_waitCount * 2)
            );

            Array.Resize(
                array: ref m_waitSemaphores,
                newSize: capacity
            );
            Array.Resize(
                array: ref m_waitValues,
                newSize: capacity
            );
        }

        m_waitSemaphores[m_waitCount] = fence.SemaphoreHandle;
        m_waitValues[m_waitCount] = wait.Value;
        ++m_waitCount;
    }
    /// <inheritdoc/>
    public IGpuSubmissionFence CreateSubmissionFence() => new VulkanGpuSubmissionFence(
        device: deviceContext.LogicalDevice.Commands,
        frameSynchronizationApi: frameSynchronizationApi
    );
    /// <inheritdoc/>
    public void BeginBatch() => deviceContext.LogicalDevice.Batch.Begin();
    /// <inheritdoc/>
    public void FlushBatch() => deviceContext.LogicalDevice.Batch.Flush();
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles) {
        // An empty submission reaches no queue, so it neither reads the queue's door nor splits an open batch.
        if (commandBufferHandles.IsEmpty || Defers(commandBufferHandles: commandBufferHandles, fence: null)) { return; }

        var logicalDevice = deviceContext.LogicalDevice;

        try {
            queueSubmitter.Submit(
                commandBufferHandles: commandBufferHandles,
                device: logicalDevice.Commands,
                graphicsQueue: logicalDevice.GraphicsQueue,
                waitSemaphores: WaitSemaphores,
                waitValues: WaitValues
            );
        } finally {
            ClearWaitsAfter(commandBufferHandles: commandBufferHandles);
        }
    }
    /// <inheritdoc/>
    /// <remarks>An empty submission carries no waits and still arms the fence, which signals once every submission made
    /// before it has completed.</remarks>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        var logicalDevice = deviceContext.LogicalDevice;
        var vkFence = ((VulkanGpuSubmissionFence)fence);

        if (Defers(commandBufferHandles: commandBufferHandles, fence: vkFence)) { return; }
        vkFence.RequireDrained();
        try {
            queueSubmitter.Submit(
                commandBufferHandles: commandBufferHandles,
                device: logicalDevice.Commands,
                fenceHandle: vkFence.Handle,
                graphicsQueue: logicalDevice.GraphicsQueue,
                waitSemaphores: (commandBufferHandles.IsEmpty ? [] : WaitSemaphores),
                waitValues: (commandBufferHandles.IsEmpty ? [] : WaitValues)
            );
        } finally {
            ClearWaitsAfter(commandBufferHandles: commandBufferHandles);
        }
        // A failed vkQueueSubmit leaves the fence untouched, so only a submit that returned arms it.
        vkFence.Arm();
    }
    /// <inheritdoc/>
    public void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        var logicalDevice = deviceContext.LogicalDevice;

        try {
            queueSubmitter.SubmitAndWait(
                commandBufferHandles: commandBufferHandles,
                device: logicalDevice.Commands,
                graphicsQueue: logicalDevice.GraphicsQueue,
                waitSemaphores: WaitSemaphores,
                waitValues: WaitValues
            );
        } finally {
            ClearWaitsAfter(commandBufferHandles: commandBufferHandles);
        }
    }
}

/// <summary>
/// The Vulkan <see cref="IGpuSubmissionFence"/>: one unsignaled <c>VkFence</c>, armed by a fenced queue submit and
/// drained by a bounded <c>vkWaitForFences</c> (<see cref="VulkanFenceWait"/>) + reset. Its batch and alias states are the shared
/// <see cref="GpuBatchedSubmissionFence"/>'s: a fence answering from its batch's owner waits on the owner's
/// <c>VkFence</c> without resetting it.
/// </summary>
internal sealed class VulkanGpuSubmissionFence : GpuBatchedSubmissionFence {
    private readonly VulkanDeviceCommands m_device;
    private readonly IVulkanFrameSynchronizationApi m_frameSynchronizationApi;

    private nint m_fenceHandle;

    internal VulkanGpuSubmissionFence(VulkanDeviceCommands device, IVulkanFrameSynchronizationApi frameSynchronizationApi) {
        m_device = device;
        m_frameSynchronizationApi = frameSynchronizationApi;
        m_frameSynchronizationApi.CreateFence(
            fenceHandle: out m_fenceHandle,
            request: new VulkanFrameSynchronizationCreateRequest(
                Device: device,
                StartSignaled: false
            )
        ).ThrowIfFailed(device: m_device, operation: "vkCreateFence");
    }

    /// <summary>Gets the native <c>VkFence</c> a submit signals; zero once disposed.</summary>
    internal nint Handle => m_fenceHandle;

    /// <summary>Marks a submission outstanding once the submit carrying <see cref="Handle"/> returned; the caller must
    /// have drained any prior submission first (<see cref="GpuBatchedSubmissionFence.Wait"/>), or the submission is the
    /// one its batch deferred.</summary>
    /// <exception cref="InvalidOperationException">A submission is outstanding.</exception>
    internal void Arm() => MarkArmed();

    /// <inheritdoc/>
    /// <remarks>Reads <c>vkGetFenceStatus</c>; <c>VK_ERROR_DEVICE_LOST</c> surfaces as <see cref="DeviceLostException"/>.</remarks>
    protected override bool NativeSignaled() {
        var status = m_frameSynchronizationApi.GetFenceStatus(
            device: m_device,
            fenceHandle: m_fenceHandle
        );

        if (status == VkResult.NotReady) {
            return false;
        }

        status.ThrowIfFailed(device: m_device, operation: "vkGetFenceStatus");

        return true;
    }
    /// <inheritdoc/>
    /// <remarks>Bounded by <see cref="VulkanFenceWait.TimeoutNanoseconds"/>: a hung GPU surfaces as a device loss
    /// (ThrowIfFailed maps <c>VK_ERROR_DEVICE_LOST</c> to the neutral <see cref="DeviceLostException"/> the host pump's
    /// recovery catches).</remarks>
    protected override void WaitNative() => VulkanFenceWait.Wait(
        api: m_frameSynchronizationApi,
        device: m_device,
        fenceHandle: m_fenceHandle
    ).ThrowIfFailed(device: m_device, operation: "vkWaitForFences");
    /// <inheritdoc/>
    protected override void DrainNative() => m_frameSynchronizationApi.ResetFence(
        device: m_device,
        fenceHandle: m_fenceHandle
    ).ThrowIfFailed(device: m_device, operation: "vkResetFences");
    /// <inheritdoc/>
    /// <remarks>Destroys the fence without waiting (the frame-ring owner drains the device before teardown, and a
    /// lost device has nothing left to wait on).</remarks>
    protected override void DisposeNative() {
        m_frameSynchronizationApi.DestroyFence(
            device: m_device,
            fenceHandle: m_fenceHandle
        );
        m_fenceHandle = 0;
    }
}
