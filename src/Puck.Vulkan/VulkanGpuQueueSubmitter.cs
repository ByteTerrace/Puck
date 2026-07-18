using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuQueueSubmitter"/> by forwarding to <see cref="VulkanQueueSubmitter"/>, resolving the
/// graphics queue from the device context by downcasting to <see cref="IVulkanDeviceContext"/>. Submission fences
/// are plain <c>VkFence</c>s created through the frame-synchronization API.
/// </summary>
public sealed class VulkanGpuQueueSubmitter(VulkanQueueSubmitter queueSubmitter, IVulkanFrameSynchronizationApi frameSynchronizationApi) : IGpuQueueSubmitter {
    /// <inheritdoc/>
    public void Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) {
        var vkContext = (IVulkanDeviceContext)deviceContext;

        queueSubmitter.Submit(
            commandBufferHandles: commandBufferHandles,
            deviceHandle: vkContext.LogicalDevice.Handle,
            graphicsQueue: vkContext.LogicalDevice.GraphicsQueue
        );
    }
    /// <inheritdoc/>
    public void Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        var vkContext = (IVulkanDeviceContext)deviceContext;
        var vkFence = (VulkanGpuSubmissionFence)fence;

        queueSubmitter.Submit(
            commandBufferHandles: commandBufferHandles,
            deviceHandle: vkContext.LogicalDevice.Handle,
            fenceHandle: vkFence.Arm(),
            graphicsQueue: vkContext.LogicalDevice.GraphicsQueue
        );
    }
    /// <inheritdoc/>
    public void SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) {
        var vkContext = (IVulkanDeviceContext)deviceContext;

        queueSubmitter.SubmitAndWait(
            commandBufferHandles: commandBufferHandles,
            deviceHandle: vkContext.LogicalDevice.Handle,
            graphicsQueue: vkContext.LogicalDevice.GraphicsQueue
        );
    }
    /// <inheritdoc/>
    public IGpuSubmissionFence CreateSubmissionFence(IGpuDeviceContext deviceContext) {
        var vkContext = (IVulkanDeviceContext)deviceContext;

        return new VulkanGpuSubmissionFence(
            deviceHandle: vkContext.LogicalDevice.Handle,
            frameSynchronizationApi: frameSynchronizationApi
        );
    }
}

/// <summary>
/// The Vulkan <see cref="IGpuSubmissionFence"/>: one unsignaled <c>VkFence</c>, armed by a fenced queue submit and
/// drained by an unbounded <c>vkWaitForFences</c> + reset. Single-threaded like every other pump-thread GPU object.
/// </summary>
file sealed class VulkanGpuSubmissionFence : IGpuSubmissionFence {
    private readonly nint m_deviceHandle;
    private readonly IVulkanFrameSynchronizationApi m_frameSynchronizationApi;
    private nint m_fenceHandle;
    private bool m_pending;

    internal VulkanGpuSubmissionFence(nint deviceHandle, IVulkanFrameSynchronizationApi frameSynchronizationApi) {
        m_deviceHandle = deviceHandle;
        m_frameSynchronizationApi = frameSynchronizationApi;
        m_frameSynchronizationApi.CreateFence(
            fenceHandle: out m_fenceHandle,
            request: new VulkanFrameSynchronizationCreateRequest(DeviceHandle: deviceHandle, StartSignaled: false)
        ).ThrowIfFailed(operation: "vkCreateFence");
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
    public void Wait() {
        if (!m_pending) {
            return;
        }

        // Unbounded, like vkDeviceWaitIdle — a hung GPU surfaces as a device loss (ThrowIfFailed maps
        // VK_ERROR_DEVICE_LOST to the neutral DeviceLostException the host pump's recovery catches).
        m_frameSynchronizationApi.WaitForFence(
            deviceHandle: m_deviceHandle,
            fenceHandle: m_fenceHandle,
            timeout: ulong.MaxValue
        ).ThrowIfFailed(operation: "vkWaitForFences");
        m_frameSynchronizationApi.ResetFence(
            deviceHandle: m_deviceHandle,
            fenceHandle: m_fenceHandle
        ).ThrowIfFailed(operation: "vkResetFences");
        m_pending = false;
    }

    /// <inheritdoc/>
    /// <remarks>Destroys the fence WITHOUT waiting (the frame-ring owner drains the device before teardown, and a
    /// lost device has nothing left to wait on).</remarks>
    public void Dispose() {
        if (0 != m_fenceHandle) {
            m_frameSynchronizationApi.DestroyFence(deviceHandle: m_deviceHandle, fenceHandle: m_fenceHandle);
            m_fenceHandle = 0;
            m_pending = false;
        }
    }
}
