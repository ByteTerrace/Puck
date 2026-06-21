using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Samples a shared-handle surface — a texture another backend (Direct3D 12) rendered into shared
/// GPU memory — without any CPU round-trip: it imports the shared NT handle into a Vulkan image bound to the
/// same device memory, then hands back a shader-readable view. The image is imported once (the handle is stable
/// across frames) and transitioned to the shader-read-only layout. This is the zero-copy alternative to
/// <see cref="VulkanSurfaceUpload"/>; producer/consumer are ordered by the producer's GPU fence.
/// </summary>
public sealed class VulkanSurfaceImport : IDisposable {
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly IVulkanExternalMemoryApi m_externalMemoryApi;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly VulkanQueueSubmitter m_queueSubmitter;
    private VulkanCommandResources? m_commandResources;
    private VulkanLogicalDevice? m_device;
    private bool m_disposed;
    private uint m_format;
    private uint m_height;
    private nint m_imageHandle;
    private nint m_imageViewHandle;
    private nint m_memoryHandle;
    private nint m_sharedHandle;
    private uint m_width;

    public VulkanSurfaceImport(
        IVulkanExternalMemoryApi externalMemoryApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        VulkanQueueSubmitter queueSubmitter
    ) {
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(externalMemoryApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(queueSubmitter);

        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_externalMemoryApi = externalMemoryApi;
        m_framebufferSetApi = framebufferSetApi;
        m_queueSubmitter = queueSubmitter;
    }

    /// <summary>Imports the shared surface (once) and returns the handle of a shader-readable image view over it.</summary>
    /// <param name="deviceContext">The device the image is imported on; must share the producer's adapter.</param>
    /// <param name="sharedHandle">The shared NT handle of the texture to import.</param>
    /// <param name="width">The width, in pixels, of the shared texture.</param>
    /// <param name="height">The height, in pixels, of the shared texture.</param>
    /// <param name="vulkanFormat">The <c>VkFormat</c> of the shared texture.</param>
    /// <returns>The native <c>VkImageView</c> handle to sample the imported image through.</returns>
    /// <exception cref="ArgumentException"><paramref name="sharedHandle"/> is zero.</exception>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public nint Import(IVulkanDeviceContext deviceContext, nint sharedHandle, uint width, uint height, uint vulkanFormat) {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (0 == sharedHandle) {
            throw new ArgumentException(
                message: "A non-zero shared handle is required.",
                paramName: nameof(sharedHandle)
            );
        }

        var device = deviceContext.LogicalDevice;

        if (
            (0 != m_imageViewHandle) &&
            (m_device is not null) &&
            (m_device.Handle == device.Handle) &&
            (m_sharedHandle == sharedHandle) &&
            (m_width == width) &&
            (m_height == height) &&
            (m_format == vulkanFormat)
        ) {
            return m_imageViewHandle;
        }

        DisposeResources();

        var instance = deviceContext.Instance;
        var imported = m_externalMemoryApi.ImportImage(request: new VulkanExternalImageImportRequest(
            DeviceHandle: device.Handle,
            Format: vulkanFormat,
            Height: height,
            InstanceHandle: instance.Handle,
            PhysicalDeviceHandle: device.PhysicalDevice.Handle,
            SharedHandle: sharedHandle,
            Width: width
        ));

        m_imageHandle = imported.ImageHandle;
        m_memoryHandle = imported.MemoryHandle;

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
        m_format = vulkanFormat;
        m_height = height;
        m_sharedHandle = sharedHandle;
        m_width = width;

        TransitionToShaderReadable(device: device);

        return m_imageViewHandle;
    }


    // The shared image is produced by Direct3D 12 (which has no Vulkan layout). Bring it into the shader-read
    // layout once; the producer's per-frame writes land in the same memory and are ordered by its GPU fence.
    private void TransitionToShaderReadable(VulkanLogicalDevice device) {
        var commandBufferHandle = m_commandResources!.CommandBufferHandles[0];

        m_commandBufferRecordingApi.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: device.Handle
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        m_commandBufferRecordingApi.TransitionImageLayout(
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.ShaderRead,
            destinationStageMask: VulkanPipelineStageFlags.FragmentShader,
            deviceHandle: device.Handle,
            imageHandle: m_imageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            oldLayout: VulkanImageLayout.Undefined,
            sourceAccessMask: 0,
            sourceStageMask: VulkanPipelineStageFlags.TopOfPipe
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
    }
    private void DisposeResources() {
        var device = m_device;

        m_commandResources?.Dispose();
        m_commandResources = null;

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
            m_externalMemoryApi.DestroyImage(
                deviceHandle: device.Handle,
                imageHandle: m_imageHandle,
                memoryHandle: m_memoryHandle
            );
        }

        m_imageHandle = 0;
        m_memoryHandle = 0;
        m_sharedHandle = 0;
    }

    /// <summary>Waits for device idle, then frees the image view, imported image, and imported memory. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_device?.WaitIdle();
        DisposeResources();
    }
}
