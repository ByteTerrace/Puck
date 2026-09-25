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

    /// <inheritdoc/>
    public VulkanExternalImageExportResult CreateExportableImage(VulkanExternalImageExportRequest request) {
        RequireWin32ExternalMemory(device: request.Device);

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
            PNext = ((nint)(&externalInfo)),
            SType = StructureTypeImageCreateInfo,
            Samples = SampleCount1Bit,
            SharingMode = SharingModeExclusive,
            Tiling = ImageTiling2dOptimal,
            // The caller picks usage: VulkanGpuFormats.ToVkImageUsage maps an image's declared usages.
            Usage = request.UsageFlags,
        };

        request.Device.CreateImage(
            request.Device.Handle,
            in imageInfo,
            0,
            out var imageHandle
        ).ThrowIfFailed(operation: "vkCreateImage");

        try {
            request.Device.GetImageMemoryRequirements(
                request.Device.Handle,
                imageHandle,
                out var memoryRequirements
            );

            request.Instance.GetPhysicalDeviceMemoryProperties(
                request.PhysicalDeviceHandle,
                out var memoryProperties
            );

            // Fresh device-local memory: unlike the import path there is no handle-properties query to intersect,
            // so the type is chosen from the image's requirements alone.
            var memoryTypeIndex = VulkanMemoryTypes.FindIndex(
                memoryProperties: in memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits,
                preferredProperties: MemoryPropertyDeviceLocalBit,
                requireProperties: false,
                resourceDescription: "the imported external handle"
            );
            var dedicatedInfo = new VkMemoryDedicatedAllocateInfo {
                Image = imageHandle,
                SType = StructureTypeMemoryDedicatedAllocateInfo,
            };
            var exportWin32Info = new VkExportMemoryWin32HandleInfoKHR {
                DwAccess = GenericAll,
                Name = 0,
                PAttributes = 0,
                PNext = ((nint)(&dedicatedInfo)),
                SType = StructureTypeExportMemoryWin32HandleInfo,
            };
            var exportInfo = new VkExportMemoryAllocateInfo {
                HandleTypes = ExternalMemoryHandleTypeOpaqueWin32Bit,
                PNext = ((nint)(&exportWin32Info)),
                SType = StructureTypeExportMemoryAllocateInfo,
            };
            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
                PNext = ((nint)(&exportInfo)),
                SType = StructureTypeMemoryAllocateInfo,
            };

            request.Device.AllocateMemory(
                request.Device.Handle,
                in allocateInfo,
                0,
                out var memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");
            request.Device.CountAllocated(
                allocateInfo: in allocateInfo,
                memoryHandle: memoryHandle,
                memoryProperties: in memoryProperties
            );

            try {
                request.Device.BindImageMemory(
                    request.Device.Handle,
                    imageHandle,
                    memoryHandle,
                    0
                ).ThrowIfFailed(operation: "vkBindImageMemory");

                var getHandleInfo = new VkMemoryGetWin32HandleInfoKHR {
                    HandleType = ExternalMemoryHandleTypeOpaqueWin32Bit,
                    Memory = memoryHandle,
                    SType = StructureTypeMemoryGetWin32HandleInfo,
                };

                request.Device.GetMemoryWin32HandleKhr(
                    request.Device.Handle,
                    in getHandleInfo,
                    out var sharedHandle
                ).ThrowIfFailed(operation: "vkGetMemoryWin32HandleKHR");

                return new VulkanExternalImageExportResult(
                    ImageHandle: imageHandle,
                    MemoryHandle: memoryHandle,
                    SharedHandle: sharedHandle
                );
            } catch {
                request.Device.Destroy(
                    destroy: request.Device.FreeMemory,
                    handle: memoryHandle
                );

                throw;
            }
        } catch {
            request.Device.Destroy(
                destroy: request.Device.DestroyImage,
                handle: imageHandle
            );

            throw;
        }
    }
    /// <inheritdoc/>
    public void DestroyImage(VulkanDeviceCommands device, nint imageHandle, nint memoryHandle) {
        device.Destroy(
            destroy: device.DestroyImage,
            handle: imageHandle,
            memoryHandle: memoryHandle
        );
    }
    /// <inheritdoc/>
    public VulkanExternalImageImportResult ImportImage(VulkanExternalImageImportRequest request) {
        RequireWin32ExternalMemory(device: request.Device);

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
            PNext = ((nint)(&externalInfo)),
            SType = StructureTypeImageCreateInfo,
            Samples = SampleCount1Bit,
            SharingMode = SharingModeExclusive,
            Tiling = ImageTiling2dOptimal,
            // The default samples a foreign render target; a non-zero UsageFlags imports it as a writable image
            // instead (e.g. STORAGE, so Vulkan can produce a compute result INTO a Direct3D 12-owned resource).
            Usage = ((request.UsageFlags != 0)
            ? request.UsageFlags
            : ImageUsageSampledBit | ImageUsageColorAttachmentBit),
        };

        request.Device.CreateImage(
            request.Device.Handle,
            in imageInfo,
            0,
            out var imageHandle
        ).ThrowIfFailed(operation: "vkCreateImage");

        try {
            request.Device.GetImageMemoryRequirements(
                request.Device.Handle,
                imageHandle,
                out var memoryRequirements
            );

            var handleProperties = new VkMemoryWin32HandlePropertiesKHR {
                SType = StructureTypeMemoryWin32HandleProperties,
            };

            request.Device.GetMemoryWin32HandlePropertiesKhr(
                request.Device.Handle,
                ExternalMemoryHandleTypeD3D12ResourceBit,
                request.SharedHandle,
                out handleProperties
            ).ThrowIfFailed(operation: "vkGetMemoryWin32HandlePropertiesKHR");

            request.Instance.GetPhysicalDeviceMemoryProperties(
                request.PhysicalDeviceHandle,
                out var memoryProperties
            );

            var memoryTypeIndex = VulkanMemoryTypes.FindIndex(
                memoryProperties: in memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits & handleProperties.MemoryTypeBits,
                preferredProperties: MemoryPropertyDeviceLocalBit,
                requireProperties: false,
                resourceDescription: "the imported external handle"
            );
            var dedicatedInfo = new VkMemoryDedicatedAllocateInfo {
                Image = imageHandle,
                SType = StructureTypeMemoryDedicatedAllocateInfo,
            };
            var importInfo = new VkImportMemoryWin32HandleInfoKHR {
                Handle = request.SharedHandle,
                HandleType = ExternalMemoryHandleTypeD3D12ResourceBit,
                PNext = ((nint)(&dedicatedInfo)),
                SType = StructureTypeImportMemoryWin32HandleInfo,
            };
            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
                PNext = ((nint)(&importInfo)),
                SType = StructureTypeMemoryAllocateInfo,
            };

            request.Device.AllocateMemory(
                request.Device.Handle,
                in allocateInfo,
                0,
                out var memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");
            request.Device.CountAllocated(
                allocateInfo: in allocateInfo,
                memoryHandle: memoryHandle,
                memoryProperties: in memoryProperties
            );

            try {
                request.Device.BindImageMemory(
                    request.Device.Handle,
                    imageHandle,
                    memoryHandle,
                    0
                ).ThrowIfFailed(operation: "vkBindImageMemory");

                return new VulkanExternalImageImportResult(
                    ImageHandle: imageHandle,
                    MemoryHandle: memoryHandle
                );
            } catch {
                request.Device.Destroy(
                    destroy: request.Device.FreeMemory,
                    handle: memoryHandle
                );

                throw;
            }
        } catch {
            request.Device.Destroy(
                destroy: request.Device.DestroyImage,
                handle: imageHandle
            );

            throw;
        }
    }
    /// <inheritdoc/>
    public VulkanExternalImageImportResult ImportOpaqueImage(VulkanExternalImageImportRequest request) {
        RequireWin32ExternalMemory(device: request.Device);

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
            PNext = ((nint)(&externalInfo)),
            SType = StructureTypeImageCreateInfo,
            Samples = SampleCount1Bit,
            SharingMode = SharingModeExclusive,
            Tiling = ImageTiling2dOptimal,
            Usage = ImageUsageSampledBit | ImageUsageColorAttachmentBit | ImageUsageTransferSourceBit,
        };

        request.Device.CreateImage(
            request.Device.Handle,
            in imageInfo,
            0,
            out var imageHandle
        ).ThrowIfFailed(operation: "vkCreateImage");

        try {
            request.Device.GetImageMemoryRequirements(
                request.Device.Handle,
                imageHandle,
                out var memoryRequirements
            );

            request.Instance.GetPhysicalDeviceMemoryProperties(
                request.PhysicalDeviceHandle,
                out var memoryProperties
            );

            // An opaque Win32 handle came from a Vulkan allocation, so its compatible memory types are exactly the
            // image's requirements. vkGetMemoryWin32HandlePropertiesKHR must NOT be called for an opaque handle type
            // (the spec restricts it to foreign/non-opaque handles), unlike the Direct3D 12 import path.
            var memoryTypeIndex = VulkanMemoryTypes.FindIndex(
                memoryProperties: in memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits,
                preferredProperties: MemoryPropertyDeviceLocalBit,
                requireProperties: false,
                resourceDescription: "the imported external handle"
            );
            var dedicatedInfo = new VkMemoryDedicatedAllocateInfo {
                Image = imageHandle,
                SType = StructureTypeMemoryDedicatedAllocateInfo,
            };
            var importInfo = new VkImportMemoryWin32HandleInfoKHR {
                Handle = request.SharedHandle,
                HandleType = ExternalMemoryHandleTypeOpaqueWin32Bit,
                PNext = ((nint)(&dedicatedInfo)),
                SType = StructureTypeImportMemoryWin32HandleInfo,
            };
            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
                PNext = ((nint)(&importInfo)),
                SType = StructureTypeMemoryAllocateInfo,
            };

            request.Device.AllocateMemory(
                request.Device.Handle,
                in allocateInfo,
                0,
                out var memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");
            request.Device.CountAllocated(
                allocateInfo: in allocateInfo,
                memoryHandle: memoryHandle,
                memoryProperties: in memoryProperties
            );

            try {
                request.Device.BindImageMemory(
                    request.Device.Handle,
                    imageHandle,
                    memoryHandle,
                    0
                ).ThrowIfFailed(operation: "vkBindImageMemory");

                return new VulkanExternalImageImportResult(
                    ImageHandle: imageHandle,
                    MemoryHandle: memoryHandle
                );
            } catch {
                request.Device.Destroy(
                    destroy: request.Device.FreeMemory,
                    handle: memoryHandle
                );

                throw;
            }
        } catch {
            request.Device.Destroy(
                destroy: request.Device.DestroyImage,
                handle: imageHandle
            );

            throw;
        }
    }

    // Both Win32 entry points exist exactly when VK_KHR_external_memory_win32 is enabled, so checking them before the
    // first native call keeps a device without the extension from creating an image with an unenabled handle type.
    private static void RequireWin32ExternalMemory(VulkanDeviceCommands device) {
        if (device.GetMemoryWin32HandleKhr is null) {
            throw VulkanProcResolver.MissingDeviceProc(functionName: "vkGetMemoryWin32HandleKHR"u8);
        }

        if (device.GetMemoryWin32HandlePropertiesKhr is null) {
            throw VulkanProcResolver.MissingDeviceProc(functionName: "vkGetMemoryWin32HandlePropertiesKHR"u8);
        }
    }
}
