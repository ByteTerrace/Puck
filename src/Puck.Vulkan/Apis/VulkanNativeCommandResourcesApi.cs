using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanCommandResourcesApi"/>, marshaling to the command-pool
/// and command-buffer allocation entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeCommandResourcesApi : IVulkanCommandResourcesApi {
    // VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT: lets callers re-record individual
    // command buffers one at a time, since vkBeginCommandBuffer may implicitly reset a
    // single buffer only when its pool carries this flag.
    private const uint CommandPoolCreateResetCommandBufferFlag = 0x00000002;
    private const uint PrimaryCommandBufferLevel = 0;
    private const uint StructureTypeCommandBufferAllocateInfo = 40;
    private const uint StructureTypeCommandPoolCreateInfo = 39;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    /// <inheritdoc/>
    public VkResult AllocateCommandBuffers(VulkanCommandBufferAllocateRequest request, nint buffer, uint commandBufferCount) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        var allocateCommandBuffers = GetPointers(deviceHandle: request.DeviceHandle).AllocateCommandBuffers;
        var allocateInfo = new VkCommandBufferAllocateInfo {
            CommandBufferCount = commandBufferCount,
            CommandPool = request.CommandPoolHandle,
            Level = PrimaryCommandBufferLevel,
            SType = StructureTypeCommandBufferAllocateInfo,
        };

        return allocateCommandBuffers(
            request.DeviceHandle,
            in allocateInfo,
            buffer
        );
    }
    /// <inheritdoc/>
    public VkResult CreateCommandPool(VulkanCommandPoolCreateRequest request, out nint commandPoolHandle) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        var createCommandPool = GetPointers(deviceHandle: request.DeviceHandle).CreateCommandPool;
        var createInfo = new VkCommandPoolCreateInfo {
            Flags = CommandPoolCreateResetCommandBufferFlag,
            QueueFamilyIndex = request.QueueFamilyIndex,
            SType = StructureTypeCommandPoolCreateInfo,
        };

        return createCommandPool(
            request.DeviceHandle,
            in createInfo,
            0,
            out commandPoolHandle
        );
    }
    /// <inheritdoc/>
    public void DestroyCommandPool(nint deviceHandle, nint commandPoolHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == commandPoolHandle)
        ) {
            return;
        }

        var destroyCommandPool = GetPointers(deviceHandle: deviceHandle).DestroyCommandPool;

        destroyCommandPool(
            deviceHandle,
            commandPoolHandle,
            0
        );
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkCommandBufferAllocateInfo, nint, VkResult> AllocateCommandBuffers;
        public delegate* unmanaged[Cdecl]<nint, in VkCommandPoolCreateInfo, nint, out nint, VkResult> CreateCommandPool;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyCommandPool;
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

        fixed (byte* pName = "vkAllocateCommandBuffers"u8) {
            pNew.AllocateCommandBuffers = (delegate* unmanaged[Cdecl]<nint, in VkCommandBufferAllocateInfo, nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCreateCommandPool"u8) {
            pNew.CreateCommandPool = (delegate* unmanaged[Cdecl]<nint, in VkCommandPoolCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyCommandPool"u8) {
            pNew.DestroyCommandPool = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
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
