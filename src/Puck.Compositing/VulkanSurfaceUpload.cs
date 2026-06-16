using Puck.Hosting;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Compositing;

/// <summary>
/// Materializes a CPU-pixel <see cref="Surface"/> onto a Vulkan device so a host can sample it like any other
/// view target. It owns a host-visible staging buffer, a sampled image, and that image's view, and rebuilds
/// them when the device or the surface's extent/format changes. Each <see cref="Upload"/> writes the pixels
/// into the staging buffer, copies them into the image, leaves it shader-readable, and returns the image-view
/// handle. This is the generic counterpart to <see cref="VulkanViewTarget"/> for surfaces that crossed a
/// device boundary as host memory — the consumer half of the CPU-pixel transport, reusable by any Vulkan host.
/// </summary>
public sealed class VulkanSurfaceUpload : IDisposable {
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly IVulkanOffscreenImageApi m_offscreenImageApi;
    private readonly VulkanQueueSubmitter m_queueSubmitter;
    private readonly IVulkanStorageBufferFactory m_storageBufferFactory;
    private VulkanCommandResources? m_commandResources;
    private VulkanLogicalDevice? m_device;
    private bool m_disposed;
    private SurfaceFormat m_format;
    private uint m_height;
    private nint m_imageHandle;
    private nint m_imageViewHandle;
    private nint m_memoryHandle;
    private VulkanStorageBuffer? m_stagingBuffer;
    private uint m_width;

    public VulkanSurfaceUpload(
        IVulkanOffscreenImageApi offscreenImageApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanStorageBufferFactory storageBufferFactory,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        VulkanQueueSubmitter queueSubmitter
    ) {
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(offscreenImageApi);
        ArgumentNullException.ThrowIfNull(queueSubmitter);
        ArgumentNullException.ThrowIfNull(storageBufferFactory);

        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_framebufferSetApi = framebufferSetApi;
        m_offscreenImageApi = offscreenImageApi;
        m_queueSubmitter = queueSubmitter;
        m_storageBufferFactory = storageBufferFactory;
    }

    /// <summary>Uploads a CPU-pixel surface and returns the handle of a shader-readable image view over it.</summary>
    /// <param name="deviceContext">The device the image is created and uploaded on.</param>
    /// <param name="surface">The CPU-pixel surface to upload; its <see cref="Surface.Pixels"/> must be tightly packed.</param>
    /// <returns>The native <c>VkImageView</c> handle to sample the uploaded image through.</returns>
    /// <exception cref="ArgumentException"><paramref name="surface"/> is not a CPU-pixel surface.</exception>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public nint Upload(IVulkanDeviceContext deviceContext, Surface surface) {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (!surface.IsCpuPixels) {
            throw new ArgumentException(
                message: "The surface does not carry CPU pixels.",
                paramName: nameof(surface)
            );
        }

        EnsureResources(
            deviceContext: deviceContext,
            surface: surface
        );

        m_stagingBuffer!.Write<byte>(data: surface.Pixels.Span);

        var device = m_device!;
        var commandBufferHandle = m_commandResources!.CommandBufferHandles[0];

        m_commandBufferRecordingApi.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: device.Handle
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        m_commandBufferRecordingApi.TransitionImageLayout(
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.TransferWrite,
            destinationStageMask: VulkanPipelineStageFlags.Transfer,
            deviceHandle: device.Handle,
            imageHandle: m_imageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.TransferDestinationOptimal,
            oldLayout: VulkanImageLayout.Undefined,
            sourceAccessMask: 0,
            sourceStageMask: VulkanPipelineStageFlags.TopOfPipe
        );
        m_commandBufferRecordingApi.CopyBufferToImage(
            bufferHandle: m_stagingBuffer.BufferHandle,
            commandBufferHandle: commandBufferHandle,
            deviceHandle: device.Handle,
            height: m_height,
            imageHandle: m_imageHandle,
            imageLayout: VulkanImageLayout.TransferDestinationOptimal,
            imageOffsetX: 0,
            imageOffsetY: 0,
            width: m_width
        );
        m_commandBufferRecordingApi.TransitionImageLayout(
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.ShaderRead,
            destinationStageMask: VulkanPipelineStageFlags.FragmentShader,
            deviceHandle: device.Handle,
            imageHandle: m_imageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            oldLayout: VulkanImageLayout.TransferDestinationOptimal,
            sourceAccessMask: VulkanAccessFlags.TransferWrite,
            sourceStageMask: VulkanPipelineStageFlags.Transfer
        );
        m_commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: device.Handle
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");

        Span<nint> commandBuffers = [commandBufferHandle];

        m_queueSubmitter.SubmitAndWait(
            commandBufferHandles: commandBuffers,
            deviceHandle: device.Handle,
            graphicsQueue: device.GraphicsQueue
        );

        return m_imageViewHandle;
    }

    private static uint ToVulkanFormat(SurfaceFormat format) {
        return format switch {
            SurfaceFormat.B8G8R8A8Unorm => VulkanFormat.B8G8R8A8Unorm,
            SurfaceFormat.R8G8B8A8Unorm => VulkanFormat.R8G8B8A8Unorm,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: format,
                message: "The CPU-pixel surface format has no Vulkan mapping.",
                paramName: nameof(format)
            ),
        };
    }
    private void EnsureResources(IVulkanDeviceContext deviceContext, Surface surface) {
        var device = deviceContext.LogicalDevice;

        if (
            (0 != m_imageViewHandle) &&
            (m_device is not null) &&
            (m_device.Handle == device.Handle) &&
            (m_width == surface.Width) &&
            (m_height == surface.Height) &&
            (m_format == surface.Format)
        ) {
            return;
        }

        DisposeResources();

        var instance = deviceContext.Instance;
        var vulkanFormat = ToVulkanFormat(format: surface.Format);
        var image = m_offscreenImageApi.CreateColorImage(request: new VulkanOffscreenImageCreateRequest(
            DeviceHandle: device.Handle,
            Format: vulkanFormat,
            Height: surface.Height,
            InstanceHandle: instance.Handle,
            PhysicalDeviceHandle: device.PhysicalDevice.Handle,
            UsageFlags: VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferDestination,
            Width: surface.Width
        ));

        m_imageHandle = image.ImageHandle;
        m_memoryHandle = image.MemoryHandle;

        m_framebufferSetApi.CreateImageView(
            imageViewHandle: out m_imageViewHandle,
            request: new VulkanImageViewCreateRequest(
                DeviceHandle: device.Handle,
                Format: vulkanFormat,
                ImageHandle: m_imageHandle
            )
        ).ThrowIfFailed(operation: "vkCreateImageView");

        m_commandResources = m_commandResourcesFactory.Create(
            commandBufferCount: 1,
            logicalDevice: device
        );
        m_device = device;
        m_format = surface.Format;
        m_height = surface.Height;
        m_stagingBuffer = m_storageBufferFactory.Create(
            logicalDevice: device,
            sizeBytes: (ulong)surface.Pixels.Length,
            vulkanInstance: instance
        );
        m_width = surface.Width;
    }
    private void DisposeResources() {
        var device = m_device;

        m_commandResources?.Dispose();
        m_commandResources = null;
        m_stagingBuffer?.Dispose();
        m_stagingBuffer = null;

        if (
            (device is not null) &&
            (0 != m_imageViewHandle)
        ) {
            m_framebufferSetApi.DestroyImageView(
                deviceHandle: device.Handle,
                imageViewHandle: m_imageViewHandle
            );
        }

        m_imageViewHandle = 0;

        if (device is not null) {
            m_offscreenImageApi.DestroyColorImage(
                deviceHandle: device.Handle,
                imageHandle: m_imageHandle,
                memoryHandle: m_memoryHandle
            );
        }

        m_imageHandle = 0;
        m_memoryHandle = 0;
    }

    /// <summary>Waits for device idle, then frees the staging buffer, image, view, and command resources. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_device?.WaitIdle();
        DisposeResources();
    }
}
