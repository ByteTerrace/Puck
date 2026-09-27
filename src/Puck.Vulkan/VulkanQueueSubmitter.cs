using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>Submits Vulkan command buffers to a graphics queue.</summary>
public sealed unsafe class VulkanQueueSubmitter {
    // VK_PIPELINE_STAGE_ALL_COMMANDS_BIT: an external wait holds every stage of the batch, as Direct3D 12's queue wait
    // holds the whole submission.
    private const uint PipelineStageAllCommands = 0x00010000;
    private const uint StructureTypeSubmitInfo = 4;

    private static void SubmitCore(VulkanDeviceCommands device, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles, nint fenceHandle, ReadOnlySpan<nint> waitSemaphores, ReadOnlySpan<ulong> waitValues, ReadOnlySpan<nint> signalSemaphores = default, ReadOnlySpan<ulong> signalValues = default) {
        if (waitSemaphores.Length != waitValues.Length) {
            throw new ArgumentException(
                message: $"{waitSemaphores.Length} wait semaphores need as many values, not {waitValues.Length}.",
                paramName: nameof(waitValues)
            );
        }
        if (signalSemaphores.Length != signalValues.Length) {
            throw new ArgumentException(
                message: $"{signalSemaphores.Length} signal semaphores need as many values, not {signalValues.Length}.",
                paramName: nameof(signalValues)
            );
        }

        var waitStages = stackalloc uint[waitSemaphores.Length];

        for (var index = 0; (index < waitSemaphores.Length); index++) {
            waitStages[index] = PipelineStageAllCommands;
        }

        fixed (nint* commandBuffersPointer = commandBufferHandles)
        fixed (nint* waitSemaphoresPointer = waitSemaphores)
        fixed (ulong* waitValuesPointer = waitValues)
        fixed (nint* signalSemaphoresPointer = signalSemaphores)
        fixed (ulong* signalValuesPointer = signalValues) {
            var timelineInfo = new VkTimelineSemaphoreSubmitInfo {
                PSignalSemaphoreValues = ((nint)signalValuesPointer),
                PWaitSemaphoreValues = ((nint)waitValuesPointer),
                SType = VkTimelineSemaphoreSubmitInfo.StructureType,
                SignalSemaphoreValueCount = ((uint)signalValues.Length),
                WaitSemaphoreValueCount = ((uint)waitValues.Length),
            };
            var submitInfo = new VkSubmitInfo {
                CommandBufferCount = ((uint)commandBufferHandles.Length),
                PCommandBuffers = ((nint)commandBuffersPointer),
                PNext = ((waitSemaphores.IsEmpty && signalSemaphores.IsEmpty)
                    ? 0
                    : ((nint)(&timelineInfo))),
                PSignalSemaphores = ((nint)signalSemaphoresPointer),
                PWaitDstStageMask = ((nint)waitStages),
                PWaitSemaphores = ((nint)waitSemaphoresPointer),
                SType = StructureTypeSubmitInfo,
                SignalSemaphoreCount = ((uint)signalSemaphores.Length),
                WaitSemaphoreCount = ((uint)waitSemaphores.Length),
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
    /// <param name="waitSemaphores">The timeline semaphores the batch waits on before any of its stages run, or empty.</param>
    /// <param name="waitValues">The value each of <paramref name="waitSemaphores"/> must reach, in the same order.</param>
    /// <exception cref="ArgumentException"><paramref name="waitSemaphores"/> and <paramref name="waitValues"/> differ in
    /// length.</exception>
    public void Submit(VulkanDeviceCommands device, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles, ReadOnlySpan<nint> waitSemaphores = default, ReadOnlySpan<ulong> waitValues = default) {
        if (commandBufferHandles.IsEmpty) {
            return;
        }

        SubmitCore(
            commandBufferHandles: commandBufferHandles,
            device: device,
            fenceHandle: 0,
            graphicsQueue: graphicsQueue,
            waitSemaphores: waitSemaphores,
            waitValues: waitValues
        );
    }
    /// <summary>Submits command buffers and signals a fence when execution completes.</summary>
    /// <param name="device">The command table of the logical device that owns the queue and fence.</param>
    /// <param name="graphicsQueue">The queue that receives the submission.</param>
    /// <param name="commandBufferHandles">The native command-buffer handles to submit.</param>
    /// <param name="fenceHandle">The native fence to signal.</param>
    /// <param name="waitSemaphores">The timeline semaphores the batch waits on before any of its stages run, or empty.</param>
    /// <param name="waitValues">The value each of <paramref name="waitSemaphores"/> must reach, in the same order.</param>
    /// <exception cref="ArgumentException"><paramref name="waitSemaphores"/> and <paramref name="waitValues"/> differ in
    /// length.</exception>
    public void Submit(VulkanDeviceCommands device, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles, nint fenceHandle, ReadOnlySpan<nint> waitSemaphores = default, ReadOnlySpan<ulong> waitValues = default) {
        if (commandBufferHandles.IsEmpty) {
            return;
        }

        SubmitCore(
            commandBufferHandles: commandBufferHandles,
            device: device,
            fenceHandle: fenceHandle,
            graphicsQueue: graphicsQueue,
            waitSemaphores: waitSemaphores,
            waitValues: waitValues
        );
    }
    /// <summary>Queues a signal of a timeline semaphore behind every submission made to the queue before it: a batch with
    /// no command buffers that sets the semaphore to the value once the queue reaches it. Nothing waits.</summary>
    /// <param name="device">The command table of the logical device that owns the queue and semaphore.</param>
    /// <param name="graphicsQueue">The queue that signals.</param>
    /// <param name="semaphore">The native timeline <c>VkSemaphore</c>.</param>
    /// <param name="value">The value the semaphore is set to; greater than every value it was set to before.</param>
    public void Signal(VulkanDeviceCommands device, VkQueue graphicsQueue, nint semaphore, ulong value) {
        Span<nint> semaphores = [semaphore];
        Span<ulong> values = [value];

        SubmitCore(
            commandBufferHandles: [],
            device: device,
            fenceHandle: 0,
            graphicsQueue: graphicsQueue,
            signalSemaphores: semaphores,
            signalValues: values,
            waitSemaphores: [],
            waitValues: []
        );
    }
    /// <summary>Submits command buffers and waits for the queue to become idle.</summary>
    /// <param name="device">The command table of the logical device that owns the queue.</param>
    /// <param name="graphicsQueue">The queue that receives the submission.</param>
    /// <param name="commandBufferHandles">The native command-buffer handles to submit.</param>
    /// <param name="waitSemaphores">The timeline semaphores the batch waits on before any of its stages run, or empty.</param>
    /// <param name="waitValues">The value each of <paramref name="waitSemaphores"/> must reach, in the same order.</param>
    /// <exception cref="ArgumentException"><paramref name="waitSemaphores"/> and <paramref name="waitValues"/> differ in
    /// length.</exception>
    public void SubmitAndWait(VulkanDeviceCommands device, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles, ReadOnlySpan<nint> waitSemaphores = default, ReadOnlySpan<ulong> waitValues = default) {
        if (commandBufferHandles.IsEmpty) {
            return;
        }

        SubmitCore(
            commandBufferHandles: commandBufferHandles,
            device: device,
            fenceHandle: 0,
            graphicsQueue: graphicsQueue,
            waitSemaphores: waitSemaphores,
            waitValues: waitValues
        );
        device.QueueWaitIdle(graphicsQueue.Handle).ThrowIfFailed(operation: "vkQueueWaitIdle");
    }
}
