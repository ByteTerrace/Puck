using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanVertexBufferApi"/>, marshaling to the buffer and memory
/// entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeVertexBufferApi : IVulkanVertexBufferApi {
    private const uint BufferUsageVertexBufferBit = 0x00000080;
    private const uint HostCoherentMemoryPropertyBit = 0x00000004;
    private const uint HostVisibleMemoryPropertyBit = 0x00000002;

    /// <inheritdoc/>
    public VulkanVertexBufferCreateResult CreateVertexBuffer(VulkanVertexBufferCreateRequest request, byte[] vertexData) {
        ValidateCreateRequest(
            request: request,
            vertexData: vertexData
        );

        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
        var createBuffer = pointers.CreateBuffer;
        var destroyBuffer = pointers.DestroyBuffer;
        var getBufferMemoryRequirements = pointers.GetBufferMemoryRequirements;
        var allocateMemory = pointers.AllocateMemory;
        var freeMemory = pointers.FreeMemory;
        var bindBufferMemory = pointers.BindBufferMemory;
        var mapMemory = pointers.MapMemory;
        var unmapMemory = pointers.UnmapMemory;
        var getPhysicalDeviceMemoryProperties = GetInstancePointers(instanceHandle: request.InstanceHandle).GetPhysicalDeviceMemoryProperties;

        nint bufferHandle = 0;
        nint memoryHandle = 0;

        try {
            bufferHandle = CreateBuffer(
                createBuffer: createBuffer,
                deviceHandle: request.DeviceHandle,
                size: checked((ulong)vertexData.Length)
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
                preferredProperties: HostVisibleMemoryPropertyBit | HostCoherentMemoryPropertyBit,
                requireProperties: true,
                resourceDescription: "a vertex buffer"
            );
            UploadBufferData(
                data: vertexData,
                deviceHandle: request.DeviceHandle,
                mapMemory: mapMemory,
                memoryHandle: memoryHandle,
                unmapMemory: unmapMemory
            );
            return new VulkanVertexBufferCreateResult(
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
    public void DestroyVertexBuffer(VulkanVertexBufferDestroyRequest request) {
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.BufferHandle,
            handleDescription: "vertex-buffer",
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.MemoryHandle,
            handleDescription: "device-memory",
            paramName: nameof(request)
        );

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
    private static unsafe void ValidateCreateRequest(VulkanVertexBufferCreateRequest request, byte[] vertexData) {
        VulkanNativeBufferSupport.ValidateBufferHandles(
            argumentName: nameof(request),
            deviceHandle: request.DeviceHandle,
            instanceHandle: request.InstanceHandle,
            physicalDeviceHandle: request.PhysicalDeviceHandle
        );
        if (0 == vertexData.Length) {
            throw new ArgumentOutOfRangeException(
                actualValue: vertexData.Length,
                message: "Vertex-buffer data must be non-empty.",
                paramName: nameof(vertexData)
            );
        }
    }
    private static unsafe nint CreateBuffer(delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult> createBuffer, nint deviceHandle, ulong size) {
        return VulkanNativeBufferSupport.CreateBuffer(
            createBuffer: createBuffer,
            deviceHandle: deviceHandle,
            size: size,
            usage: BufferUsageVertexBufferBit
        );
    }
    private static unsafe void UploadBufferData(
        delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, out nint, VkResult> mapMemory,
        delegate* unmanaged[Cdecl]<nint, nint, void> unmapMemory,
        nint deviceHandle,
        nint memoryHandle,
        byte[] data
    ) {
        VulkanNativeBufferSupport.UploadBufferData(
            data: data,
            deviceHandle: deviceHandle,
            mapMemory: mapMemory,
            memoryHandle: memoryHandle,
            unmapMemory: unmapMemory
        );
    }
}
