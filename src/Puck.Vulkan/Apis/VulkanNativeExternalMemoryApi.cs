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
    private const uint ImageTiling2dOptimal = 0;
    private const uint ImageType2d = 1;
    // The shared resource is a Direct3D 12 render target, so the imported image must allow color-attachment use
    // (matching the producer) in addition to being sampled by the consumer.
    private const uint ImageUsageColorAttachmentBit = 0x00000010;
    private const uint ImageUsageSampledBit = 0x00000004;
    private const uint MemoryPropertyDeviceLocalBit = 0x00000001;
    private const uint SampleCount1Bit = 1;
    private const uint SharingModeExclusive = 0;
    private const uint StructureTypeExternalMemoryImageCreateInfo = 1000072001;
    private const uint StructureTypeImageCreateInfo = 14;
    private const uint StructureTypeImportMemoryWin32HandleInfo = 1000073000;
    private const uint StructureTypeMemoryAllocateInfo = 5;
    private const uint StructureTypeMemoryDedicatedAllocateInfo = 1000127001;
    private const uint StructureTypeMemoryWin32HandleProperties = 1000073002;

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
        ).ThrowIfFailed(device: request.Device, operation: "vkCreateImage");

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
            ).ThrowIfFailed(device: request.Device, operation: "vkGetMemoryWin32HandlePropertiesKHR");

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
            ).ThrowIfFailed(device: request.Device, operation: "vkAllocateMemory");
            request.Device.CountAllocated(
                allocationSize: allocateInfo.AllocationSize,
                memoryHandle: memoryHandle,
                role: GpuMemoryRole.DeviceLocal
            );

            try {
                request.Device.BindImageMemory(
                    request.Device.Handle,
                    imageHandle,
                    memoryHandle,
                    0
                ).ThrowIfFailed(device: request.Device, operation: "vkBindImageMemory");

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
