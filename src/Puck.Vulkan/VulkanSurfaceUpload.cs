using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Materializes CPU pixels onto a Vulkan device so a host can sample them like any other
/// image. It owns a host-visible staging buffer, a sampled image, and that image's view on the device of its first
/// upload, and rebuilds them when the extent or format changes. It never moves to another device: its owner releases
/// it before that device goes, a device loss included, and creates a new one on the replacement, so an upload handed a
/// different device refuses it. Each <see cref="Upload"/> writes the pixels
/// into the staging buffer, copies them into the image, leaves it shader-readable, and returns the image-view
/// handle. This is the generic counterpart to <see cref="VulkanGpuImage"/> for surfaces that crossed a
/// device boundary as host memory — the consumer half of the CPU-pixel transport, reusable by any Vulkan host.
/// <para>
/// With a <c>frameSynchronizationApi</c> supplied, <see cref="Upload"/> is PIPELINED: it waits only for its own
/// PREVIOUS copy's fence (protecting the reused staging buffer and command buffer), then submits fire-and-forget —
/// a same-queue consumer is still correct by queue order plus the recorded barriers, and a per-frame feed no longer
/// drains the whole queue behind a frame-ring host. Without it, the legacy blocking submit-and-wait applies.
/// </para>
/// </summary>
public sealed class VulkanSurfaceUpload : IDisposable {
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly IVulkanDeviceContext m_deviceContext;
    private readonly IVulkanFrameSynchronizationApi? m_frameSynchronizationApi;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly IVulkanOffscreenImageApi m_offscreenImageApi;
    private readonly IVulkanBufferApi m_bufferApi;
    private readonly VulkanQueueSubmitter m_queueSubmitter;

    private VulkanCommandResources? m_commandResources;
    private VulkanLogicalDevice? m_device;
    private bool m_disposed;
    private nint m_fence;
    private uint m_format;
    private uint m_height;
    private nint m_imageHandle;
    private nint m_imageViewHandle;
    private nint m_memoryHandle;
    private VulkanBuffer? m_stagingBuffer;
    private bool m_uploadPending;
    private uint m_width;

    /// <summary>Initializes a reusable CPU-pixel uploader on a device context.</summary>
    /// <param name="deviceContext">The device context whose device the image is created and uploaded on.</param>
    /// <param name="offscreenImageApi">The API used to create the sampled image.</param>
    /// <param name="framebufferSetApi">The API used to create and destroy its image view.</param>
    /// <param name="bufferApi">The API that makes the host-coherent staging buffer.</param>
    /// <param name="commandResourcesFactory">The factory for copy command resources.</param>
    /// <param name="commandBufferRecordingApi">The API used to record buffer-to-image copies.</param>
    /// <param name="queueSubmitter">The queue submission service.</param>
    /// <param name="frameSynchronizationApi">The optional API for pipelined upload synchronization.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public VulkanSurfaceUpload(
        IVulkanDeviceContext deviceContext,
        IVulkanOffscreenImageApi offscreenImageApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanBufferApi bufferApi,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        VulkanQueueSubmitter queueSubmitter,
        IVulkanFrameSynchronizationApi? frameSynchronizationApi = null
    ) {
        ArgumentNullException.ThrowIfNull(bufferApi);
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(offscreenImageApi);
        ArgumentNullException.ThrowIfNull(queueSubmitter);

        m_bufferApi = bufferApi;
        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_deviceContext = deviceContext;
        m_framebufferSetApi = framebufferSetApi;
        m_frameSynchronizationApi = frameSynchronizationApi;
        m_offscreenImageApi = offscreenImageApi;
        m_queueSubmitter = queueSubmitter;
    }

    private void DisposeResources() {
        if (m_device is not { } device) {
            return;
        }

        VulkanDeviceOwnership.ThrowIfDestroyed(
            held: device,
            holder: nameof(VulkanSurfaceUpload)
        );

        // The staging/command resources may still feed an outstanding pipelined copy — drain it first.
        WaitForPendingUpload();
        m_frameSynchronizationApi?.DestroyFence(
            device: device.Commands,
            fenceHandle: m_fence
        );
        m_fence = 0;
        m_commandResources?.Dispose();
        m_commandResources = null;
        m_stagingBuffer?.Dispose();
        m_stagingBuffer = null;
        m_framebufferSetApi.DestroyImageView(
            device: device.Commands,
            imageViewHandle: m_imageViewHandle
        );
        m_offscreenImageApi.DestroyColorImage(
            device: device.Commands,
            imageHandle: m_imageHandle,
            memoryHandle: m_memoryHandle
        );
        m_imageViewHandle = 0;
        m_imageHandle = 0;
        m_memoryHandle = 0;
    }
    private void EnsureResources(uint width, uint height, uint vulkanFormat) {
        var device = m_deviceContext.LogicalDevice;

        VulkanDeviceOwnership.ThrowIfOtherDevice(
            held: m_device,
            holder: nameof(VulkanSurfaceUpload),
            offered: device
        );

        if (
            (0 != m_imageViewHandle) &&
            (m_width == width) &&
            (m_height == height) &&
            (m_format == vulkanFormat)
        ) {
            return;
        }

        // A resize or format change destroys the image and view that in-flight GPU work — the SDF views kernel's
        // screen sampler, the presenter blit — may still be reading. WaitForPendingUpload (inside DisposeResources)
        // drains only this uploader's own copy fence, not those consumers, so idle the whole device first, exactly as
        // Dispose does. The null-conditional skips the first allocation, where nothing has been submitted yet.
        m_device?.TryWaitIdle();

        DisposeResources();

        // Everything below is made on this device, and DisposeResources releases exactly what was made, so a failure
        // part way (a refused view after its image exists, say) leaks nothing.
        m_device = device;

        try {
            var image = m_offscreenImageApi.CreateColorImage(request: new VulkanOffscreenImageCreateRequest(
                Device: device.Commands,
                Format: vulkanFormat,
                Height: height,
                Instance: m_deviceContext.Instance.Commands,
                PhysicalDeviceHandle: device.PhysicalDevice.Handle,
                UsageFlags: VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferDestination,
                Width: width
            ));

            m_imageHandle = image.ImageHandle;
            m_memoryHandle = image.MemoryHandle;

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
            m_stagingBuffer = VulkanBuffer.Create(
                bufferApi: m_bufferApi,
                device: m_deviceContext,
                memory: VulkanBufferMemory.HostCoherent,
                sizeBytes: checked((ulong)Surface.RequiredByteLength(
                    height: height,
                    width: width
                )),
                usage: VulkanBufferUsageFlags.Storage
            );

            // The pipelined path's completion fence (see the class remarks), rebuilt alongside the buffer on an extent
            // or format change. Absent (0) when no frame-synchronization API was supplied: the blocking submit applies.
            if (m_frameSynchronizationApi is not null) {
                m_frameSynchronizationApi.CreateFence(
                    fenceHandle: out m_fence,
                    request: new VulkanFrameSynchronizationCreateRequest(
                        Device: device.Commands,
                        StartSignaled: false
                    )
                ).ThrowIfFailed(operation: "vkCreateFence");
            }
        } catch {
            DisposeResources();

            throw;
        }

        m_format = vulkanFormat;
        m_height = height;
        m_width = width;
    }
    // Drains the pipelined path's outstanding copy (fence wait + reset); a no-op when none is outstanding. A lost
    // device has nothing left to wait on — clear the flag so teardown proceeds (mirroring TryWaitIdle's tolerance).
    private void WaitForPendingUpload() {
        if (
            !m_uploadPending ||
            (m_device is null) ||
            (0 == m_fence)
        ) {
            m_uploadPending = false;

            return;
        }

        var waitResult = m_frameSynchronizationApi!.WaitForFence(
            device: m_device.Commands,
            fenceHandle: m_fence,
            timeout: ulong.MaxValue
        );

        m_uploadPending = false;

        if (waitResult == Bindings.VkResult.ErrorDeviceLost) {
            return;
        }

        waitResult.ThrowIfFailed(operation: "vkWaitForFences");
        m_frameSynchronizationApi.ResetFence(
            device: m_device.Commands,
            fenceHandle: m_fence
        ).ThrowIfFailed(operation: "vkResetFences");
    }

    /// <summary>Waits for device idle, then frees the staging buffer, image, view, and command resources. Safe to call more than once.</summary>
    /// <exception cref="InvalidOperationException">The device was destroyed first, so these resources can no longer be
    /// destroyed and the owner's teardown order is wrong.</exception>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_device?.TryWaitIdle();
        DisposeResources();
        m_disposed = true;
    }
    /// <summary>Uploads a CPU-pixel surface and returns the handle of a shader-readable image view over it.</summary>
    /// <param name="pixels">The CPU-pixel data to upload; it must be tightly packed.</param>
    /// <param name="width">The width of the image.</param>
    /// <param name="height">The height of the image.</param>
    /// <param name="vulkanFormat">The Vulkan format of the image.</param>
    /// <returns>The native <c>VkImageView</c> handle to sample the uploaded image through.</returns>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is empty, or a dimension is zero.</exception>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The context's device is not the one an earlier upload created this
    /// instance's resources on.</exception>
    public nint Upload(ReadOnlyMemory<byte> pixels, uint width, uint height, uint vulkanFormat) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);
        var requiredByteLength = Surface.RequiredByteLength(
            height: height,
            width: width
        );

        if (pixels.Length != requiredByteLength) {
            throw new ArgumentException(
                message: $"The upload requires exactly {requiredByteLength} tightly packed bytes for its declared extent.",
                paramName: nameof(pixels)
            );
        }

        EnsureResources(
            height: height,
            vulkanFormat: vulkanFormat,
            width: width
        );

        // Pipelined mode: the previous copy must have retired before the staging buffer and command buffer are
        // reused. Uploads queue ahead of the frame that samples them, so by the next frame's upload the previous
        // one has long retired — this wait is ~free in the steady state.
        WaitForPendingUpload();

        m_stagingBuffer!.Write<byte>(data: pixels.Span);

        var device = m_device!;
        var commandBufferHandle = m_commandResources!.CommandBufferHandles[0];

        m_commandBufferRecordingApi.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: device.Commands
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        // The Undefined transition DISCARDS the prior contents (each upload rewrites the whole image), but its source
        // scope must still ORDER after the previous frame's samplers — with a pipelining host the prior frame may
        // still be reading this image on the queue when this copy is recorded (an execution-only dependency; no
        // access needed for the discard).
        m_commandBufferRecordingApi.TransitionImageLayout(
            aspectMask: VulkanGpuFormats.ColorAspect,
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.TransferWrite,
            destinationStageMask: VulkanPipelineStageFlags.Transfer,
            device: device.Commands,
            imageHandle: m_imageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.TransferDestinationOptimal,
            oldLayout: VulkanImageLayout.Undefined,
            sourceAccessMask: 0,
            sourceStageMask: VulkanPipelineStageFlags.ComputeShader | VulkanPipelineStageFlags.FragmentShader
        );
        m_commandBufferRecordingApi.CopyBufferToImage(
            bufferHandle: m_stagingBuffer.BufferHandle,
            commandBufferHandle: commandBufferHandle,
            device: device.Commands,
            height: m_height,
            imageHandle: m_imageHandle,
            imageLayout: VulkanImageLayout.TransferDestinationOptimal,
            imageOffsetX: 0,
            imageOffsetY: 0,
            width: m_width
        );
        // Visible to BOTH consumer stages — a compute sampler (the SDF views kernel's screen sources) and a
        // fragment sampler (the presenter blit path).
        m_commandBufferRecordingApi.TransitionImageLayout(
            aspectMask: VulkanGpuFormats.ColorAspect,
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.ShaderRead,
            destinationStageMask: VulkanPipelineStageFlags.ComputeShader | VulkanPipelineStageFlags.FragmentShader,
            device: device.Commands,
            imageHandle: m_imageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            oldLayout: VulkanImageLayout.TransferDestinationOptimal,
            sourceAccessMask: VulkanAccessFlags.TransferWrite,
            sourceStageMask: VulkanPipelineStageFlags.Transfer
        );
        m_commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: device.Commands
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");

        Span<nint> commandBuffers = [commandBufferHandle];

        if (0 != m_fence) {
            // Pipelined: fire-and-forget behind the fence. A same-queue consumer submitted AFTER this copy reads the
            // finished pixels by queue order + the transitions above; the fence only guards the staging/command
            // resources' reuse at the NEXT Upload.
            m_queueSubmitter.Submit(
                commandBufferHandles: commandBuffers,
                device: device.Commands,
                fenceHandle: m_fence,
                graphicsQueue: device.GraphicsQueue
            );
            m_uploadPending = true;
        } else {
            m_queueSubmitter.SubmitAndWait(
                commandBufferHandles: commandBuffers,
                device: device.Commands,
                graphicsQueue: device.GraphicsQueue
            );
        }

        return m_imageViewHandle;
    }
}
