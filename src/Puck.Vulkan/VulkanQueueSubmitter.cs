using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>Submits Vulkan command buffers to a graphics queue.</summary>
public sealed unsafe class VulkanQueueSubmitter {
    private const uint StructureTypeSubmitInfo = 4;

    private readonly Dictionary<nint, DevicePointers> m_pointers = [];
    private readonly Lock m_syncRoot = new();
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    private struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, uint, in VkSubmitInfo, nint, VkResult> QueueSubmit;
        public delegate* unmanaged[Cdecl]<nint, VkResult> QueueWaitIdle;
    }

    /// <summary>Submits command buffers without a fence or an idle wait.</summary>
    /// <param name="deviceHandle">The logical device that owns the queue.</param>
    /// <param name="graphicsQueue">The queue that receives the submission.</param>
    /// <param name="commandBufferHandles">The native command-buffer handles to submit.</param>
    public void Submit(nint deviceHandle, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles) {
        if (commandBufferHandles.IsEmpty) {
            return;
        }

        SubmitCore(
            commandBufferHandles: commandBufferHandles,
            fenceHandle: 0,
            graphicsQueue: graphicsQueue,
            pointers: GetPointers(deviceHandle: deviceHandle)
        );
    }
    /// <summary>Submits command buffers and signals a fence when execution completes.</summary>
    /// <param name="deviceHandle">The logical device that owns the queue and fence.</param>
    /// <param name="graphicsQueue">The queue that receives the submission.</param>
    /// <param name="commandBufferHandles">The native command-buffer handles to submit.</param>
    /// <param name="fenceHandle">The native fence to signal.</param>
    public void Submit(nint deviceHandle, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles, nint fenceHandle) {
        if (commandBufferHandles.IsEmpty) {
            return;
        }

        SubmitCore(
            commandBufferHandles: commandBufferHandles,
            fenceHandle: fenceHandle,
            graphicsQueue: graphicsQueue,
            pointers: GetPointers(deviceHandle: deviceHandle)
        );
    }
    /// <summary>Submits command buffers and waits for the queue to become idle.</summary>
    /// <param name="deviceHandle">The logical device that owns the queue.</param>
    /// <param name="graphicsQueue">The queue that receives the submission.</param>
    /// <param name="commandBufferHandles">The native command-buffer handles to submit.</param>
    public void SubmitAndWait(nint deviceHandle, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles) {
        if (commandBufferHandles.IsEmpty) {
            return;
        }

        var pointers = GetPointers(deviceHandle: deviceHandle);

        SubmitCore(
            commandBufferHandles: commandBufferHandles,
            fenceHandle: 0,
            graphicsQueue: graphicsQueue,
            pointers: pointers
        );

        pointers.QueueWaitIdle(graphicsQueue.Handle).ThrowIfFailed(operation: "vkQueueWaitIdle");
    }

    private static void SubmitCore(DevicePointers pointers, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles, nint fenceHandle) {
        fixed (nint* commandBuffersPointer = commandBufferHandles) {
            var submitInfo = new VkSubmitInfo {
                CommandBufferCount = (uint)commandBufferHandles.Length,
                PCommandBuffers = (nint)commandBuffersPointer,
                SType = StructureTypeSubmitInfo,
            };

            pointers.QueueSubmit(
                graphicsQueue.Handle,
                1,
                in submitInfo,
                fenceHandle
            ).ThrowIfFailed(operation: "vkQueueSubmit");
        }
    }
    private DevicePointers GetPointers(nint deviceHandle) {
        lock (m_syncRoot) {
            if (m_pointers.TryGetValue(
                key: deviceHandle,
                value: out var existing
            )) {
                return existing;
            }

            var getAddr = GetDeviceProcAddr();
            DevicePointers pointers = default;

            fixed (byte* name = "vkQueueSubmit"u8) {
                pointers.QueueSubmit = (delegate* unmanaged[Cdecl]<nint, uint, in VkSubmitInfo, nint, VkResult>)getAddr(
                    deviceHandle,
                    name
                );
            }

            fixed (byte* name = "vkQueueWaitIdle"u8) {
                pointers.QueueWaitIdle = (delegate* unmanaged[Cdecl]<nint, VkResult>)getAddr(
                    deviceHandle,
                    name
                );
            }

            m_pointers[deviceHandle] = pointers;

            return pointers;
        }
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        if (m_getDeviceProcAddr is not null) {
            return m_getDeviceProcAddr;
        }

        m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");

        return m_getDeviceProcAddr;
    }
}
