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
        var device = deviceContext.LogicalDevice;

        if (!device.Commands.Batch.IsOpen || commandBufferHandles.IsEmpty) { return false; }
        if (fence is not null) { fence.RequireDrained(); }
        device.Commands.Batch.Add(commandBufferHandles: commandBufferHandles, fence: fence, queue: device.GraphicsQueue, waitSemaphores: WaitSemaphores, waitValues: WaitValues);
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
    public IGpuSubmissionFence CreateSubmissionFence() {
        var vkContext = deviceContext;

        return new VulkanGpuSubmissionFence(
            device: vkContext.LogicalDevice.Commands,
            frameSynchronizationApi: frameSynchronizationApi
        );
    }
    /// <inheritdoc/>
    public void BeginBatch() => deviceContext.LogicalDevice.Commands.Batch.Begin();
    /// <inheritdoc/>
    public void FlushBatch() => deviceContext.LogicalDevice.Commands.Batch.Flush(device: deviceContext.LogicalDevice.Commands);
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles) {
        var vkContext = deviceContext;

        if (Defers(commandBufferHandles: commandBufferHandles, fence: null)) { return; }
        try {
            queueSubmitter.Submit(
                commandBufferHandles: commandBufferHandles,
                device: vkContext.LogicalDevice.Commands,
                graphicsQueue: vkContext.LogicalDevice.GraphicsQueue,
                waitSemaphores: WaitSemaphores,
                waitValues: WaitValues
            );
        } finally {
            ClearWaitsAfter(commandBufferHandles: commandBufferHandles);
        }
    }
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        var vkContext = deviceContext;
        var vkFence = ((VulkanGpuSubmissionFence)fence);

        if (Defers(commandBufferHandles: commandBufferHandles, fence: vkFence)) { return; }
        try {
            queueSubmitter.Submit(
                commandBufferHandles: commandBufferHandles,
                device: vkContext.LogicalDevice.Commands,
                fenceHandle: vkFence.Arm(),
                graphicsQueue: vkContext.LogicalDevice.GraphicsQueue,
                waitSemaphores: WaitSemaphores,
                waitValues: WaitValues
            );
        } finally {
            ClearWaitsAfter(commandBufferHandles: commandBufferHandles);
        }
    }
    /// <inheritdoc/>
    public void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        var vkContext = deviceContext;

        try {
            queueSubmitter.SubmitAndWait(
                commandBufferHandles: commandBufferHandles,
                device: vkContext.LogicalDevice.Commands,
                graphicsQueue: vkContext.LogicalDevice.GraphicsQueue,
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
/// drained by an unbounded <c>vkWaitForFences</c> + reset. A submission deferred to a batch arms it only when the batch
/// flushes: the batch's last fenced submission arms its own fence, and every other one answers from that fence, as
/// signaled once the owner has drained it (its generation moves on each drain). Single-threaded like every other
/// pump-thread GPU object.
/// </summary>
internal sealed class VulkanGpuSubmissionFence : IGpuSubmissionFence {
    private readonly VulkanDeviceCommands m_device;
    private readonly IVulkanFrameSynchronizationApi m_frameSynchronizationApi;

    private nint m_fenceHandle;
    private bool m_pending;
    // The open batch a deferred submission waits in, until it flushes.
    private VulkanSubmissionBatch? m_deferred;
    // The fence whose submission signals this one's, and its generation when it was armed.
    private VulkanGpuSubmissionFence? m_alias;
    private long m_aliasGeneration;
    private long m_generation;

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

    /// <inheritdoc/>
    /// <remarks>Reads <c>vkGetFenceStatus</c>; <c>VK_ERROR_DEVICE_LOST</c> surfaces as <see cref="DeviceLostException"/>.
    /// A submission still deferred in an open batch is not signaled.</remarks>
    public bool IsSignaled {
        get {
            if (!m_pending) {
                return true;
            }
            if (m_deferred is not null) {
                return false;
            }
            if (m_alias is { } alias) {
                return ((alias.m_generation != m_aliasGeneration) || alias.Signaled());
            }

            return Signaled();
        }
    }

    // Whether this fence's own VkFence is signaled.
    private bool Signaled() {
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
    /// <summary>Refuses a fence with a submission outstanding: the caller must have drained it (<see cref="Wait"/>).</summary>
    internal void RequireDrained() {
        if (m_pending) {
            throw new InvalidOperationException(message: "A submission is already outstanding on this fence; Wait before re-arming it.");
        }
    }
    /// <summary>Marks a submission deferred to an open batch.</summary>
    internal void Defer(VulkanSubmissionBatch batch) {
        RequireDrained();
        m_pending = true;
        m_deferred = batch;
    }
    /// <summary>Marks a submission outstanding and hands the native fence handle to the submit; the caller must have
    /// drained any prior submission first (<see cref="Wait"/>), or the submission is the one its batch deferred.</summary>
    internal nint Arm() {
        if (m_deferred is null) {
            RequireDrained();
        }

        m_pending = true;
        m_deferred = null;

        return m_fenceHandle;
    }
    /// <summary>Answers a deferred submission from the fence its batch armed.</summary>
    internal void Alias(VulkanGpuSubmissionFence owner) {
        m_deferred = null;
        m_alias = owner;
        m_aliasGeneration = owner.m_generation;
    }

    /// <inheritdoc/>
    /// <remarks>Destroys the fence WITHOUT waiting (the frame-ring owner drains the device before teardown, and a
    /// lost device has nothing left to wait on).</remarks>
    public void Dispose() {
        m_frameSynchronizationApi.DestroyFence(
            device: m_device,
            fenceHandle: m_fenceHandle
        );
        m_fenceHandle = 0;
        m_pending = false;
        m_deferred = null;
        m_alias = null;
        m_generation++;
    }
    /// <inheritdoc/>
    /// <remarks>A submission still deferred in an open batch flushes it first.</remarks>
    public void Wait() {
        if (!m_pending) {
            return;
        }
        m_deferred?.Flush(device: m_device);
        if (m_alias is { } alias) {
            // The owner resets its fence only once it has drained it, so a moved generation means the batch retired.
            if (alias.m_generation == m_aliasGeneration) {
                m_frameSynchronizationApi.WaitForFence(
                    device: m_device,
                    fenceHandle: alias.m_fenceHandle,
                    timeout: ulong.MaxValue
                ).ThrowIfFailed(device: m_device, operation: "vkWaitForFences");
            }
            m_alias = null;
            m_pending = false;

            return;
        }

        // Unbounded, like vkDeviceWaitIdle — a hung GPU surfaces as a device loss (ThrowIfFailed maps
        // VK_ERROR_DEVICE_LOST to the neutral DeviceLostException the host pump's recovery catches).
        m_frameSynchronizationApi.WaitForFence(
            device: m_device,
            fenceHandle: m_fenceHandle,
            timeout: ulong.MaxValue
        ).ThrowIfFailed(device: m_device, operation: "vkWaitForFences");
        m_frameSynchronizationApi.ResetFence(
            device: m_device,
            fenceHandle: m_fenceHandle
        ).ThrowIfFailed(device: m_device, operation: "vkResetFences");
        m_pending = false;
        m_generation++;
    }
}
