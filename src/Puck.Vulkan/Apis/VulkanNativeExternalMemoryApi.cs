using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanExternalMemoryApi"/>: it creates a Vulkan image flagged for
/// external memory and either imports the shared NT handle's memory as a dedicated allocation (consuming a texture
/// another backend produced) or allocates fresh exportable memory and retrieves its shared handle (producing a
/// texture another Vulkan instance consumes) — both without a CPU round-trip.
/// </summary>
public unsafe sealed class VulkanNativeExternalMemoryApi : IVulkanExternalMemoryApi {
    // A Direct3D 12 committed resource shared via ID3D12Device::CreateSharedHandle yields an NT handle that
    // refers to the resource, so it imports through the D3D12-resource handle type — the one Vulkan defines for
    // exactly that handle. (The D3D11-texture type, 0x10, is for D3D11 textures and is rejected with
    // VK_ERROR_INITIALIZATION_FAILED for a D3D12 resource on the NVIDIA driver tested.)
    private const uint ExternalMemoryHandleTypeD3D12ResourceBit = 0x00000040;
    // The only handle type a Vulkan allocation can EXPORT; an opaque-Vulkan handle is importable by another Vulkan
    // instance, not by Direct3D 12 (which only opens handles to D3D-created resources).
    private const uint ExternalMemoryHandleTypeOpaqueWin32Bit = 0x00000002;
    private const uint GenericAll = 0x10000000;
    private const uint ImageTiling2dOptimal = 0;
    private const uint ImageType2d = 1;
    // The shared resource is a Direct3D 12 render target, so the imported image must allow color-attachment use
    // (matching the producer) in addition to being sampled by the consumer.
    private const uint ImageUsageColorAttachmentBit = 0x00000010;
    private const uint ImageUsageSampledBit = 0x00000004;
    private const uint ImageUsageTransferSourceBit = 0x00000001;
    private const uint MemoryPropertyDeviceLocalBit = 0x00000001;
    private const uint SampleCount1Bit = 1;
    private const uint SharingModeExclusive = 0;
    private const uint StructureTypeExportMemoryAllocateInfo = 1000072002;
    private const uint StructureTypeExportMemoryWin32HandleInfo = 1000073001;
    private const uint StructureTypeExternalMemoryImageCreateInfo = 1000072001;
    private const uint StructureTypeImageCreateInfo = 14;
    private const uint StructureTypeImportMemoryWin32HandleInfo = 1000073000;
    private const uint StructureTypeMemoryAllocateInfo = 5;
    private const uint StructureTypeMemoryDedicatedAllocateInfo = 1000127001;
    private const uint StructureTypeMemoryGetWin32HandleInfo = 1000073003;
    private const uint StructureTypeMemoryWin32HandleProperties = 1000073002;

    private readonly Lock m_syncRoot = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, InstancePointers> m_instancePointers = new();
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getInstanceProcAddr;

    private struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkImageCreateInfo, nint, out nint, VkResult> CreateImage;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyImage;
        public delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void> GetImageMemoryRequirements;
        public delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult> AllocateMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> FreeMemory;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult> BindImageMemory;
        public delegate* unmanaged[Cdecl]<nint, uint, nint, out VkMemoryWin32HandlePropertiesKHR, VkResult> GetMemoryWin32HandleProperties;
        public delegate* unmanaged[Cdecl]<nint, in VkMemoryGetWin32HandleInfoKHR, out nint, VkResult> GetMemoryWin32Handle;
    }
    private struct InstancePointers {
        public delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void> GetPhysicalDeviceMemoryProperties;
    }

    /// <inheritdoc/>
    public VulkanExternalImageImportResult ImportImage(VulkanExternalImageImportRequest request) {
        var pointers = GetPointers(deviceHandle: request.DeviceHandle);

        var externalInfo = new VkExternalMemoryImageCreateInfo {
            HandleTypes = ExternalMemoryHandleTypeD3D12ResourceBit,
            SType = StructureTypeExternalMemoryImageCreateInfo,
        };
        var imageInfo = new VkImageCreateInfo {
            ArrayLayers = 1,
            Extent = new VkExtent3D(
                width: request.Width,
                height: request.Height,
                depth: 1
            ),
            Format = request.Format,
            ImageType = ImageType2d,
            InitialLayout = 0,
            MipLevels = 1,
            PNext = (nint)(&externalInfo),
            SType = StructureTypeImageCreateInfo,
            Samples = SampleCount1Bit,
            SharingMode = SharingModeExclusive,
            Tiling = ImageTiling2dOptimal,
            // The default samples a foreign render target; a non-zero UsageFlags imports it as a writable image
            // instead (e.g. STORAGE, so Vulkan can produce a compute result INTO a Direct3D 12-owned resource).
            Usage = ((request.UsageFlags != 0) ? request.UsageFlags : (ImageUsageSampledBit | ImageUsageColorAttachmentBit)),
        };

        pointers.CreateImage(
            request.DeviceHandle,
            in imageInfo,
            0,
            out var imageHandle
        ).ThrowIfFailed(operation: "vkCreateImage");

        try {
            pointers.GetImageMemoryRequirements(
                request.DeviceHandle,
                imageHandle,
                out var memoryRequirements
            );

            var handleProperties = new VkMemoryWin32HandlePropertiesKHR {
                SType = StructureTypeMemoryWin32HandleProperties,
            };

            pointers.GetMemoryWin32HandleProperties(
                request.DeviceHandle,
                ExternalMemoryHandleTypeD3D12ResourceBit,
                request.SharedHandle,
                out handleProperties
            ).ThrowIfFailed(operation: "vkGetMemoryWin32HandlePropertiesKHR");

            GetInstancePointers(instanceHandle: request.InstanceHandle).GetPhysicalDeviceMemoryProperties(
                request.PhysicalDeviceHandle,
                out var memoryProperties
            );

            var memoryTypeIndex = FindMemoryTypeIndex(
                memoryProperties: memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits & handleProperties.MemoryTypeBits
            );
            var dedicatedInfo = new VkMemoryDedicatedAllocateInfo {
                Image = imageHandle,
                SType = StructureTypeMemoryDedicatedAllocateInfo,
            };
            var importInfo = new VkImportMemoryWin32HandleInfoKHR {
                Handle = request.SharedHandle,
                HandleType = ExternalMemoryHandleTypeD3D12ResourceBit,
                PNext = (nint)(&dedicatedInfo),
                SType = StructureTypeImportMemoryWin32HandleInfo,
            };
            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
                PNext = (nint)(&importInfo),
                SType = StructureTypeMemoryAllocateInfo,
            };

            pointers.AllocateMemory(
                request.DeviceHandle,
                in allocateInfo,
                0,
                out var memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");

            try {
                pointers.BindImageMemory(
                    request.DeviceHandle,
                    imageHandle,
                    memoryHandle,
                    0
                ).ThrowIfFailed(operation: "vkBindImageMemory");

                return new VulkanExternalImageImportResult(
                    ImageHandle: imageHandle,
                    MemoryHandle: memoryHandle
                );
            } catch {
                pointers.FreeMemory(
                    request.DeviceHandle,
                    memoryHandle,
                    0
                );

                throw;
            }
        } catch {
            pointers.DestroyImage(
                request.DeviceHandle,
                imageHandle,
                0
            );

            throw;
        }
    }
    /// <inheritdoc/>
    public VulkanExternalImageImportResult ImportOpaqueImage(VulkanExternalImageImportRequest request) {
        var pointers = GetPointers(deviceHandle: request.DeviceHandle);

        var externalInfo = new VkExternalMemoryImageCreateInfo {
            HandleTypes = ExternalMemoryHandleTypeOpaqueWin32Bit,
            SType = StructureTypeExternalMemoryImageCreateInfo,
        };
        var imageInfo = new VkImageCreateInfo {
            ArrayLayers = 1,
            Extent = new VkExtent3D(
                width: request.Width,
                height: request.Height,
                depth: 1
            ),
            Format = request.Format,
            ImageType = ImageType2d,
            InitialLayout = 0,
            MipLevels = 1,
            PNext = (nint)(&externalInfo),
            SType = StructureTypeImageCreateInfo,
            Samples = SampleCount1Bit,
            SharingMode = SharingModeExclusive,
            Tiling = ImageTiling2dOptimal,
            Usage = ImageUsageSampledBit | ImageUsageColorAttachmentBit | ImageUsageTransferSourceBit,
        };

        pointers.CreateImage(
            request.DeviceHandle,
            in imageInfo,
            0,
            out var imageHandle
        ).ThrowIfFailed(operation: "vkCreateImage");

        try {
            pointers.GetImageMemoryRequirements(
                request.DeviceHandle,
                imageHandle,
                out var memoryRequirements
            );

            GetInstancePointers(instanceHandle: request.InstanceHandle).GetPhysicalDeviceMemoryProperties(
                request.PhysicalDeviceHandle,
                out var memoryProperties
            );

            // An opaque Win32 handle came from a Vulkan allocation, so its compatible memory types are exactly the
            // image's requirements. vkGetMemoryWin32HandlePropertiesKHR must NOT be called for an opaque handle type
            // (the spec restricts it to foreign/non-opaque handles), unlike the Direct3D 12 import path.
            var memoryTypeIndex = FindMemoryTypeIndex(
                memoryProperties: memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits
            );
            var dedicatedInfo = new VkMemoryDedicatedAllocateInfo {
                Image = imageHandle,
                SType = StructureTypeMemoryDedicatedAllocateInfo,
            };
            var importInfo = new VkImportMemoryWin32HandleInfoKHR {
                Handle = request.SharedHandle,
                HandleType = ExternalMemoryHandleTypeOpaqueWin32Bit,
                PNext = (nint)(&dedicatedInfo),
                SType = StructureTypeImportMemoryWin32HandleInfo,
            };
            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
                PNext = (nint)(&importInfo),
                SType = StructureTypeMemoryAllocateInfo,
            };

            pointers.AllocateMemory(
                request.DeviceHandle,
                in allocateInfo,
                0,
                out var memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");

            try {
                pointers.BindImageMemory(
                    request.DeviceHandle,
                    imageHandle,
                    memoryHandle,
                    0
                ).ThrowIfFailed(operation: "vkBindImageMemory");

                return new VulkanExternalImageImportResult(
                    ImageHandle: imageHandle,
                    MemoryHandle: memoryHandle
                );
            } catch {
                pointers.FreeMemory(
                    request.DeviceHandle,
                    memoryHandle,
                    0
                );

                throw;
            }
        } catch {
            pointers.DestroyImage(
                request.DeviceHandle,
                imageHandle,
                0
            );

            throw;
        }
    }
    /// <inheritdoc/>
    public VulkanExternalImageExportResult CreateExportableImage(VulkanExternalImageExportRequest request) {
        var pointers = GetPointers(deviceHandle: request.DeviceHandle);

        var externalInfo = new VkExternalMemoryImageCreateInfo {
            HandleTypes = ExternalMemoryHandleTypeOpaqueWin32Bit,
            SType = StructureTypeExternalMemoryImageCreateInfo,
        };
        var imageInfo = new VkImageCreateInfo {
            ArrayLayers = 1,
            Extent = new VkExtent3D(
                width: request.Width,
                height: request.Height,
                depth: 1
            ),
            Format = request.Format,
            ImageType = ImageType2d,
            InitialLayout = 0,
            MipLevels = 1,
            PNext = (nint)(&externalInfo),
            SType = StructureTypeImageCreateInfo,
            Samples = SampleCount1Bit,
            SharingMode = SharingModeExclusive,
            Tiling = ImageTiling2dOptimal,
            // The caller picks usage: a render target wants COLOR_ATTACHMENT | SAMPLED | TRANSFER_SRC (matching a
            // plain VulkanViewTarget); a storage image wants STORAGE | SAMPLED | TRANSFER_SRC.
            Usage = request.UsageFlags,
        };

        pointers.CreateImage(
            request.DeviceHandle,
            in imageInfo,
            0,
            out var imageHandle
        ).ThrowIfFailed(operation: "vkCreateImage");

        try {
            pointers.GetImageMemoryRequirements(
                request.DeviceHandle,
                imageHandle,
                out var memoryRequirements
            );

            GetInstancePointers(instanceHandle: request.InstanceHandle).GetPhysicalDeviceMemoryProperties(
                request.PhysicalDeviceHandle,
                out var memoryProperties
            );

            // Fresh device-local memory: unlike the import path there is no handle-properties query to intersect,
            // so the type is chosen from the image's requirements alone.
            var memoryTypeIndex = FindMemoryTypeIndex(
                memoryProperties: memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits
            );
            var dedicatedInfo = new VkMemoryDedicatedAllocateInfo {
                Image = imageHandle,
                SType = StructureTypeMemoryDedicatedAllocateInfo,
            };
            var exportWin32Info = new VkExportMemoryWin32HandleInfoKHR {
                DwAccess = GenericAll,
                Name = 0,
                PAttributes = 0,
                PNext = (nint)(&dedicatedInfo),
                SType = StructureTypeExportMemoryWin32HandleInfo,
            };
            var exportInfo = new VkExportMemoryAllocateInfo {
                HandleTypes = ExternalMemoryHandleTypeOpaqueWin32Bit,
                PNext = (nint)(&exportWin32Info),
                SType = StructureTypeExportMemoryAllocateInfo,
            };
            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
                PNext = (nint)(&exportInfo),
                SType = StructureTypeMemoryAllocateInfo,
            };

            pointers.AllocateMemory(
                request.DeviceHandle,
                in allocateInfo,
                0,
                out var memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");

            try {
                pointers.BindImageMemory(
                    request.DeviceHandle,
                    imageHandle,
                    memoryHandle,
                    0
                ).ThrowIfFailed(operation: "vkBindImageMemory");

                var getHandleInfo = new VkMemoryGetWin32HandleInfoKHR {
                    HandleType = ExternalMemoryHandleTypeOpaqueWin32Bit,
                    Memory = memoryHandle,
                    SType = StructureTypeMemoryGetWin32HandleInfo,
                };

                pointers.GetMemoryWin32Handle(
                    request.DeviceHandle,
                    in getHandleInfo,
                    out var sharedHandle
                ).ThrowIfFailed(operation: "vkGetMemoryWin32HandleKHR");

                return new VulkanExternalImageExportResult(
                    ImageHandle: imageHandle,
                    MemoryHandle: memoryHandle,
                    SharedHandle: sharedHandle
                );
            } catch {
                pointers.FreeMemory(
                    request.DeviceHandle,
                    memoryHandle,
                    0
                );

                throw;
            }
        } catch {
            pointers.DestroyImage(
                request.DeviceHandle,
                imageHandle,
                0
            );

            throw;
        }
    }
    /// <inheritdoc/>
    public void DestroyImage(nint deviceHandle, nint imageHandle, nint memoryHandle) {
        var pointers = GetPointers(deviceHandle: deviceHandle);

        if (0 != imageHandle) {
            pointers.DestroyImage(
                deviceHandle,
                imageHandle,
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

    private static uint FindMemoryTypeIndex(VkPhysicalDeviceMemoryProperties memoryProperties, uint memoryTypeBits) {
        for (var index = 0; (index < memoryProperties.MemoryTypeCount); index++) {
            var supported = (0 != (memoryTypeBits & (1u << index)));
            var deviceLocal = (0 != (memoryProperties.MemoryTypePropertyFlags(memoryTypeIndex: index) & MemoryPropertyDeviceLocalBit));

            if (
                supported &&
                deviceLocal
            ) {
                return (uint)index;
            }
        }

        for (var index = 0; (index < memoryProperties.MemoryTypeCount); index++) {
            if (0 != (memoryTypeBits & (1u << index))) {
                return (uint)index;
            }
        }

        throw new InvalidOperationException(message: "No Vulkan memory type is compatible with the imported external handle.");
    }
    private DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var existing
        )) {
            return existing;
        }

        var getAddr = GetDeviceProcAddr();
        DevicePointers pointers = default;

        fixed (byte* name = "vkCreateImage"u8) {
            pointers.CreateImage = (delegate* unmanaged[Cdecl]<nint, in VkImageCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkDestroyImage"u8) {
            pointers.DestroyImage = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkGetImageMemoryRequirements"u8) {
            pointers.GetImageMemoryRequirements = (delegate* unmanaged[Cdecl]<nint, nint, out VkMemoryRequirements, void>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkAllocateMemory"u8) {
            pointers.AllocateMemory = (delegate* unmanaged[Cdecl]<nint, in VkMemoryAllocateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkFreeMemory"u8) {
            pointers.FreeMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkBindImageMemory"u8) {
            pointers.BindImageMemory = (delegate* unmanaged[Cdecl]<nint, nint, nint, ulong, VkResult>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkGetMemoryWin32HandlePropertiesKHR"u8) {
            pointers.GetMemoryWin32HandleProperties = (delegate* unmanaged[Cdecl]<nint, uint, nint, out VkMemoryWin32HandlePropertiesKHR, VkResult>)getAddr(
                deviceHandle,
                name
            );
        }
        fixed (byte* name = "vkGetMemoryWin32HandleKHR"u8) {
            pointers.GetMemoryWin32Handle = (delegate* unmanaged[Cdecl]<nint, in VkMemoryGetWin32HandleInfoKHR, out nint, VkResult>)getAddr(
                deviceHandle,
                name
            );
        }

        m_pointers[deviceHandle] = pointers;

        return pointers;
    }
    private InstancePointers GetInstancePointers(nint instanceHandle) {
        if (m_instancePointers.TryGetValue(
            key: instanceHandle,
            value: out var existing
        )) {
            return existing;
        }

        var getAddr = GetInstanceProcAddr();
        InstancePointers pointers = default;

        fixed (byte* name = "vkGetPhysicalDeviceMemoryProperties"u8) {
            pointers.GetPhysicalDeviceMemoryProperties = (delegate* unmanaged[Cdecl]<nint, out VkPhysicalDeviceMemoryProperties, void>)getAddr(
                instanceHandle,
                name
            );
        }

        m_instancePointers[instanceHandle] = pointers;

        return pointers;
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is null) {
                m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");
            }

            return m_getDeviceProcAddr;
        }
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetInstanceProcAddr() {
        lock (m_syncRoot) {
            if (m_getInstanceProcAddr is null) {
                m_getInstanceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)VulkanNativeLibrary.GetExport(functionName: "vkGetInstanceProcAddr");
            }

            return m_getInstanceProcAddr;
        }
    }
}
