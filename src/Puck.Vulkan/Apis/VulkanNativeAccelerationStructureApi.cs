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
            var memoryTypeIndex = VulkanNativeBufferSupport.FindMemoryTypeIndex(
                memoryProperties: in memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits,
                preferredProperties: (request.HostVisible
                    ? HostVisibleMemoryPropertyBit | HostCoherentMemoryPropertyBit
                    : MemoryPropertyDeviceLocalBit),
                requireProperties: request.HostVisible,
                resourceDescription: "acceleration-structure buffers"
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
        return m_pointers.GetOrAdd(
            key: deviceHandle,
            valueFactory: static handle => {
                // vkGetBufferDeviceAddress is core 1.2; devices below it expose only the extension alias.
                var getBufferDeviceAddress = VulkanProcResolver.ResolveOptionalDeviceProc(deviceHandle: handle, functionName: "vkGetBufferDeviceAddress"u8);

                if (0 == getBufferDeviceAddress) {
                    getBufferDeviceAddress = VulkanProcResolver.ResolveOptionalDeviceProc(deviceHandle: handle, functionName: "vkGetBufferDeviceAddressKHR"u8);
                }

                // The acceleration-structure entry points are optional: SupportsDevice probes them on
                // possibly-unsupported devices, where they resolve to null. The core buffer/memory commands are
                // required. GetBufferDeviceAddress carries whichever of its two names resolved above.
                return new DevicePointers {
                    CreateAccelerationStructure = (delegate* unmanaged[Cdecl]<nint, in VkAccelerationStructureCreateInfoKhr, nint, out nint, VkResult>)VulkanProcResolver.ResolveOptionalDeviceProc(deviceHandle: handle, functionName: "vkCreateAccelerationStructureKHR"u8),
                    DestroyAccelerationStructure = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveOptionalDeviceProc(deviceHandle: handle, functionName: "vkDestroyAccelerationStructureKHR"u8),
                    GetAccelerationStructureBuildSizes = (delegate* unmanaged[Cdecl]<nint, uint, nint, nint, nint, void>)VulkanProcResolver.ResolveOptionalDeviceProc(deviceHandle: handle, functionName: "vkGetAccelerationStructureBuildSizesKHR"u8),
                    GetAccelerationStructureDeviceAddress = (delegate* unmanaged[Cdecl]<nint, in VkAccelerationStructureDeviceAddressInfoKhr, ulong>)VulkanProcResolver.ResolveOptionalDeviceProc(deviceHandle: handle, functionName: "vkGetAccelerationStructureDeviceAddressKHR"u8),
                    CmdBuildAccelerationStructures = (delegate* unmanaged[Cdecl]<nint, uint, nint, nint, void>)VulkanProcResolver.ResolveOptionalDeviceProc(deviceHandle: handle, functionName: "vkCmdBuildAccelerationStructuresKHR"u8),
                    GetBufferDeviceAddress = (delegate* unmanaged[Cdecl]<nint, in VkBufferDeviceAddressInfo, ulong>)getBufferDeviceAddress,
                    CmdPipelineBarrier = (delegate* unmanaged[Cdecl]<nint, uint, uint, uint, uint, nint, uint, nint, uint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCmdPipelineBarrier"u8),
                    CreateBuffer = (delegate* unmanaged[Cdecl]<nint, in VkBufferCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCreateBuffer"u8),
                    DestroyBuffer = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroyBuffer"u8),
                    GetBufferMemoryRequirements = (delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkGetBufferMemoryRequirements"u8),
                    AllocateMemory = (delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkAllocateMemory"u8),
                    FreeMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkFreeMemory"u8),
                    BindBufferMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkBindBufferMemory"u8),
                    MapMemory = (delegate* unmanaged[Cdecl]<nint, nint, ulong, nuint, uint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkMapMemory"u8),
                    UnmapMemory = (delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkUnmapMemory"u8),
                };
            }
        );
    }
    private InstancePointers GetInstancePointers(nint instanceHandle) {
        return m_instancePointers.GetOrAdd(
            key: instanceHandle,
            valueFactory: static handle => new InstancePointers {
                GetPhysicalDeviceMemoryProperties = (delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void>)VulkanProcResolver.ResolveInstanceProc(instanceHandle: handle, functionName: "vkGetPhysicalDeviceMemoryProperties"u8),
                GetPhysicalDeviceProperties2 = (delegate* unmanaged[Cdecl]<nint, nint, void>)VulkanProcResolver.ResolveOptionalInstanceProc(instanceHandle: handle, functionName: "vkGetPhysicalDeviceProperties2"u8),
            }
        );
    }
}
