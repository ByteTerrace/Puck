using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>Native <see cref="IVulkanOffscreenImageApi"/>: a generic device-local 2D color
/// image plus bound memory, loaded through the same per-device proc-address pattern as the
/// other native resource APIs. Carries no policy — usage and format are the caller's.</summary>
public unsafe sealed class VulkanNativeOffscreenImageApi : IVulkanOffscreenImageApi {
    private const uint ImageLayoutUndefined = 0;
    private const uint ImageTiling2DOptimal = 0;
    private const uint ImageType2D = 1;
    private const uint MemoryPropertyDeviceLocalBit = 0x00000001;
    private const uint SampleCount1Bit = 1;
    private const uint SharingModeExclusive = 0;
    private const uint StructureTypeImageCreateInfo = 14;
    private const uint StructureTypeMemoryAllocateInfo = 5;

    private static void ValidateCreateRequest(VulkanOffscreenImageCreateRequest request) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        ArgumentNullException.ThrowIfNull(
            argument: request.Instance,
            paramName: nameof(request)
        );

        VulkanArgument.RequireHandle(
            handle: request.PhysicalDeviceHandle,
            handleDescription: "physical-device",
            paramName: nameof(request)
        );

        ArgumentOutOfRangeException.ThrowIfZero(
            value: request.Width,
            paramName: nameof(request)
        );
        ArgumentOutOfRangeException.ThrowIfZero(
            value: request.Height,
            paramName: nameof(request)
        );
        ArgumentOutOfRangeException.ThrowIfZero(
            value: request.MipLevels,
            paramName: nameof(request)
        );
    }

    /// <inheritdoc/>
    public VulkanOffscreenImageCreateResult CreateColorImage(VulkanOffscreenImageCreateRequest request) {
        ValidateCreateRequest(request: request);

        var getPhysicalDeviceMemoryProperties = request.Instance.GetPhysicalDeviceMemoryProperties;

        nint imageHandle = 0;
        nint memoryHandle = 0;

        try {
            var createInfo = new VkImageCreateInfo {
                ArrayLayers = 1,
                Extent = new VkExtent3D(
                depth: 1,
                height: request.Height,
                width: request.Width
            ),
                Format = request.Format,
                ImageType = ImageType2D,
                InitialLayout = ImageLayoutUndefined,
                MipLevels = request.MipLevels,
                SType = StructureTypeImageCreateInfo,
                Samples = SampleCount1Bit,
                SharingMode = SharingModeExclusive,
                Tiling = ImageTiling2DOptimal,
                Usage = request.UsageFlags,
            };

            request.Device.CreateImage(
                request.Device.Handle,
                in createInfo,
                0,
                out imageHandle
            ).ThrowIfFailed(operation: "vkCreateImage");
            request.Device.GetImageMemoryRequirements(
                request.Device.Handle,
                imageHandle,
                out var memoryRequirements
            );
            getPhysicalDeviceMemoryProperties(
                request.PhysicalDeviceHandle,
                out var memoryProperties
            );
            var allocateInfo = new VkMemoryAllocateInfo {
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = VulkanMemoryTypes.FindIndex(
                memoryProperties: in memoryProperties,
                memoryTypeBits: memoryRequirements.MemoryTypeBits,
                preferredProperties: MemoryPropertyDeviceLocalBit,
                requireProperties: false,
                resourceDescription: "an offscreen color image"
            ),
                SType = StructureTypeMemoryAllocateInfo,
            };

            request.Device.AllocateMemory(
                request.Device.Handle,
                in allocateInfo,
                0,
                out memoryHandle
            ).ThrowIfFailed(operation: "vkAllocateMemory");
            request.Device.CountAllocated(
                allocationSize: allocateInfo.AllocationSize,
                memoryHandle: memoryHandle,
                role: GpuMemoryRole.DeviceLocal
            );
            request.Device.BindImageMemory(
                request.Device.Handle,
                imageHandle,
                memoryHandle,
                0
            ).ThrowIfFailed(operation: "vkBindImageMemory");

            return new VulkanOffscreenImageCreateResult(
                ImageHandle: imageHandle,
                MemoryHandle: memoryHandle
            );
        } catch {
            request.Device.Destroy(
                destroy: request.Device.DestroyImage,
                handle: imageHandle,
                memoryHandle: memoryHandle
            );

            throw;
        }
    }
    /// <inheritdoc/>
    public void DestroyColorImage(VulkanDeviceCommands device, nint imageHandle, nint memoryHandle) =>
        device?.Destroy(
            destroy: device.DestroyImage,
            handle: imageHandle,
            memoryHandle: memoryHandle
        );
}
