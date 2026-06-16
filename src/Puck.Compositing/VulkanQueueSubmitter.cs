using Puck.Vulkan;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interop;

namespace Puck.Compositing;

public sealed unsafe class VulkanQueueSubmitter {
    private const uint StructureTypeSubmitInfo = 4;

    private readonly Dictionary<nint, DevicePointers> m_pointers = [];
    private readonly Lock m_syncRoot = new();
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    private struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, uint, in VkSubmitInfo, nint, VkResult> QueueSubmit;
        public delegate* unmanaged[Cdecl]<nint, VkResult> QueueWaitIdle;
    }

    public void SubmitAndWait(nint deviceHandle, VkQueue graphicsQueue, ReadOnlySpan<nint> commandBufferHandles) {
        if (commandBufferHandles.IsEmpty) {
            return;
        }

        var pointers = GetPointers(deviceHandle: deviceHandle);

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
                0
            ).ThrowIfFailed(operation: "vkQueueSubmit");
        }

        pointers.QueueWaitIdle(graphicsQueue.Handle).ThrowIfFailed(operation: "vkQueueWaitIdle");
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
