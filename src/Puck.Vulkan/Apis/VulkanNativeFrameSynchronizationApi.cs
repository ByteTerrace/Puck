using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanFrameSynchronizationApi"/>, marshaling to the fence and
/// semaphore entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeFrameSynchronizationApi : IVulkanFrameSynchronizationApi {
    private const uint FenceCreateSignaledBit = 0x00000001;
    private const uint StructureTypeFenceCreateInfo = 8;
    private const uint StructureTypeSemaphoreCreateInfo = 9;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    /// <inheritdoc/>
    public VkResult CreateFence(VulkanFrameSynchronizationCreateRequest request, out nint fenceHandle) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        var createFence = GetPointers(deviceHandle: request.DeviceHandle).CreateFence;
        var createInfo = new VkFenceCreateInfo {
            Flags = (request.StartSignaled
                ? FenceCreateSignaledBit
                : 0),
            SType = StructureTypeFenceCreateInfo,
        };

        return createFence(
            request.DeviceHandle,
            in createInfo,
            0,
            out fenceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult CreateSemaphore(VulkanFrameSynchronizationCreateRequest request, out nint semaphoreHandle) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        var createSemaphore = GetPointers(deviceHandle: request.DeviceHandle).CreateSemaphore;
        var createInfo = new VkSemaphoreCreateInfo { SType = StructureTypeSemaphoreCreateInfo };

        return createSemaphore(
            request.DeviceHandle,
            in createInfo,
            0,
            out semaphoreHandle
        );
    }
    /// <inheritdoc/>
    public void DestroyFence(nint deviceHandle, nint fenceHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == fenceHandle)
        ) {
            return;
        }

        var destroyFence = GetPointers(deviceHandle: deviceHandle).DestroyFence;

        destroyFence(
            deviceHandle,
            fenceHandle,
            0
        );
    }
    /// <inheritdoc/>
    public void DestroySemaphore(nint deviceHandle, nint semaphoreHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == semaphoreHandle)
        ) {
            return;
        }

        var destroySemaphore = GetPointers(deviceHandle: deviceHandle).DestroySemaphore;

        destroySemaphore(
            deviceHandle,
            semaphoreHandle,
            0
        );
    }
    /// <inheritdoc/>
    public VkResult ResetFence(nint deviceHandle, nint fenceHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == fenceHandle) {
            throw new ArgumentException(
                message: "Vulkan fence handle must be non-zero.",
                paramName: nameof(fenceHandle)
            );
        }

        var resetFences = GetPointers(deviceHandle: deviceHandle).ResetFences;

        return resetFences(
            deviceHandle,
            1,
            in fenceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult WaitForFence(nint deviceHandle, nint fenceHandle, ulong timeout) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == fenceHandle) {
            throw new ArgumentException(
                message: "Vulkan fence handle must be non-zero.",
                paramName: nameof(fenceHandle)
            );
        }

        var waitForFences = GetPointers(deviceHandle: deviceHandle).WaitForFences;

        return waitForFences(
            deviceHandle,
            1,
            in fenceHandle,
            1,
            timeout
        );
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkFenceCreateInfo, nint, out nint, VkResult> CreateFence;
        public delegate* unmanaged[Cdecl]<nint, in VkSemaphoreCreateInfo, nint, out nint, VkResult> CreateSemaphore;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyFence;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroySemaphore;
        public delegate* unmanaged[Cdecl]<nint, uint, in nint, VkResult> ResetFences;
        public delegate* unmanaged[Cdecl]<nint, uint, in nint, uint, ulong, VkResult> WaitForFences;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();

    private unsafe DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkCreateFence"u8) {
            pNew.CreateFence = (delegate* unmanaged[Cdecl]<nint, in VkFenceCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCreateSemaphore"u8) {
            pNew.CreateSemaphore = (delegate* unmanaged[Cdecl]<nint, in VkSemaphoreCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyFence"u8) {
            pNew.DestroyFence = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroySemaphore"u8) {
            pNew.DestroySemaphore = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkResetFences"u8) {
            pNew.ResetFences = (delegate* unmanaged[Cdecl]<nint, uint, in nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkWaitForFences"u8) {
            pNew.WaitForFences = (delegate* unmanaged[Cdecl]<nint, uint, in nint, uint, ulong, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is not null) {
                return m_getDeviceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");

            m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getDeviceProcAddr;
        }
    }
}
