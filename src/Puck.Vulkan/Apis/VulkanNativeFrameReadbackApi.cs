using System.Runtime.InteropServices;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanFrameReadbackApi"/>, marshaling to the buffer, memory,
/// and mapping entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeFrameReadbackApi : IVulkanFrameReadbackApi {
    private const uint BufferUsageTransferDestinationBit = 0x00000002;
    private const uint HostCoherentMemoryPropertyBit = 0x00000004;
    private const uint HostVisibleMemoryPropertyBit = 0x00000002;
    private const uint SharingModeExclusive = 0;
    private const uint StructureTypeBufferCreateInfo = 12;
    private const uint StructureTypeMemoryAllocateInfo = 5;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;

    /// <inheritdoc/>
    public VulkanFrameReadbackBuffer CreateBuffer(VulkanFrameReadbackBufferCreateRequest request) {
        if (0 == request.DeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.InstanceHandle) {
            throw new ArgumentException(
                message: "Vulkan instance handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.PhysicalDeviceHandle) {
            throw new ArgumentException(
                message: "Vulkan physical-device handle must be non-zero.",
                paramName: nameof(request)
            );
        }

        if (0 == request.SizeBytes) {
            throw new ArgumentOutOfRangeException(
                actualValue: request.SizeBytes,
                message: "Vulkan readback buffer size must be non-zero.",
                paramName: nameof(request)
            );
        }

        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
        var createBuffer = pointers.CreateBuffer;
        var allocateMemory = pointers.AllocateMemory;
        var bindBufferMemory = pointers.BindBufferMemory;
        var freeMemory = pointers.FreeMemory;
        var destroyBuffer = pointers.DestroyBuffer;
        var getRequirements = pointers.GetBufferMemoryRequirements;
        var getMemoryProperties = GetInstancePointers(instanceHandle: request.InstanceHandle).GetPhysicalDeviceMemoryProperties;

        var bufferHandle = nint.Zero;
        var memoryHandle = nint.Zero;

        try {
            bufferHandle = CreateBufferHandle(
                createBuffer: createBuffer,
                deviceHandle: request.DeviceHandle,
                sizeBytes: request.SizeBytes
            );
            memoryHandle = AllocateAndBindMemory(
                allocateMemory: allocateMemory,
                bindBufferMemory: bindBufferMemory,
                bufferHandle: bufferHandle,
                freeMemory: freeMemory,
                getMemoryProperties: getMemoryProperties,
                getRequirements: getRequirements,
                request: request
            );
            return new VulkanFrameReadbackBuffer(
                bufferHandle: bufferHandle,
                deviceHandle: request.DeviceHandle,
                frameReadbackApi: this,
                memoryHandle: memoryHandle,
                sizeBytes: request.SizeBytes
            );
        } catch {
            if (0 != memoryHandle) {
                freeMemory(
                    request.DeviceHandle,
                    memoryHandle,
                    0
                );
            }

            if (0 != bufferHandle) {
                destroyBuffer(
                    request.DeviceHandle,
                    bufferHandle,
                    0
                );
            }

            throw;
        }
    }
    /// <inheritdoc/>
    public byte[] ReadBuffer(VulkanFrameReadbackBuffer buffer) {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.SizeBytes > int.MaxValue) {
            throw new InvalidOperationException(message: "Vulkan frame readback buffer is too large for a managed byte array.");
        }

        var mapMemory = GetPointers(deviceHandle: buffer.DeviceHandle).MapMemory;
        var unmapMemory = GetPointers(deviceHandle: buffer.DeviceHandle).UnmapMemory;
        var pixelData = new byte[(int)buffer.SizeBytes];
        var mapResult = mapMemory(
            buffer.DeviceHandle,
            buffer.MemoryHandle,
            0,
            checked((nuint)buffer.SizeBytes),
            0,
            out var mappedMemory
        );

        mapResult.ThrowIfFailed(operation: "vkMapMemory");
        try {
            Marshal.Copy(
                destination: pixelData,
                length: pixelData.Length,
                source: mappedMemory,
                startIndex: 0
            );
            return pixelData;
        } finally {
            unmapMemory(
                buffer.DeviceHandle,
                buffer.MemoryHandle
            );
        }
    }
    /// <inheritdoc/>
    public void DestroyBuffer(VulkanFrameReadbackBufferDestroyRequest request) {
        if (0 == request.DeviceHandle) {
            return;
        }

        if (0 != request.MemoryHandle) {
            var freeMemory = GetPointers(deviceHandle: request.DeviceHandle).FreeMemory;

            freeMemory(
                request.DeviceHandle,
                request.MemoryHandle,
                0
            );
        }

        if (0 != request.BufferHandle) {
            var destroyBuffer = GetPointers(deviceHandle: request.DeviceHandle).DestroyBuffer;

            destroyBuffer(
                request.DeviceHandle,
                request.BufferHandle,
                0
            );
        }
    }

    private static unsafe nint CreateBufferHandle(delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult> createBuffer, nint deviceHandle, ulong sizeBytes) {
        var createInfo = new VkBufferCreateInfo {
            SType = StructureTypeBufferCreateInfo,
            SharingMode = SharingModeExclusive,
            Size = sizeBytes,
            Usage = BufferUsageTransferDestinationBit,
        };
        var result = createBuffer(
            deviceHandle,
            in createInfo,
            0,
            out var bufferHandle
        );

        result.ThrowIfFailed(operation: "vkCreateBuffer");
        if (0 == bufferHandle) {
            throw new InvalidOperationException(message: "vkCreateBuffer returned success without a valid readback buffer handle.");
        }

        return bufferHandle;
    }
    private static unsafe nint AllocateAndBindMemory(
        delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult> allocateMemory,
        delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult> bindBufferMemory,
        delegate* unmanaged[Cdecl]<nint, nint, nint, void> freeMemory,
        delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void> getRequirements,
        delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void> getMemoryProperties,
        VulkanFrameReadbackBufferCreateRequest request,
        nint bufferHandle
    ) {
        getRequirements(
            request.DeviceHandle,
            bufferHandle,
            out var memoryRequirements
        );
        getMemoryProperties(
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

        throw new InvalidOperationException(message: "The Vulkan physical device did not report a compatible host-visible frame-readback memory type.");
    }
}
