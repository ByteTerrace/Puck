using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// Reads a Vulkan image back into a CPU-pixel surface so a producer can hand its result to a
/// host on another device (or process). It owns a host-visible readback buffer and the command resources to
/// drive the copy on the device of its first read, and rebuilds them when the source extent or format changes. It
/// never moves to another device (<see cref="VulkanDeviceOwnership"/>): its owner releases it before that device goes,
/// a device loss included, and creates a new one on the replacement, so a readback whose context now holds a different
/// device refuses it. Each <see cref="Read"/> copies the source image into the buffer and returns the tightly packed
/// pixels as a CPU-pixel surface — the exact inverse of <see cref="VulkanSurfaceUpload"/>, and the producer (egress)
/// half of the CPU-pixel transport, reusable by any Vulkan node whose pixels must cross a device boundary.
/// <para>
/// Asymmetry note: <see cref="VulkanSurfaceUpload"/> takes a surface (which carries its own
/// extent/format) and creates the image it owns; this block does not own the image it reads, and the GPU
/// surface variant exposes only a view handle, so the caller passes the source <c>VkImage</c> plus its extent,
/// format, and current layout explicitly. The source is restored to that layout after the copy.
/// </para>
/// </summary>
public sealed class VulkanSurfaceReadback : IDisposable {
    private readonly IVulkanBufferApi m_bufferApi;
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly IVulkanDeviceContext m_deviceContext;
    private readonly VulkanQueueSubmitter m_queueSubmitter;

    private VulkanCommandResources? m_commandResources;
    private VulkanLogicalDevice? m_device;
    private bool m_disposed;
    private uint m_format;
    private uint m_height;
    private VulkanBuffer? m_readbackBuffer;
    private uint m_width;

    /// <summary>Initializes a reusable image readback service on a device context.</summary>
    /// <param name="deviceContext">The device context whose device the source images live on.</param>
    /// <param name="bufferApi">The API that makes the host-coherent readback buffer.</param>
    /// <param name="commandResourcesFactory">The factory for copy command resources.</param>
    /// <param name="commandBufferRecordingApi">The API used to record image-to-buffer copies.</param>
    /// <param name="queueSubmitter">The queue submission service.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public VulkanSurfaceReadback(
        IVulkanDeviceContext deviceContext,
        IVulkanBufferApi bufferApi,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        VulkanQueueSubmitter queueSubmitter
    ) {
        ArgumentNullException.ThrowIfNull(bufferApi);
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(queueSubmitter);

        m_bufferApi = bufferApi;
        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_deviceContext = deviceContext;
        m_queueSubmitter = queueSubmitter;
    }

    private void DisposeResources() {
        if (m_device is not { } device) {
            return;
        }

        VulkanDeviceOwnership.ThrowIfDestroyed(
            held: device,
            holder: nameof(VulkanSurfaceReadback)
        );
        m_commandResources?.Dispose();
        m_commandResources = null;
        m_readbackBuffer?.Dispose();
        m_readbackBuffer = null;
    }
    private void EnsureResources(uint width, uint height, uint vulkanFormat, uint bytesPerPixel) {
        var device = m_deviceContext.LogicalDevice;

        VulkanDeviceOwnership.ThrowIfOtherDevice(
            held: m_device,
            holder: nameof(VulkanSurfaceReadback),
            offered: device
        );

        if (
            (m_readbackBuffer is not null) &&
            (m_width == width) &&
            (m_height == height) &&
            (m_format == vulkanFormat)
        ) {
            return;
        }

        DisposeResources();

        // The resources below are made on this device; a failure part way releases what was made through
        // DisposeResources rather than leaving it unreachable.
        m_device = device;

        try {
            m_commandResources = m_commandResourcesFactory.Create(
                commandBufferCount: 1,
                logicalDevice: device
            );
            m_readbackBuffer = VulkanBuffer.Create(
                bufferApi: m_bufferApi,
                device: m_deviceContext,
                memory: VulkanBufferMemory.HostCoherent,
                sizeBytes: ((((ulong)width) * height) * bytesPerPixel),
                usage: VulkanBufferUsageFlags.TransferDestination
            );
        } catch {
            DisposeResources();

            throw;
        }

        m_format = vulkanFormat;
        m_height = height;
        m_width = width;
    }
    // The source-state tuple must match the descriptor/producer contract; a mismatched old layout is undefined
    // behavior on Vulkan and a mismatched resource state is undefined behavior on Direct3D 12.
    private void RecordReadback(nint commandBufferHandle, nint sourceImageHandle, GpuImageLayout sourceLayout) {
        var device = m_device!;

        var (vulkanSourceLayout, sourceAccessMask, sourceStageMask) = sourceLayout switch {
            // The producer-side External handoff is still VkImageLayout.GENERAL; unlike a working General storage
            // image, however, Record left it scoped for the completed producer write's read-only handoff.
            GpuImageLayout.External => (
                VulkanImageLayout.General,
                VulkanAccessFlags.ShaderRead,
                VulkanPipelineStageFlags.ComputeShader
            ),
            GpuImageLayout.General => (
                VulkanImageLayout.General,
                VulkanAccessFlags.ShaderRead | VulkanAccessFlags.ShaderWrite,
                VulkanPipelineStageFlags.ComputeShader
            ),
            GpuImageLayout.ShaderReadOnly => (
                VulkanImageLayout.ShaderReadOnlyOptimal,
                VulkanAccessFlags.ShaderRead,
                VulkanPipelineStageFlags.FragmentShader
            ),
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(sourceLayout),
            actualValue: sourceLayout,
            message: "Readback requires an External, General, or ShaderReadOnly source image."
        ),
        };

        m_commandBufferRecordingApi.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: device.Commands
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        m_commandBufferRecordingApi.TransitionImageLayout(
            aspectMask: VulkanGpuFormats.ColorAspect,
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.TransferRead,
            destinationStageMask: VulkanPipelineStageFlags.Transfer,
            device: device.Commands,
            imageHandle: sourceImageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.TransferSourceOptimal,
            oldLayout: vulkanSourceLayout,
            sourceAccessMask: sourceAccessMask,
            sourceStageMask: sourceStageMask
        );
        m_commandBufferRecordingApi.CopyImageToBuffer(
            bufferHandle: m_readbackBuffer!.BufferHandle,
            commandBufferHandle: commandBufferHandle,
            device: device.Commands,
            height: m_height,
            imageHandle: sourceImageHandle,
            imageLayout: VulkanImageLayout.TransferSourceOptimal,
            width: m_width
        );
        m_commandBufferRecordingApi.TransitionImageLayout(
            aspectMask: VulkanGpuFormats.ColorAspect,
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: sourceAccessMask,
            destinationStageMask: sourceStageMask,
            device: device.Commands,
            imageHandle: sourceImageHandle,
            mipLevelCount: 1,
            newLayout: vulkanSourceLayout,
            oldLayout: VulkanImageLayout.TransferSourceOptimal,
            sourceAccessMask: VulkanAccessFlags.TransferRead,
            sourceStageMask: VulkanPipelineStageFlags.Transfer
        );
        m_commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            device: device.Commands
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");
    }

    /// <summary>Waits for device idle, then frees the readback buffer and command resources. Safe to call more than once.</summary>
    /// <exception cref="InvalidOperationException">The device was destroyed first, so these resources can no longer be
    /// destroyed and the owner's teardown order is wrong; the readback stays undisposed.</exception>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_device?.TryWaitIdle();
        DisposeResources();
        m_disposed = true;
    }
    /// <summary>Reads a color image back into tightly packed CPU pixels.</summary>
    /// <param name="sourceImageHandle">The native <c>VkImage</c> handle to read.</param>
    /// <param name="width">The width, in pixels, of the source image.</param>
    /// <param name="height">The height, in pixels, of the source image.</param>
    /// <param name="vulkanFormat">The <c>VkFormat</c> of the source image.</param>
    /// <param name="bytesPerPixel">The number of bytes per pixel for the given format.</param>
    /// <param name="sourceLayout">The image's current layout, restored after the copy.</param>
    /// <returns>The tightly packed pixel data read back from the image.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourceImageHandle"/> is zero, or a dimension is zero.</exception>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The context's device is not the one an earlier read created this
    /// instance's resources on.</exception>
    public ReadOnlyMemory<byte> Read(nint sourceImageHandle, uint width, uint height, uint vulkanFormat, uint bytesPerPixel, GpuImageLayout sourceLayout) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (0 == sourceImageHandle) {
            throw new ArgumentException(
                message: "A non-zero source image handle is required.",
                paramName: nameof(sourceImageHandle)
            );
        }

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "Source dimensions must be non-zero.");
        }

        EnsureResources(
            bytesPerPixel: bytesPerPixel,
            height: height,
            vulkanFormat: vulkanFormat,
            width: width
        );

        var device = m_device!;
        var commandBufferHandle = m_commandResources!.CommandBufferHandles[0];

        RecordReadback(
            commandBufferHandle: commandBufferHandle,
            sourceImageHandle: sourceImageHandle,
            sourceLayout: sourceLayout
        );

        Span<nint> commandBuffers = [commandBufferHandle];

        m_queueSubmitter.SubmitAndWait(
            commandBufferHandles: commandBuffers,
            device: device.Commands,
            graphicsQueue: device.GraphicsQueue
        );

        return m_readbackBuffer!.Read();
    }
}
