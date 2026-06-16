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
    private const uint StructureTypeMemoryAllocateInfo = 5;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;

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
            memoryHandle = AllocateAndBindMemory(
                allocateMemory: allocateMemory,
                bindBufferMemory: bindBufferMemory,
                bufferHandle: bufferHandle,
                freeMemory: freeMemory,
                getBufferMemoryRequirements: getBufferMemoryRequirements,
                getPhysicalDeviceMemoryProperties: getPhysicalDeviceMemoryProperties,
                request: request
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
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.BufferHandle) {
            throw new ArgumentException(
                message: "Vulkan vertex-buffer handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.MemoryHandle) {
            throw new ArgumentException(
                message: "Vulkan device-memory handle must be non-zero.",
                paramName: nameof(request)
            );
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

    private unsafe DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkCreateBuffer"u8) {
            pNew.CreateBuffer = (delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyBuffer"u8) {
            pNew.DestroyBuffer = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetBufferMemoryRequirements"u8) {
            pNew.GetBufferMemoryRequirements = (delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkAllocateMemory"u8) {
            pNew.AllocateMemory = (delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkFreeMemory"u8) {
            pNew.FreeMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkBindBufferMemory"u8) {
            pNew.BindBufferMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkMapMemory"u8) {
            pNew.MapMemory = (delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkUnmapMemory"u8) {
            pNew.UnmapMemory = (delegate* unmanaged[Cdecl]<nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private unsafe InstancePointers GetInstancePointers(nint instanceHandle) {
        if (m_instancePointers.TryGetValue(
            key: instanceHandle,
            value: out var pointers
        )) {
            return pointers;
        }

        var getAddr = GetInstanceProcAddr();
        InstancePointers pNew = default;

        fixed (byte* pName = "vkGetPhysicalDeviceMemoryProperties"u8) {
            pNew.GetPhysicalDeviceMemoryProperties = (delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void>)getAddr(
                instanceHandle,
                pName
            );
        }

        m_instancePointers[instanceHandle] = pNew;
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
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetInstanceProcAddr() {
        lock (m_syncRoot) {
            if (m_getInstanceProcAddr is not null) {
                return m_getInstanceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr");

            m_getInstanceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getInstanceProcAddr;
        }
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
    private static unsafe nint AllocateAndBindMemory(
        delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult> allocateMemory,
        delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult> bindBufferMemory,
        delegate* unmanaged[Cdecl]<nint, nint, nint, void> freeMemory,
        delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void> getBufferMemoryRequirements,
        delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void> getPhysicalDeviceMemoryProperties,
        VulkanVertexBufferCreateRequest request,
        nint bufferHandle
    ) {
        getBufferMemoryRequirements(
            request.DeviceHandle,
            bufferHandle,
            out var memoryRequirements
        );
        getPhysicalDeviceMemoryProperties(
            request.PhysicalDeviceHandle,
            out var memoryProperties
        );
        var memoryTypeIndex = FindMemoryTypeIndex(
            memoryProperties: memoryProperties,
            memoryTypeBits: memoryRequirements.MemoryTypeBits,
            requiredProperties: HostVisibleMemoryPropertyBit | HostCoherentMemoryPropertyBit
        );
        var allocateInfo = new VkMemoryAllocateInfo {
            AllocationSize = memoryRequirements.Size,
            MemoryTypeIndex = memoryTypeIndex,
            SType = StructureTypeMemoryAllocateInfo,
        };
        var result = allocateMemory(
            request.DeviceHandle,
            in allocateInfo,
            0,
            out var memoryHandle
        );

        result.ThrowIfFailed(operation: "vkAllocateMemory");

        try {
            bindBufferMemory(
                request.DeviceHandle,
                bufferHandle,
                memoryHandle,
                0
            ).ThrowIfFailed(operation: "vkBindBufferMemory");
            return memoryHandle;
        } catch {
            freeMemory(
                request.DeviceHandle,
                memoryHandle,
                0
            );
            throw;
        }
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
    private static unsafe uint FindMemoryTypeIndex(uint memoryTypeBits, uint requiredProperties, VkPhysicalDeviceMemoryProperties memoryProperties) {
        for (var index = 0; (index < memoryProperties.MemoryTypeCount); index++) {
            var supported = (0 != (memoryTypeBits & (1u << index)));
            var hasProperties = ((memoryProperties.MemoryTypePropertyFlags(memoryTypeIndex: index) & requiredProperties) == requiredProperties);

            if (
                supported &&
                hasProperties
            ) {
                return (uint)index;
            }
        }

        throw new InvalidOperationException(message: "The Vulkan physical device did not report a compatible host-visible vertex-buffer memory type.");
    }
}
