using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuQueueSubmitter"/> by forwarding to <see cref="VulkanQueueSubmitter"/>, resolving the
/// graphics queue from the device context by downcasting to <see cref="IVulkanDeviceContext"/>. Submission fences
/// are plain <c>VkFence</c>s created through the frame-synchronization API. External waits join the next submission's
/// wait list as timeline semaphores (<see cref="VulkanSharedFence"/>) with their values, at every stage.
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

    // A submission that reached the queue carried the list; an empty one did not, so it keeps the list.
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
                message: $"A Vulkan submission waits only on an imported timeline semaphore, not a {wait.Fence?.GetType().Name ?? "null"}.",
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
    public void Submit(ReadOnlySpan<nint> commandBufferHandles) {
        var vkContext = deviceContext;

        queueSubmitter.Submit(
            commandBufferHandles: commandBufferHandles,
            device: vkContext.LogicalDevice.Commands,
            graphicsQueue: vkContext.LogicalDevice.GraphicsQueue,
            waitSemaphores: WaitSemaphores,
            waitValues: WaitValues
        );
        ClearWaitsAfter(commandBufferHandles: commandBufferHandles);
    }
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        var vkContext = deviceContext;
        var vkFence = ((VulkanGpuSubmissionFence)fence);

        queueSubmitter.Submit(
            commandBufferHandles: commandBufferHandles,
            device: vkContext.LogicalDevice.Commands,
            fenceHandle: vkFence.Arm(),
            graphicsQueue: vkContext.LogicalDevice.GraphicsQueue,
            waitSemaphores: WaitSemaphores,
            waitValues: WaitValues
        );
        ClearWaitsAfter(commandBufferHandles: commandBufferHandles);
    }
    /// <inheritdoc/>
    public void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        var vkContext = deviceContext;

        queueSubmitter.SubmitAndWait(
            commandBufferHandles: commandBufferHandles,
            device: vkContext.LogicalDevice.Commands,
            graphicsQueue: vkContext.LogicalDevice.GraphicsQueue,
            waitSemaphores: WaitSemaphores,
            waitValues: WaitValues
        );
        ClearWaitsAfter(commandBufferHandles: commandBufferHandles);
    }
}

/// <summary>
/// The Vulkan <see cref="IGpuSubmissionFence"/>: one unsignaled <c>VkFence</c>, armed by a fenced queue submit and
/// drained by an unbounded <c>vkWaitForFences</c> + reset. Single-threaded like every other pump-thread GPU object.
/// </summary>
file sealed class VulkanGpuSubmissionFence : IGpuSubmissionFence {
    private readonly VulkanDeviceCommands m_device;
    private readonly IVulkanFrameSynchronizationApi m_frameSynchronizationApi;

    private nint m_fenceHandle;
    private bool m_pending;

    internal VulkanGpuSubmissionFence(VulkanDeviceCommands device, IVulkanFrameSynchronizationApi frameSynchronizationApi) {
        m_device = device;
        m_frameSynchronizationApi = frameSynchronizationApi;
        m_frameSynchronizationApi.CreateFence(
            fenceHandle: out m_fenceHandle,
            request: new VulkanFrameSynchronizationCreateRequest(
                Device: device,
                StartSignaled: false
            )
        ).ThrowIfFailed(operation: "vkCreateFence");
    }

    /// <inheritdoc/>
    /// <remarks>Reads <c>vkGetFenceStatus</c>; <c>VK_ERROR_DEVICE_LOST</c> surfaces as <see cref="DeviceLostException"/>.</remarks>
    public bool IsSignaled {
        get {
            if (!m_pending) {
                return true;
            }

            var status = m_frameSynchronizationApi.GetFenceStatus(
                device: m_device,
                fenceHandle: m_fenceHandle
            );

            if (status == VkResult.NotReady) {
                return false;
            }

            status.ThrowIfFailed(operation: "vkGetFenceStatus");

            return true;
        }
    }

    /// <summary>Marks a submission outstanding and hands the native fence handle to the submit; the caller must have
    /// drained any prior submission first (<see cref="Wait"/>).</summary>
    internal nint Arm() {
        if (m_pending) {
            throw new InvalidOperationException(message: "A submission is already outstanding on this fence; Wait before re-arming it.");
        }

        m_pending = true;

        return m_fenceHandle;
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
    }
    /// <inheritdoc/>
    public void Wait() {
        if (!m_pending) {
            return;
        }

        // Unbounded, like vkDeviceWaitIdle — a hung GPU surfaces as a device loss (ThrowIfFailed maps
        // VK_ERROR_DEVICE_LOST to the neutral DeviceLostException the host pump's recovery catches).
        m_frameSynchronizationApi.WaitForFence(
            device: m_device,
            fenceHandle: m_fenceHandle,
            timeout: ulong.MaxValue
        ).ThrowIfFailed(operation: "vkWaitForFences");
        m_frameSynchronizationApi.ResetFence(
            device: m_device,
            fenceHandle: m_fenceHandle
        ).ThrowIfFailed(operation: "vkResetFences");
        m_pending = false;
    }
}
