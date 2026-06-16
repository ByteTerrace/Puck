using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>Generic wrappers over the Vulkan acceleration-structure commands. Every buffer
/// it creates carries SHADER_DEVICE_ADDRESS usage, so the DEVICE_ADDRESS allocate flag is
/// always applied.</summary>
public unsafe sealed class VulkanNativeAccelerationStructureApi : IVulkanAccelerationStructureApi {
    // Values verified against the Vulkan SDK 1.4.350 header (vulkan_core.h).
    private const uint AccelerationStructureBuildTypeDevice = 1;
    private const uint BuildAccelerationStructureModeBuild = 0;
    private const uint HostCoherentMemoryPropertyBit = 0x00000004;
    private const uint HostVisibleMemoryPropertyBit = 0x00000002;
    private const uint MemoryAllocateDeviceAddressBit = 0x00000002;
    private const uint MemoryPropertyDeviceLocalBit = 0x00000001;
    private const uint SharingModeExclusive = 0;
    private const uint StructureTypeAccelerationStructureBuildGeometryInfoKhr = 1000150000;
    private const uint StructureTypeAccelerationStructureBuildSizesInfoKhr = 1000150020;
    private const uint StructureTypeAccelerationStructureCreateInfoKhr = 1000150017;
    private const uint StructureTypeAccelerationStructureDeviceAddressInfoKhr = 1000150002;
    private const uint StructureTypeBufferCreateInfo = 12;
    private const uint StructureTypeBufferDeviceAddressInfo = 1000244001;
    private const uint StructureTypeMemoryAllocateFlagsInfo = 1000060000;
    private const uint StructureTypeMemoryAllocateInfo = 5;
    private const uint StructureTypeMemoryBarrier = 46;
    private const uint StructureTypePhysicalDeviceAccelerationStructurePropertiesKhr = 1000150014;
    private const uint StructureTypePhysicalDeviceProperties2 = 1000059001;

    private readonly Lock m_syncRoot = new();
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;

    /// <inheritdoc/>
    public bool SupportsDevice(nint deviceHandle) {
        return (
            (0 != deviceHandle) &&
            (GetPointers(deviceHandle: deviceHandle).CreateAccelerationStructure is not null)
        );
    }
    /// <inheritdoc/>
    public uint QueryScratchAlignment(nint instanceHandle, nint physicalDeviceHandle) {
        var instancePointers = GetInstancePointers(instanceHandle: instanceHandle);

        if (instancePointers.GetPhysicalDeviceProperties2 is null) {
            return 256;
        }

        var accelerationProperties = new VkPhysicalDeviceAccelerationStructurePropertiesKhr {
            SType = StructureTypePhysicalDeviceAccelerationStructurePropertiesKhr,
        };
        var properties2 = new VkPhysicalDeviceProperties2 {
            PNext = (nint)(&accelerationProperties),
            SType = StructureTypePhysicalDeviceProperties2,
        };

        instancePointers.GetPhysicalDeviceProperties2(
            physicalDeviceHandle,
            (nint)(&properties2)
        );
        return Math.Max(
            val1: accelerationProperties.MinAccelerationStructureScratchOffsetAlignment,
            val2: 1u
        );
    }
    /// <inheritdoc/>
    public (nint BufferHandle, nint MemoryHandle) CreateBuffer(VulkanAccelerationBufferCreateRequest request) {
        var pointers = GetPointers(deviceHandle: request.DeviceHandle);
        var instancePointers = GetInstancePointers(instanceHandle: request.InstanceHandle);

        var createInfo = new VkBufferCreateInfo {
            SType = StructureTypeBufferCreateInfo,
            SharingMode = SharingModeExclusive,
            Size = request.SizeBytes,
            Usage = request.Usage,
        };

        pointers.CreateBuffer(
            request.DeviceHandle,
            in createInfo,
            0,
            out var bufferHandle
        ).ThrowIfFailed(operation: "vkCreateBuffer");

        try {
            pointers.GetBufferMemoryRequirements(
                request.DeviceHandle,
                bufferHandle,
                out var memoryRequirements
            );
            instancePointers.GetPhysicalDeviceMemoryProperties(
                request.PhysicalDeviceHandle,
                out var memoryProperties
            );
            var memoryTypeIndex = FindMemoryTypeIndex(
                memoryProperties: in memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits,
                preferredProperties: (request.HostVisible
                    ? HostVisibleMemoryPropertyBit | HostCoherentMemoryPropertyBit
                    : MemoryPropertyDeviceLocalBit),
                requireProperties: request.HostVisible
            );

            // SHADER_DEVICE_ADDRESS buffers require the DEVICE_ADDRESS allocate flag.
            var allocateFlags = new VkMemoryAllocateFlagsInfo {
                Flags = MemoryAllocateDeviceAddressBit,
                SType = StructureTypeMemoryAllocateFlagsInfo,
            };
            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
                PNext = (nint)(&allocateFlags),
                SType = StructureTypeMemoryAllocateInfo,
            };

            pointers.AllocateMemory(
                request.DeviceHandle,
                in allocateInfo,
                0,
                out var memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");

            try {
                pointers.BindBufferMemory(
                    request.DeviceHandle,
                    bufferHandle,
                    memoryHandle,
                    0
                ).ThrowIfFailed(operation: "vkBindBufferMemory");
                return (bufferHandle, memoryHandle);
            } catch {
                pointers.FreeMemory(
                    request.DeviceHandle,
                    memoryHandle,
                    0
                );
                throw;
            }
        } catch {
            pointers.DestroyBuffer(
                request.DeviceHandle,
                bufferHandle,
                0
            );
            throw;
        }
    }
    /// <inheritdoc/>
    public void DestroyBuffer(nint deviceHandle, nint bufferHandle, nint memoryHandle) {
        var pointers = GetPointers(deviceHandle: deviceHandle);

        if (0 != bufferHandle) {
            pointers.DestroyBuffer(
                deviceHandle,
                bufferHandle,
                0
            );
        }

        if (0 != memoryHandle) {
            pointers.FreeMemory(
                deviceHandle,
                memoryHandle,
                0
            );
        }
    }
    /// <inheritdoc/>
    public nint MapMemory(nint deviceHandle, nint memoryHandle, ulong sizeBytes) {
        GetPointers(deviceHandle: deviceHandle).MapMemory(
            deviceHandle,
            memoryHandle,
            0,
            (nuint)sizeBytes,
            0,
            out var mappedPointer
        ).ThrowIfFailed(operation: "vkMapMemory");
        return mappedPointer;
    }
    /// <inheritdoc/>
    public void UnmapMemory(nint deviceHandle, nint memoryHandle) {
        GetPointers(deviceHandle: deviceHandle).UnmapMemory(
            deviceHandle,
            memoryHandle
        );
    }
    /// <inheritdoc/>
    public ulong GetBufferDeviceAddress(nint deviceHandle, nint bufferHandle) {
        var addressInfo = new VkBufferDeviceAddressInfo {
            Buffer = bufferHandle,
            SType = StructureTypeBufferDeviceAddressInfo,
        };

        return GetPointers(deviceHandle: deviceHandle).GetBufferDeviceAddress(
            deviceHandle,
            in addressInfo
        );
    }
    /// <inheritdoc/>
    public nint CreateAccelerationStructure(nint deviceHandle, nint bufferHandle, ulong sizeBytes, uint accelerationStructureType) {
        var createInfo = new VkAccelerationStructureCreateInfoKhr {
            Buffer = bufferHandle,
            Offset = 0,
            SType = StructureTypeAccelerationStructureCreateInfoKhr,
            Size = sizeBytes,
            Type = accelerationStructureType,
        };

        GetPointers(deviceHandle: deviceHandle).CreateAccelerationStructure(
            deviceHandle,
            in createInfo,
            0,
            out var handle
        ).ThrowIfFailed(operation: "vkCreateAccelerationStructureKHR");
        return handle;
    }
    /// <inheritdoc/>
    public void DestroyAccelerationStructure(nint deviceHandle, nint accelerationStructureHandle) {
        if (0 == accelerationStructureHandle) {
            return;
        }

        var destroyAccelerationStructure = GetPointers(deviceHandle: deviceHandle).DestroyAccelerationStructure;

        if (destroyAccelerationStructure is not null) {
            destroyAccelerationStructure(
                deviceHandle,
                accelerationStructureHandle,
                0
            );
        }
    }
    /// <inheritdoc/>
    public ulong GetDeviceAddress(nint deviceHandle, nint accelerationStructureHandle) {
        var addressInfo = new VkAccelerationStructureDeviceAddressInfoKhr {
            AccelerationStructure = accelerationStructureHandle,
            SType = StructureTypeAccelerationStructureDeviceAddressInfoKhr,
        };

        return GetPointers(deviceHandle: deviceHandle).GetAccelerationStructureDeviceAddress(
            deviceHandle,
            in addressInfo
        );
    }
    /// <inheritdoc/>
    public VkAccelerationStructureBuildSizesInfoKhr GetBuildSizes<TGeometry>(
        nint deviceHandle,
        uint accelerationStructureType,
        uint buildFlags,
        in TGeometry geometry,
        uint maxPrimitiveCount
    ) where TGeometry : unmanaged {
        var pointers = GetPointers(deviceHandle: deviceHandle);

        fixed (TGeometry* geometryPointer = &geometry) {
            var buildInfo = new VkAccelerationStructureBuildGeometryInfoKhr {
                Flags = buildFlags,
                GeometryCount = 1,
                Mode = BuildAccelerationStructureModeBuild,
                PGeometries = (nint)geometryPointer,
                SType = StructureTypeAccelerationStructureBuildGeometryInfoKhr,
                Type = accelerationStructureType,
            };
            var sizes = new VkAccelerationStructureBuildSizesInfoKhr {
                SType = StructureTypeAccelerationStructureBuildSizesInfoKhr,
            };

            pointers.GetAccelerationStructureBuildSizes(
                deviceHandle,
                AccelerationStructureBuildTypeDevice,
                (nint)(&buildInfo),
                (nint)(&maxPrimitiveCount),
                (nint)(&sizes)
            );
            return sizes;
        }
    }
    /// <inheritdoc/>
    public void CmdBuildAccelerationStructure<TGeometry>(
        nint deviceHandle,
        nint commandBufferHandle,
        uint accelerationStructureType,
        uint buildFlags,
        nint destinationAccelerationStructure,
        ulong scratchDeviceAddress,
        in TGeometry geometry,
        uint primitiveCount
    ) where TGeometry : unmanaged {
        var pointers = GetPointers(deviceHandle: deviceHandle);

        fixed (TGeometry* geometryPointer = &geometry) {
            var buildInfo = new VkAccelerationStructureBuildGeometryInfoKhr {
                DstAccelerationStructure = destinationAccelerationStructure,
                Flags = buildFlags,
                GeometryCount = 1,
                Mode = BuildAccelerationStructureModeBuild,
                PGeometries = (nint)geometryPointer,
                SType = StructureTypeAccelerationStructureBuildGeometryInfoKhr,
                ScratchDataDeviceAddress = scratchDeviceAddress,
                Type = accelerationStructureType,
            };
            var range = new VkAccelerationStructureBuildRangeInfoKhr {
                PrimitiveCount = primitiveCount,
            };
            var rangePointer = &range;

            pointers.CmdBuildAccelerationStructures(
                commandBufferHandle,
                1,
                (nint)(&buildInfo),
                (nint)(&rangePointer)
            );
        }
    }
    /// <inheritdoc/>
    public void CmdMemoryBarrier(
        nint deviceHandle,
        nint commandBufferHandle,
        uint sourceAccessMask,
        uint destinationAccessMask,
        uint sourceStageMask,
        uint destinationStageMask
    ) {
        var barrier = new VkMemoryBarrier {
            DstAccessMask = destinationAccessMask,
            SType = StructureTypeMemoryBarrier,
            SrcAccessMask = sourceAccessMask,
        };

        GetPointers(deviceHandle: deviceHandle).CmdPipelineBarrier(
            commandBufferHandle,
            sourceStageMask,
            destinationStageMask,
            0,
            1,
            (nint)(&barrier),
            0,
            0,
            0,
            0
        );
    }

    private static uint FindMemoryTypeIndex(
        uint memoryTypeBits,
        uint preferredProperties,
        bool requireProperties,
        in VkPhysicalDeviceMemoryProperties memoryProperties
    ) {
        var fallbackIndex = -1;

        for (var index = 0; (index < memoryProperties.MemoryTypeCount); index++) {
            if (0 == (memoryTypeBits & (1u << index))) {
                continue;
            }

            if ((memoryProperties.MemoryTypePropertyFlags(memoryTypeIndex: index) & preferredProperties) == preferredProperties) {
                return (uint)index;
            }

            if (fallbackIndex < 0) {
                fallbackIndex = index;
            }
        }

        if (
            requireProperties ||
            (fallbackIndex < 0)
        ) {
            throw new InvalidOperationException(message: "The Vulkan physical device did not report a compatible memory type for acceleration-structure buffers.");
        }

        return (uint)fallbackIndex;
    }

    private struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkAccelerationStructureCreateInfoKhr, nint, out nint, VkResult> CreateAccelerationStructure;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyAccelerationStructure;
        public delegate* unmanaged[Cdecl]<nint, uint, nint, nint, nint, void> GetAccelerationStructureBuildSizes;
        public delegate* unmanaged[Cdecl]<nint, in VkAccelerationStructureDeviceAddressInfoKhr, ulong> GetAccelerationStructureDeviceAddress;
        public delegate* unmanaged[Cdecl]<nint, uint, nint, nint, void> CmdBuildAccelerationStructures;
        public delegate* unmanaged[Cdecl]<nint, in VkBufferDeviceAddressInfo, ulong> GetBufferDeviceAddress;
        public delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, nint, uint, nint, uint, nint, void> CmdPipelineBarrier;
        public delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult> CreateBuffer;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyBuffer;
        public delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void> GetBufferMemoryRequirements;
        public delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult> AllocateMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> FreeMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult> BindBufferMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, out nint, VkResult> MapMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, void> UnmapMemory;
    }
    private struct InstancePointers {
        public delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void> GetPhysicalDeviceMemoryProperties;
        public delegate* unmanaged[Cdecl]<nint, nint, void> GetPhysicalDeviceProperties2;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, InstancePointers> m_instancePointers = new();

    private DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkCreateAccelerationStructureKHR"u8) {
            pNew.CreateAccelerationStructure = (delegate* unmanaged[Cdecl]<nint, in VkAccelerationStructureCreateInfoKhr, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyAccelerationStructureKHR"u8) {
            pNew.DestroyAccelerationStructure = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetAccelerationStructureBuildSizesKHR"u8) {
            pNew.GetAccelerationStructureBuildSizes = (delegate* unmanaged[Cdecl]<nint, uint, nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetAccelerationStructureDeviceAddressKHR"u8) {
            pNew.GetAccelerationStructureDeviceAddress = (delegate* unmanaged[Cdecl]<nint, in VkAccelerationStructureDeviceAddressInfoKhr, ulong>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdBuildAccelerationStructuresKHR"u8) {
            pNew.CmdBuildAccelerationStructures = (delegate* unmanaged[Cdecl]<nint, uint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetBufferDeviceAddress"u8) {
            pNew.GetBufferDeviceAddress = (delegate* unmanaged[Cdecl]<nint, in VkBufferDeviceAddressInfo, ulong>)getAddr(
                deviceHandle,
                pName
            );
        }
        if (pNew.GetBufferDeviceAddress is null) {
            // Devices below core 1.2 expose only the extension alias.
            fixed (byte* pName = "vkGetBufferDeviceAddressKHR"u8) {
                pNew.GetBufferDeviceAddress = (delegate* unmanaged[Cdecl]<nint, in VkBufferDeviceAddressInfo, ulong>)getAddr(
                    deviceHandle,
                    pName
                );
            }
        }

        fixed (byte* pName = "vkCmdPipelineBarrier"u8) {
            pNew.CmdPipelineBarrier = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, nint, uint, nint, uint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
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
    private InstancePointers GetInstancePointers(nint instanceHandle) {
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
        fixed (byte* pName = "vkGetPhysicalDeviceProperties2"u8) {
            pNew.GetPhysicalDeviceProperties2 = (delegate* unmanaged[Cdecl]<nint, nint, void>)getAddr(
                instanceHandle,
                pName
            );
        }
        m_instancePointers[instanceHandle] = pNew;
        return pNew;
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is not null) {
                return m_getDeviceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");

            m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getDeviceProcAddr;
        }
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetInstanceProcAddr() {
        lock (m_syncRoot) {
            if (m_getInstanceProcAddr is not null) {
                return m_getInstanceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr");

            m_getInstanceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getInstanceProcAddr;
        }
    }
}
