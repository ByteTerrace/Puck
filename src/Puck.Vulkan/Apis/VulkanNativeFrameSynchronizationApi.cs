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

    /// <inheritdoc/>
    public VkResult CreateFence(VulkanFrameSynchronizationCreateRequest request, out nint fenceHandle) {
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );

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
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );

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
        VulkanArgument.RequireHandle(
            handle: deviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(deviceHandle)
        );

        VulkanArgument.RequireHandle(
            handle: fenceHandle,
            handleDescription: "fence",
            paramName: nameof(fenceHandle)
        );

        var resetFences = GetPointers(deviceHandle: deviceHandle).ResetFences;

        return resetFences(
            deviceHandle,
            1,
            in fenceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult WaitForFence(nint deviceHandle, nint fenceHandle, ulong timeout) {
        VulkanArgument.RequireHandle(
            handle: deviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(deviceHandle)
        );

        VulkanArgument.RequireHandle(
            handle: fenceHandle,
            handleDescription: "fence",
            paramName: nameof(fenceHandle)
        );

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

    private DevicePointers GetPointers(nint deviceHandle) {
        return m_pointers.GetOrAdd(
            key: deviceHandle,
            valueFactory: static handle => new DevicePointers {
                CreateFence = (delegate* unmanaged[Cdecl]<nint, in VkFenceCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCreateFence"u8),
                CreateSemaphore = (delegate* unmanaged[Cdecl]<nint, in VkSemaphoreCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCreateSemaphore"u8),
                DestroyFence = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroyFence"u8),
                DestroySemaphore = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroySemaphore"u8),
                ResetFences = (delegate* unmanaged[Cdecl]<nint, uint, in nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkResetFences"u8),
                WaitForFences = (delegate* unmanaged[Cdecl]<nint, uint, in nint, uint, ulong, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkWaitForFences"u8),
            }
        );
    }
}
