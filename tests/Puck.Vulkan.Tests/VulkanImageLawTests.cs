using Puck.Abstractions.Gpu;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, what the image factories create through their API seams: the Vulkan usage and view
/// aspect a declared <see cref="GpuImageUsage"/> maps to, and that an image whose view cannot be created is destroyed
/// with its memory before the failure propagates, so a failed create owns nothing.</summary>
public sealed class VulkanImageLawTests {
    private const nint Image = 0x100;
    private const nint Memory = 0x200;
    private const nint View = 0x300;

    [InlineData(GpuPixelFormat.R8G8B8A8Unorm, GpuImageUsage.Storage | GpuImageUsage.Sampled, 0x0000000Fu, 0x1u)]
    [InlineData(GpuPixelFormat.R8G8B8A8Unorm, GpuImageUsage.ColorAttachment | GpuImageUsage.Sampled, 0x00000017u, 0x1u)]
    [InlineData(GpuPixelFormat.R16G16B16A16Float, GpuImageUsage.ColorAttachment | GpuImageUsage.Sampled | GpuImageUsage.Storage, 0x0000001Fu, 0x1u)]
    [InlineData(GpuPixelFormat.D32Float, GpuImageUsage.DepthAttachment, 0x00000020u, 0x2u)]
    [Theory]
    public void AnImageIsCreatedWithTheVulkanUsageAndAspectItsDeclaredUsagesNeed(GpuPixelFormat format, GpuImageUsage usage, uint vulkanUsage, uint aspect) {
        var images = new RecordingOffscreenImageApi();
        var views = new RecordingFramebufferSetApi(result: VkResult.Success);

        using var image = VulkanGpuImage.Create(
            device: null!,
            format: format,
            framebufferSetApi: views,
            height: 8,
            instance: null!,
            offscreenImageApi: images,
            physicalDeviceHandle: 1,
            usage: usage,
            width: 8
        );

        Assert.Equal(
            actual: (Usage: images.Requests.Single().UsageFlags, Aspect: views.Requests.Single().AspectMask, image.Format, image.Usage, image.ImageHandle, image.ImageViewHandle),
            expected: (Usage: vulkanUsage, Aspect: aspect, format, usage, Image, View)
        );
    }
    [Fact]
    public void AnImageWhoseViewFailsIsDestroyedWithItsMemory() {
        var images = new RecordingOffscreenImageApi();
        var views = new RecordingFramebufferSetApi(result: VkResult.ErrorOutOfDeviceMemory);

        var failure = Assert.Throws<VulkanException>(testCode: () => VulkanGpuImage.Create(
            device: null!,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            framebufferSetApi: views,
            height: 8,
            instance: null!,
            offscreenImageApi: images,
            physicalDeviceHandle: 1,
            usage: GpuImageUsage.Storage | GpuImageUsage.Sampled,
            width: 8
        ));

        Assert.Equal(
            actual: (failure.Result, Created: images.Requests.Count),
            expected: (VkResult.ErrorOutOfDeviceMemory, Created: 1)
        );
        Assert.Equal(
            actual: images.Destroyed,
            expected: [(Image, Memory)]
        );
    }
    [Fact]
    public void AnExportableImageWhoseViewFailsIsDestroyedWithItsMemory() {
        var exports = new RecordingExternalMemoryApi();
        var views = new RecordingFramebufferSetApi(result: VkResult.ErrorOutOfDeviceMemory);

        var failure = Assert.Throws<VulkanException>(testCode: () => VulkanGpuExportableImage.CreateImageAndView(
            device: null!,
            externalMemoryApi: exports,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            framebufferSetApi: views,
            height: 8,
            instance: null!,
            physicalDeviceHandle: 1,
            usage: GpuImageUsage.Storage | GpuImageUsage.Sampled,
            width: 8
        ));

        Assert.Equal(
            actual: (failure.Result, Created: exports.Requests.Count),
            expected: (VkResult.ErrorOutOfDeviceMemory, Created: 1)
        );
        Assert.Equal(
            actual: exports.Destroyed,
            expected: [(Image, Memory)]
        );
    }
    [Fact]
    public void AnImageRequestThatBreaksAUsageRuleCreatesNothing() {
        var images = new RecordingOffscreenImageApi();
        var views = new RecordingFramebufferSetApi(result: VkResult.Success);

        _ = Assert.Throws<ArgumentException>(testCode: () => VulkanGpuImage.Create(
            device: null!,
            format: GpuPixelFormat.D32Float,
            framebufferSetApi: views,
            height: 8,
            instance: null!,
            offscreenImageApi: images,
            physicalDeviceHandle: 1,
            usage: GpuImageUsage.DepthAttachment | GpuImageUsage.Sampled,
            width: 8
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => VulkanGpuImage.Create(
            device: null!,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            framebufferSetApi: views,
            height: 8,
            instance: null!,
            offscreenImageApi: images,
            physicalDeviceHandle: 1,
            usage: GpuImageUsage.None,
            width: 8
        ));
        Assert.Equal(
            actual: (Created: images.Requests.Count, Views: views.Requests.Count),
            expected: (Created: 0, Views: 0)
        );
    }

    private sealed class RecordingOffscreenImageApi : IVulkanOffscreenImageApi {
        public List<(nint Image, nint Memory)> Destroyed { get; } = [];
        public List<VulkanOffscreenImageCreateRequest> Requests { get; } = [];

        public VulkanOffscreenImageCreateResult CreateColorImage(VulkanOffscreenImageCreateRequest request) {
            Requests.Add(item: request);

            return new VulkanOffscreenImageCreateResult(
                ImageHandle: Image,
                MemoryHandle: Memory
            );
        }
        public void DestroyColorImage(VulkanDeviceCommands device, nint imageHandle, nint memoryHandle) => Destroyed.Add(item: (imageHandle, memoryHandle));
    }
    private sealed class RecordingExternalMemoryApi : IVulkanExternalMemoryApi {
        public List<(nint Image, nint Memory)> Destroyed { get; } = [];
        public List<VulkanExternalImageExportRequest> Requests { get; } = [];

        public VulkanExternalImageExportResult CreateExportableImage(VulkanExternalImageExportRequest request) {
            Requests.Add(item: request);

            return new VulkanExternalImageExportResult(
                ImageHandle: Image,
                MemoryHandle: Memory,
                SharedHandle: 0
            );
        }
        public void DestroyImage(VulkanDeviceCommands device, nint imageHandle, nint memoryHandle) => Destroyed.Add(item: (imageHandle, memoryHandle));
        public VulkanExternalImageImportResult ImportImage(VulkanExternalImageImportRequest request) => throw new NotSupportedException();
        public VulkanExternalImageImportResult ImportOpaqueImage(VulkanExternalImageImportRequest request) => throw new NotSupportedException();
    }
    private sealed class RecordingFramebufferSetApi(VkResult result) : IVulkanFramebufferSetApi {
        public List<VulkanImageViewCreateRequest> Requests { get; } = [];

        public VkResult CreateFramebuffer(VulkanFramebufferCreateRequest request, out nint framebufferHandle) => throw new NotSupportedException();
        public VkResult CreateImageView(VulkanImageViewCreateRequest request, out nint imageViewHandle) {
            Requests.Add(item: request);
            imageViewHandle = ((result == VkResult.Success)
                ? View
                : 0
            );

            return result;
        }
        public void DestroyFramebuffer(VulkanDeviceCommands device, nint framebufferHandle) => throw new NotSupportedException();
        public void DestroyImageView(VulkanDeviceCommands device, nint imageViewHandle) { }
        public IReadOnlyList<nint> GetSwapchainImages(VulkanDeviceCommands device, nint swapchainHandle) => throw new NotSupportedException();
    }
}
