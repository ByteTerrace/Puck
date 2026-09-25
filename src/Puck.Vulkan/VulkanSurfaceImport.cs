using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Samples a shared-handle surface — a texture another backend (Direct3D 12) rendered into shared
/// GPU memory — without any CPU round-trip: it imports the shared NT handle into a Vulkan image bound to the
/// same device memory, then hands back a shader-readable view. The image is imported once (the handle is stable
/// across frames) and transitioned to the shader-read-only layout. This is the zero-copy alternative to
/// <see cref="VulkanSurfaceUpload"/>. It imports no fence or semaphore, so nothing orders the producer's writes
/// against a Vulkan read on the GPU: today's producers finish each write with a CPU wait on their own device before
/// publishing it, and a consumer holds the written slot through a CPU lease until its submission retires. Rendering
/// plan P12b-4 adds a shared fence that Vulkan waits on as a timeline semaphore.
/// </summary>
public sealed class VulkanSurfaceImport : IDisposable {
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly IVulkanDeviceContext m_deviceContext;
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

    /// <summary>Gets the native <c>VkImage</c> handle of the current import, or zero before the first successful
    /// <see cref="Import"/>.</summary>
    public nint ImageHandle => m_imageHandle;

    /// <summary>Initializes a shared-surface importer on a device context.</summary>
    /// <param name="deviceContext">The device context whose device the image is imported on; it must share the
    /// producer's adapter.</param>
    /// <param name="externalMemoryApi">The external-memory API used to import the shared allocation.</param>
    /// <param name="framebufferSetApi">The API used to create and destroy the imported image view.</param>
    /// <param name="commandResourcesFactory">The factory for transition command resources.</param>
    /// <param name="commandBufferRecordingApi">The API used to record image transitions.</param>
    /// <param name="queueSubmitter">The queue submission service.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public VulkanSurfaceImport(
        IVulkanDeviceContext deviceContext,
        IVulkanExternalMemoryApi externalMemoryApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        VulkanQueueSubmitter queueSubmitter
    ) {
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(externalMemoryApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(queueSubmitter);

        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_deviceContext = deviceContext;
        m_externalMemoryApi = externalMemoryApi;
        m_framebufferSetApi = framebufferSetApi;
        m_queueSubmitter = queueSubmitter;
    }

    private void DisposeResources() {
        if (m_device is not { } device) {
            return;
        }

        VulkanDeviceOwnership.ThrowIfDestroyed(
            held: device,
            holder: nameof(VulkanSurfaceImport)
        );
        m_commandResources?.Dispose();
        m_commandResources = null;
        m_framebufferSetApi.DestroyImageView(
            device: device.Commands,
            imageViewHandle: m_imageViewHandle
        );
        m_externalMemoryApi.DestroyImage(
            device: device.Commands,
            imageHandle: m_imageHandle,
            memoryHandle: m_memoryHandle
        );
        m_imageViewHandle = 0;
        m_imageHandle = 0;
        m_memoryHandle = 0;
        m_sharedHandle = 0;
    }
    // The shared image is produced by Direct3D 12 (which has no Vulkan layout). Bring it into the shader-read
    // layout once; the producer's per-frame writes land in the same memory, ordered only by the producer's CPU wait
    // before it publishes a write and the consumer's CPU lease on what it samples (see the type's remarks).
    private void TransitionToShaderReadable(VulkanLogicalDevice device) {
        var commandBufferHandle = m_commandResources!.CommandBufferHandles[0];

        m_commandBufferRecordingApi.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: device.Commands
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        m_commandBufferRecordingApi.TransitionImageLayout(
            aspectMask: VulkanGpuFormats.ColorAspect,
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.ShaderRead,
            destinationStageMask: VulkanPipelineStageFlags.FragmentShader,
            device: device.Commands,
            imageHandle: m_imageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            oldLayout: VulkanImageLayout.Undefined,
            sourceAccessMask: 0,
            sourceStageMask: VulkanPipelineStageFlags.TopOfPipe
        );
        m_commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: device.Commands
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");

        Span<nint> commandBuffers = [commandBufferHandle];

        m_queueSubmitter.SubmitAndWait(
            commandBufferHandles: commandBuffers,
            device: device.Commands,
            graphicsQueue: device.GraphicsQueue
        );
    }

    /// <summary>Waits for device idle, then frees the image view, imported image, and imported memory. Safe to call more than once.</summary>
    /// <exception cref="InvalidOperationException">The device was destroyed first, so these resources can no longer be
    /// destroyed and the owner's teardown order is wrong; the import stays undisposed.</exception>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_device?.TryWaitIdle();
        DisposeResources();
        m_disposed = true;
    }
    /// <summary>Imports the shared surface (once) and returns the handle of a shader-readable image view over it.</summary>
    /// <param name="sharedHandle">The shared NT handle of the texture to import.</param>
    /// <param name="width">The width, in pixels, of the shared texture.</param>
    /// <param name="height">The height, in pixels, of the shared texture.</param>
    /// <param name="vulkanFormat">The <c>VkFormat</c> of the shared texture.</param>
    /// <returns>The native <c>VkImageView</c> handle to sample the imported image through.</returns>
    /// <exception cref="ArgumentException"><paramref name="sharedHandle"/> is zero.</exception>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The context's device is not the one an earlier import created this
    /// instance's resources on.</exception>
    public nint Import(nint sharedHandle, uint width, uint height, uint vulkanFormat) {
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

        var device = m_deviceContext.LogicalDevice;

        VulkanDeviceOwnership.ThrowIfOtherDevice(
            held: m_device,
            holder: nameof(VulkanSurfaceImport),
            offered: device
        );

        if (
            (0 != m_imageViewHandle) &&
            (m_sharedHandle == sharedHandle) &&
            (m_width == width) &&
            (m_height == height) &&
            (m_format == vulkanFormat)
        ) {
            return m_imageViewHandle;
        }

        DisposeResources();

        // Everything below is made on this device, and DisposeResources releases exactly what was made, so a failure
        // part way (a refused view after its image exists, say) leaks nothing. The cached identity is recorded only
        // once the image is shader-readable, so a failed import is retried rather than returned.
        m_device = device;

        try {
            var imported = m_externalMemoryApi.ImportImage(request: new VulkanExternalImageImportRequest(
                Device: device.Commands,
                Format: vulkanFormat,
                Height: height,
                Instance: m_deviceContext.Instance.Commands,
                PhysicalDeviceHandle: device.PhysicalDevice.Handle,
                SharedHandle: sharedHandle,
                Width: width
            ));

            m_imageHandle = imported.ImageHandle;
            m_memoryHandle = imported.MemoryHandle;

            m_framebufferSetApi.CreateImageView(
                imageViewHandle: out var imageViewHandle,
                request: new VulkanImageViewCreateRequest(
                    Device: device.Commands,
                    Format: vulkanFormat,
                    ImageHandle: m_imageHandle
                )
            ).ThrowIfFailed(operation: "vkCreateImageView");
            m_imageViewHandle = imageViewHandle;

            m_commandResources = m_commandResourcesFactory.Create(
                commandBufferCount: 1,
                logicalDevice: device
            );

            TransitionToShaderReadable(device: device);
        } catch {
            DisposeResources();

            throw;
        }

        m_format = vulkanFormat;
        m_height = height;
        m_sharedHandle = sharedHandle;
        m_width = width;

        return m_imageViewHandle;
    }
}
