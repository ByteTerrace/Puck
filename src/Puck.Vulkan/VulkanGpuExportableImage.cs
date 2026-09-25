using System.Runtime.InteropServices;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// A Vulkan <see cref="IGpuExportableImage"/>: an image with its declared usages whose backing memory lives in
/// exportable, dedicated device memory. Beyond the normal image handles, it exposes an opaque Win32 NT handle
/// (<see cref="SharedHandle"/>) another Vulkan instance imports to sample the result zero-copy. (Unlike a Direct3D 12
/// shared texture, an opaque-Vulkan handle is not importable by Direct3D 12, so this is a Vulkan-to-Vulkan capability.)
/// <para>
/// The producer transitions the image to <see cref="GpuImageLayout.External"/> (Vulkan GENERAL) as its final recorded
/// barrier and submits through the neutral queue; <see cref="FinalizeForExport"/> only drains the device so the
/// importing instance samples completed writes.
/// </para>
/// </summary>
public sealed partial class VulkanGpuExportableImage : IGpuExportableImage {
    private readonly VulkanDeviceCommands m_device;
    private readonly IVulkanExternalMemoryApi m_externalMemoryApi;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly VulkanLogicalDevice m_logicalDevice;
    private readonly nint m_memoryHandle;

    private bool m_disposed;
    private nint m_imageHandle;
    private nint m_imageViewHandle;
    private nint m_sharedHandle;

    private VulkanGpuExportableImage(IVulkanExternalMemoryApi externalMemoryApi, IVulkanFramebufferSetApi framebufferSetApi, VulkanLogicalDevice logicalDevice, VulkanExternalImageExportResult image, nint imageViewHandle, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {
        m_device = logicalDevice.Commands;
        m_externalMemoryApi = externalMemoryApi;
        m_framebufferSetApi = framebufferSetApi;
        m_imageHandle = image.ImageHandle;
        m_imageViewHandle = imageViewHandle;
        m_logicalDevice = logicalDevice;
        m_memoryHandle = image.MemoryHandle;
        m_sharedHandle = image.SharedHandle;
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
    public nint SharedHandle => m_sharedHandle;
    /// <inheritdoc/>
    public GpuImageUsage Usage { get; }
    /// <inheritdoc/>
    public uint Width { get; }

    /// <summary>Creates the exportable image and its view. An image whose view cannot be created is destroyed with its
    /// memory, and its shared handle closed, before the failure propagates, so a failed create owns nothing.</summary>
    /// <param name="externalMemoryApi">The API that creates and destroys the exportable image and its memory.</param>
    /// <param name="framebufferSetApi">The API that creates and destroys the view.</param>
    /// <param name="device">The device's command table.</param>
    /// <param name="instance">The instance's command table, which resolves the device's memory types.</param>
    /// <param name="physicalDeviceHandle">The native physical-device handle.</param>
    /// <param name="format">The image format; a color format.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="usage">The declared usages.</param>
    /// <returns>The image, its memory and shared handle, and its view, which the caller owns.</returns>
    public static (VulkanExternalImageExportResult Image, nint ImageViewHandle) CreateImageAndView(IVulkanExternalMemoryApi externalMemoryApi, IVulkanFramebufferSetApi framebufferSetApi, VulkanDeviceCommands device, VulkanInstanceCommands instance, nint physicalDeviceHandle, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {
        ArgumentNullException.ThrowIfNull(externalMemoryApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        GpuImageUsages.Validate(
            format: format,
            height: height,
            usage: usage,
            width: width
        );

        if (GpuPixelFormats.IsDepth(format: format)) {
            throw new ArgumentException(
                message: "A depth attachment is never exported.",
                paramName: nameof(format)
            );
        }

        var vkFormat = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format);
        var image = externalMemoryApi.CreateExportableImage(request: new VulkanExternalImageExportRequest(
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
                    Device: device,
                    Format: vkFormat,
                    ImageHandle: image.ImageHandle
                )
            ).ThrowIfFailed(operation: "vkCreateImageView");

            return (image, imageViewHandle);
        } catch {
            externalMemoryApi.DestroyImage(
                device: device,
                imageHandle: image.ImageHandle,
                memoryHandle: image.MemoryHandle
            );

            if (image.SharedHandle != 0) {
                _ = CloseHandle(handle: image.SharedHandle);
            }

            throw;
        }
    }
    /// <summary>Creates an exportable image on a device context through <see cref="CreateImageAndView"/>.</summary>
    /// <param name="externalMemoryApi">The API that creates and destroys the exportable image and its memory.</param>
    /// <param name="framebufferSetApi">The API that creates and destroys the view.</param>
    /// <param name="deviceContext">The Vulkan device context.</param>
    /// <param name="format">The image format; a color format.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="usage">The declared usages.</param>
    /// <returns>The image, owned by the caller.</returns>
    public static VulkanGpuExportableImage Create(IVulkanExternalMemoryApi externalMemoryApi, IVulkanFramebufferSetApi framebufferSetApi, IVulkanDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {

        var logicalDevice = deviceContext.LogicalDevice;

        var (image, view) = CreateImageAndView(
            device: logicalDevice.Commands,
            externalMemoryApi: externalMemoryApi,
            format: format,
            framebufferSetApi: framebufferSetApi,
            height: height,
            instance: deviceContext.Instance.Commands,
            physicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            usage: usage,
            width: width
        );

        return new VulkanGpuExportableImage(
            externalMemoryApi: externalMemoryApi,
            format: format,
            framebufferSetApi: framebufferSetApi,
            height: height,
            image: image,
            imageViewHandle: view,
            logicalDevice: logicalDevice,
            usage: usage,
            width: width
        );
    }
    /// <inheritdoc/>
    public void FinalizeForExport() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        // The producer already recorded the GENERAL handoff transition and submitted; drain the device so the
        // importing instance samples completed writes. Drains on every call: a per-frame producer re-submits and
        // re-finalizes this image each frame, and the neutral submit path carries no fence — matching the
        // unconditional per-frame fence on the Direct3D 12 exportable image.
        m_logicalDevice.WaitIdle();
    }
    /// <summary>Releases the image view, the exportable image and its memory, and closes the exported shared
    /// handle. Safe to call more than once.</summary>
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
        m_externalMemoryApi.DestroyImage(
            device: m_device,
            imageHandle: m_imageHandle,
            memoryHandle: m_memoryHandle
        );
        m_imageHandle = 0;

        if (0 != m_sharedHandle) {
            _ = CloseHandle(handle: m_sharedHandle);
            m_sharedHandle = 0;
        }
    }

    // The handle from vkGetMemoryWin32HandleKHR (OPAQUE_WIN32) is a fresh NT handle this image owns and closes.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
/// <summary>
/// Implements <see cref="IGpuSurfaceExportFactory"/> for Vulkan by creating <see cref="VulkanGpuExportableImage"/>
/// instances.
/// </summary>
public sealed class VulkanGpuSurfaceExportFactory(IVulkanDeviceContext deviceContext, IVulkanExternalMemoryApi externalMemoryApi, IVulkanFramebufferSetApi framebufferSetApi) : IGpuSurfaceExportFactory {
    /// <inheritdoc/>
    public IGpuExportableImage CreateExportableImage(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) =>
        VulkanGpuExportableImage.Create(
            deviceContext: deviceContext,
            externalMemoryApi: externalMemoryApi,
            format: format,
            framebufferSetApi: framebufferSetApi,
            height: height,
            usage: usage,
            width: width
        );
}
