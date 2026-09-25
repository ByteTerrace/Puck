using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuQueueSubmitter"/> by forwarding to <see cref="VulkanQueueSubmitter"/>, resolving the
/// graphics queue from the device context by downcasting to <see cref="IVulkanDeviceContext"/>. Submission fences
/// are plain <c>VkFence</c>s created through the frame-synchronization API.
/// </summary>
public sealed class VulkanGpuQueueSubmitter(IVulkanDeviceContext deviceContext, VulkanQueueSubmitter queueSubmitter, IVulkanFrameSynchronizationApi frameSynchronizationApi) : IGpuQueueSubmitter {
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
            graphicsQueue: vkContext.LogicalDevice.GraphicsQueue
        );
    }
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        var vkContext = deviceContext;
        var vkFence = ((VulkanGpuSubmissionFence)fence);

        queueSubmitter.Submit(
            commandBufferHandles: commandBufferHandles,
            device: vkContext.LogicalDevice.Commands,
            fenceHandle: vkFence.Arm(),
            graphicsQueue: vkContext.LogicalDevice.GraphicsQueue
        );
    }
    /// <inheritdoc/>
    public void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        var vkContext = deviceContext;

        queueSubmitter.SubmitAndWait(
            commandBufferHandles: commandBufferHandles,
            device: vkContext.LogicalDevice.Commands,
            graphicsQueue: vkContext.LogicalDevice.GraphicsQueue
        );
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
