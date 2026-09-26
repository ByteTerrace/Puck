using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// A Vulkan <see cref="IGpuImage"/>: a device-local image created with the Vulkan usage its declared
/// <see cref="GpuImageUsage"/> maps to (<see cref="VulkanGpuFormats.ToVkImageUsage"/>) plus its 2D view, destroyed through
/// the offscreen-image and framebuffer-set APIs on dispose.
/// </summary>
public sealed class VulkanGpuImage : IGpuImage {
    private readonly VulkanDeviceCommands m_device;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly nint m_memoryHandle;
    private readonly IVulkanOffscreenImageApi m_offscreenImageApi;

    private bool m_disposed;
    private nint m_imageHandle;
    private nint m_imageViewHandle;

    private VulkanGpuImage(IVulkanOffscreenImageApi offscreenImageApi, IVulkanFramebufferSetApi framebufferSetApi, VulkanDeviceCommands device, nint imageHandle, nint memoryHandle, nint imageViewHandle, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {
        m_device = device;
        m_framebufferSetApi = framebufferSetApi;
        m_imageHandle = imageHandle;
        m_imageViewHandle = imageViewHandle;
        m_memoryHandle = memoryHandle;
        m_offscreenImageApi = offscreenImageApi;
        Format = format;
        Height = height;
        Usage = usage;
        Width = width;
    }

    /// <inheritdoc/>
    public GpuPixelFormat Format { get; }
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public nint ImageHandle => m_imageHandle;
    /// <inheritdoc/>
    public nint ImageViewHandle => m_imageViewHandle;
    /// <inheritdoc/>
    public GpuImageUsage Usage { get; }
    /// <inheritdoc/>
    public uint Width { get; }

    /// <summary>Creates an image and its view. An image whose view cannot be created is destroyed, with its memory,
    /// before the failure propagates, so a failed create owns nothing.</summary>
    /// <param name="offscreenImageApi">The API that creates and destroys the image and its memory.</param>
    /// <param name="framebufferSetApi">The API that creates and destroys the view.</param>
    /// <param name="device">The device's command table.</param>
    /// <param name="instance">The instance's command table, which resolves the device's memory types.</param>
    /// <param name="physicalDeviceHandle">The native physical-device handle.</param>
    /// <param name="format">The image format.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="usage">The declared usages; <see cref="GpuImageUsages.Validate"/> refuses a request that breaks a
    /// rule before anything is created.</param>
    /// <returns>The image, owned by the caller.</returns>
    public static VulkanGpuImage Create(IVulkanOffscreenImageApi offscreenImageApi, IVulkanFramebufferSetApi framebufferSetApi, VulkanDeviceCommands device, VulkanInstanceCommands instance, nint physicalDeviceHandle, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {
        ArgumentNullException.ThrowIfNull(offscreenImageApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        GpuImageUsages.Validate(
            format: format,
            height: height,
            usage: usage,
            width: width
        );

        var vkFormat = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format);
        var image = offscreenImageApi.CreateColorImage(request: new VulkanOffscreenImageCreateRequest(
            Device: device,
            Format: vkFormat,
            Height: height,
            Instance: instance,
            PhysicalDeviceHandle: physicalDeviceHandle,
            UsageFlags: VulkanGpuFormats.ToVkImageUsage(usage: usage),
            Width: width
        ));

        try {
            framebufferSetApi.CreateImageView(
                imageViewHandle: out var imageViewHandle,
                request: new VulkanImageViewCreateRequest(
                    AspectMask: VulkanGpuFormats.AspectOf(format: format),
                    Device: device,
                    Format: vkFormat,
                    ImageHandle: image.ImageHandle
                )
            ).ThrowIfFailed(operation: "vkCreateImageView");

            return new VulkanGpuImage(
                device: device,
                format: format,
                framebufferSetApi: framebufferSetApi,
                height: height,
                imageHandle: image.ImageHandle,
                imageViewHandle: imageViewHandle,
                memoryHandle: image.MemoryHandle,
                offscreenImageApi: offscreenImageApi,
                usage: usage,
                width: width
            );
        } catch {
            offscreenImageApi.DestroyColorImage(
                device: device,
                imageHandle: image.ImageHandle,
                memoryHandle: image.MemoryHandle
            );

            throw;
        }
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_framebufferSetApi.DestroyImageView(
            device: m_device,
            imageViewHandle: m_imageViewHandle
        );
        m_imageViewHandle = 0;
        m_offscreenImageApi.DestroyColorImage(
            device: m_device,
            imageHandle: m_imageHandle,
            memoryHandle: m_memoryHandle
        );
        m_imageHandle = 0;
    }
}
/// <summary>
/// Implements <see cref="IGpuImageFactory"/> for Vulkan through <see cref="VulkanGpuImage.Create"/>.
/// </summary>
/// <param name="deviceContext">The device context every image is created on.</param>
/// <param name="offscreenImageApi">The native image API.</param>
/// <param name="framebufferSetApi">The native image-view API.</param>
/// <param name="naming">The naming every created object is handed to.</param>
public sealed class VulkanGpuImageFactory(IVulkanDeviceContext deviceContext, IVulkanOffscreenImageApi offscreenImageApi, IVulkanFramebufferSetApi framebufferSetApi, GpuObjectNaming naming) : IGpuImageFactory {
    /// <inheritdoc/>
    public IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name) {
        GpuImageUsages.ValidateCreate(
            format: format,
            height: height,
            usage: usage,
            width: width
        );

        return Named(
            format: format,
            height: height,
            name: in name,
            usage: usage,
            width: width
        );
    }
    /// <inheritdoc/>
    /// <remarks>Vulkan records a clear with the render pass that begins, so the image keeps no clear of its own.</remarks>
    public IGpuImage CreateDepth(in GpuDepthAttachment attachment, uint width, uint height, in GpuObjectName name) {
        GpuImageUsages.ValidateDepth(
            attachment: in attachment,
            height: height,
            width: width
        );

        return Named(
            format: attachment.Format,
            height: height,
            name: in name,
            usage: GpuImageUsage.DepthAttachment,
            width: width
        );
    }

    // Creates an image and names it and its view.
    private VulkanGpuImage Named(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name) {
        var vkContext = deviceContext;
        var logicalDevice = vkContext.LogicalDevice;
        var image = VulkanGpuImage.Create(
            device: logicalDevice.Commands,
            format: format,
            framebufferSetApi: framebufferSetApi,
            height: height,
            instance: vkContext.Instance.Commands,
            offscreenImageApi: offscreenImageApi,
            physicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            usage: usage,
            width: width
        );

        naming.Name(
            handle: image.ImageHandle,
            kind: GpuObjectKind.Image,
            name: in name
        );
        naming.Name(
            handle: image.ImageViewHandle,
            kind: GpuObjectKind.ImageView,
            name: in name
        );

        return image;
    }
}
