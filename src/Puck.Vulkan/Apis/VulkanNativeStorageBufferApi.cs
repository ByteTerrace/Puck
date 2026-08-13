using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanStorageBufferApi"/>, marshaling to the buffer, memory,
/// and mapping entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeStorageBufferApi : IVulkanStorageBufferApi {
    private const uint BufferUsageStorageBufferBit = 0x00000020;
    // Also a transfer source so a host-visible storage buffer can stage uploads (e.g. CPU pixels into an image).
    private const uint BufferUsageTransferSourceBit = 0x00000001;
    // Lets the buffer back vkCmdDispatchIndirect / vkCmdDrawIndirect (the GPU reads the group/draw counts from it).
    private const uint BufferUsageIndirectBufferBit = 0x00000100;
    private const uint DeviceLocalMemoryPropertyBit = 0x00000001;
    private const uint HostCoherentMemoryPropertyBit = 0x00000004;
    private const uint HostVisibleMemoryPropertyBit = 0x00000002;

    /// <inheritdoc/>
    public VulkanStorageBufferCreateResult CreateStorageBuffer(VulkanStorageBufferCreateRequest request) {
        ValidateCreateRequest(request: request);

        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
        var createBuffer = pointers.CreateBuffer;
        var destroyBuffer = pointers.DestroyBuffer;
        var getBufferMemoryRequirements = pointers.GetBufferMemoryRequirements;
        var allocateMemory = pointers.AllocateMemory;
        var freeMemory = pointers.FreeMemory;
        var bindBufferMemory = pointers.BindBufferMemory;
        var getPhysicalDeviceMemoryProperties = GetInstancePointers(instanceHandle: request.InstanceHandle).GetPhysicalDeviceMemoryProperties;

        nint bufferHandle = 0;
        nint memoryHandle = 0;

        try {
            bufferHandle = CreateBuffer(
                createBuffer: createBuffer,
                deviceHandle: request.DeviceHandle,
                extraUsage: (request.IndirectArgs ? BufferUsageIndirectBufferBit : 0u),
                size: request.SizeBytes
            );
            memoryHandle = VulkanNativeBufferSupport.AllocateAndBindMemory(
                allocateMemory: allocateMemory,
                bindBufferMemory: bindBufferMemory,
                bufferHandle: bufferHandle,
                deviceHandle: request.DeviceHandle,
                freeMemory: freeMemory,
                getBufferMemoryRequirements: getBufferMemoryRequirements,
                getPhysicalDeviceMemoryProperties: getPhysicalDeviceMemoryProperties,
                physicalDeviceHandle: request.PhysicalDeviceHandle,
                // Device-local memory is GPU-only (never host-mapped — the buffer's Map/Write is never called for it);
                // the default host-visible+coherent memory backs buffers the CPU writes.
                preferredProperties: (request.DeviceLocal
                    ? DeviceLocalMemoryPropertyBit
                    : HostVisibleMemoryPropertyBit | HostCoherentMemoryPropertyBit),
                requireProperties: true,
                resourceDescription: "a storage buffer"
            );
            return new VulkanStorageBufferCreateResult(
                BufferHandle: bufferHandle,
                MemoryHandle: memoryHandle
            );
        } catch {
            if (0 != bufferHandle) {
                destroyBuffer(
                    request.DeviceHandle,
                    bufferHandle,
                    0
                );
            }

            if (0 != memoryHandle) {
                freeMemory(
                    request.DeviceHandle,
                    memoryHandle,
                    0
                );
            }

            throw;
        }
    }
    /// <inheritdoc/>
    public void DestroyStorageBuffer(VulkanStorageBufferDestroyRequest request) {
        if (
            (0 == request.DeviceHandle) ||
            (0 == request.BufferHandle) ||
            (0 == request.MemoryHandle)
        ) {
            return;
        }

        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
        var destroyBuffer = pointers.DestroyBuffer;
        var freeMemory = pointers.FreeMemory;

        destroyBuffer(
            request.DeviceHandle,
            request.BufferHandle,
            0
        );
        freeMemory(
            request.DeviceHandle,
            request.MemoryHandle,
            0
        );
    }
    /// <inheritdoc/>
    public nint MapMemory(nint deviceHandle, nint memoryHandle, ulong size) {
        var mapMemory = GetPointers(deviceHandle: deviceHandle).MapMemory;
        var result = mapMemory(
            deviceHandle,
            memoryHandle,
            0,
            (nuint)size,
            0,
            out var dataPointer
        );

        result.ThrowIfFailed(operation: "vkMapMemory");
        return dataPointer;
    }
    /// <inheritdoc/>
    public void UnmapMemory(nint deviceHandle, nint memoryHandle) {
        var unmapMemory = GetPointers(deviceHandle: deviceHandle).UnmapMemory;

        unmapMemory(
            deviceHandle,
            memoryHandle
        );
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult> CreateBuffer;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyBuffer;
        public delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void> GetBufferMemoryRequirements;
        public delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult> AllocateMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> FreeMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult> BindBufferMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, out nint, VkResult> MapMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, void> UnmapMemory;
    }
    private unsafe struct InstancePointers {
        public delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void> GetPhysicalDeviceMemoryProperties;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, InstancePointers> m_instancePointers = new();

    private DevicePointers GetPointers(nint deviceHandle) {
        return m_pointers.GetOrAdd(
            key: deviceHandle,
            valueFactory: static handle => new DevicePointers {
                AllocateMemory = (delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkAllocateMemory"u8),
                BindBufferMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkBindBufferMemory"u8),
                CreateBuffer = (delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCreateBuffer"u8),
                DestroyBuffer = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroyBuffer"u8),
                FreeMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkFreeMemory"u8),
                GetBufferMemoryRequirements = (delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkGetBufferMemoryRequirements"u8),
                MapMemory = (delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkMapMemory"u8),
                UnmapMemory = (delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkUnmapMemory"u8),
            }
        );
    }
    private InstancePointers GetInstancePointers(nint instanceHandle) {
        return m_instancePointers.GetOrAdd(
            key: instanceHandle,
            valueFactory: static handle => new InstancePointers {
                GetPhysicalDeviceMemoryProperties = (delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void>)VulkanProcResolver.ResolveInstanceProc(instanceHandle: handle, functionName: "vkGetPhysicalDeviceMemoryProperties"u8),
            }
        );
    }
    private static unsafe void ValidateCreateRequest(VulkanStorageBufferCreateRequest request) {
        VulkanNativeBufferSupport.ValidateBufferHandles(
            argumentName: nameof(request),
            deviceHandle: request.DeviceHandle,
            instanceHandle: request.InstanceHandle,
            physicalDeviceHandle: request.PhysicalDeviceHandle
        );
        if (0 == request.SizeBytes) {
            throw new ArgumentOutOfRangeException(
                actualValue: request.SizeBytes,
                message: "Storage-buffer size must be greater than zero.",
                paramName: nameof(request)
            );
        }
    }
    private static unsafe nint CreateBuffer(delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult> createBuffer, nint deviceHandle, ulong size, uint extraUsage) {
        return VulkanNativeBufferSupport.CreateBuffer(
            createBuffer: createBuffer,
            deviceHandle: deviceHandle,
            size: size,
            usage: BufferUsageStorageBufferBit | BufferUsageTransferSourceBit | extraUsage
        );
    }
}
