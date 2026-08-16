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

    /// <inheritdoc/>
    public VulkanFrameReadbackBuffer CreateBuffer(VulkanFrameReadbackBufferCreateRequest request) {
        VulkanArgument.RequireHandle(
            handle: request.DeviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(request)
        );
        VulkanArgument.RequireHandle(
            handle: request.InstanceHandle,
            handleDescription: "instance",
            paramName: nameof(request)
        );
        VulkanArgument.RequireHandle(
            handle: request.PhysicalDeviceHandle,
            handleDescription: "physical-device",
            paramName: nameof(request)
        );

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
            memoryHandle = VulkanNativeBufferSupport.AllocateAndBindMemory(
                allocateMemory: allocateMemory,
                bindBufferMemory: bindBufferMemory,
                bufferHandle: bufferHandle,
                deviceHandle: request.DeviceHandle,
                freeMemory: freeMemory,
                getBufferMemoryRequirements: getRequirements,
                getPhysicalDeviceMemoryProperties: getMemoryProperties,
                physicalDeviceHandle: request.PhysicalDeviceHandle,
                preferredProperties: HostVisibleMemoryPropertyBit | HostCoherentMemoryPropertyBit,
                requireProperties: true,
                resourceDescription: "a frame-readback buffer"
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
        var pixelData = new byte[((int)buffer.SizeBytes)];
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
                AllocateMemory = ((delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkAllocateMemory"u8)),
                BindBufferMemory = ((delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkBindBufferMemory"u8)),
                CreateBuffer = ((delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCreateBuffer"u8)),
                DestroyBuffer = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroyBuffer"u8)),
                FreeMemory = ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkFreeMemory"u8)),
                GetBufferMemoryRequirements = ((delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkGetBufferMemoryRequirements"u8)),
                MapMemory = ((delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkMapMemory"u8)),
                UnmapMemory = ((delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkUnmapMemory"u8)),
            }
        );
    }
    private InstancePointers GetInstancePointers(nint instanceHandle) {
        return m_instancePointers.GetOrAdd(
            key: instanceHandle,
            valueFactory: static handle => new InstancePointers {
                GetPhysicalDeviceMemoryProperties = ((delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void>)VulkanProcResolver.ResolveInstanceProc(functionName: "vkGetPhysicalDeviceMemoryProperties"u8, instanceHandle: handle)),
            }
        );
    }
}
