using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuQueueSubmitter"/> by forwarding to <see cref="VulkanQueueSubmitter"/>, resolving the
/// graphics queue from the device context by downcasting to <see cref="IVulkanDeviceContext"/>.
/// </summary>
public sealed class VulkanGpuQueueSubmitter(VulkanQueueSubmitter queueSubmitter) : IGpuQueueSubmitter {
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
    public void SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) {
        var vkContext = (IVulkanDeviceContext)deviceContext;

        queueSubmitter.SubmitAndWait(
            commandBufferHandles: commandBufferHandles,
            deviceHandle: vkContext.LogicalDevice.Handle,
            graphicsQueue: vkContext.LogicalDevice.GraphicsQueue
        );
    }
}
