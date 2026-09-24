using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>Submits Vulkan command buffers to a graphics queue.</summary>
public sealed unsafe class VulkanQueueSubmitter {
    private const uint StructureTypeSubmitInfo = 4;

    private static void SubmitCore(VulkanDeviceCommands device, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles, nint fenceHandle) {
        fixed (nint* commandBuffersPointer = commandBufferHandles) {
            var submitInfo = new VkSubmitInfo {
                CommandBufferCount = ((uint)commandBufferHandles.Length),
                PCommandBuffers = ((nint)commandBuffersPointer),
                SType = StructureTypeSubmitInfo,
            };

            device.QueueSubmit(
                graphicsQueue.Handle,
                1,
                in submitInfo,
                fenceHandle
            ).ThrowIfFailed(operation: "vkQueueSubmit");
        }
    }

    /// <summary>Submits command buffers without a fence or an idle wait.</summary>
    /// <param name="device">The command table of the logical device that owns the queue.</param>
    /// <param name="graphicsQueue">The queue that receives the submission.</param>
    /// <param name="commandBufferHandles">The native command-buffer handles to submit.</param>
    public void Submit(VulkanDeviceCommands device, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles) {
        if (commandBufferHandles.IsEmpty) {
            return;
        }

        SubmitCore(
            commandBufferHandles: commandBufferHandles,
            device: device,
            fenceHandle: 0,
            graphicsQueue: graphicsQueue
        );
    }
    /// <summary>Submits command buffers and signals a fence when execution completes.</summary>
    /// <param name="device">The command table of the logical device that owns the queue and fence.</param>
    /// <param name="graphicsQueue">The queue that receives the submission.</param>
    /// <param name="commandBufferHandles">The native command-buffer handles to submit.</param>
    /// <param name="fenceHandle">The native fence to signal.</param>
    public void Submit(VulkanDeviceCommands device, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles, nint fenceHandle) {
        if (commandBufferHandles.IsEmpty) {
            return;
        }

        SubmitCore(
            commandBufferHandles: commandBufferHandles,
            device: device,
            fenceHandle: fenceHandle,
            graphicsQueue: graphicsQueue
        );
    }
    /// <summary>Submits command buffers and waits for the queue to become idle.</summary>
    /// <param name="device">The command table of the logical device that owns the queue.</param>
    /// <param name="graphicsQueue">The queue that receives the submission.</param>
    /// <param name="commandBufferHandles">The native command-buffer handles to submit.</param>
    public void SubmitAndWait(VulkanDeviceCommands device, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles) {
        if (commandBufferHandles.IsEmpty) {
            return;
        }

        SubmitCore(
            commandBufferHandles: commandBufferHandles,
            device: device,
            fenceHandle: 0,
            graphicsQueue: graphicsQueue
        );
        device.QueueWaitIdle(graphicsQueue.Handle).ThrowIfFailed(operation: "vkQueueWaitIdle");
    }
}
