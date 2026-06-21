using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Reads a Vulkan image back into a CPU-pixel surface so a producer can hand its result to a
/// host on another device (or process). It owns a host-visible readback buffer and the command resources to
/// drive the copy, rebuilding them when the device or the source extent/format changes. Each <see cref="Read"/>
/// copies the source image into the buffer and returns the tightly packed pixels as a CPU-pixel surface — the
/// exact inverse of <see cref="VulkanSurfaceUpload"/>, and the producer (egress) half of the CPU-pixel
/// transport, reusable by any Vulkan node whose pixels must cross a device boundary.
/// <para>
/// Asymmetry note: <see cref="VulkanSurfaceUpload"/> takes a surface (which carries its own
/// extent/format) and creates the image it owns; this block does not own the image it reads, and the GPU
/// surface variant exposes only a view handle, so the caller passes the source <c>VkImage</c> plus its extent
/// and format explicitly. The source must be in the shader-read-only layout and is left in it.
/// </para>
/// </summary>
public sealed class VulkanSurfaceReadback : IDisposable {
    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly IVulkanFrameReadbackApi m_frameReadbackApi;
    private readonly VulkanQueueSubmitter m_queueSubmitter;
    private VulkanCommandResources? m_commandResources;
    private VulkanLogicalDevice? m_device;
    private bool m_disposed;
    private uint m_bytesPerPixel;
    private uint m_format;
    private uint m_height;
    private VulkanFrameReadbackBuffer? m_readbackBuffer;
    private uint m_width;

    public VulkanSurfaceReadback(
        IVulkanFrameReadbackApi frameReadbackApi,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        VulkanQueueSubmitter queueSubmitter
    ) {
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(frameReadbackApi);
        ArgumentNullException.ThrowIfNull(queueSubmitter);

        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_frameReadbackApi = frameReadbackApi;
        m_queueSubmitter = queueSubmitter;
    }

    /// <summary>Reads a shader-readable color image back into tightly packed CPU pixels.</summary>
    /// <param name="deviceContext">The device the source image lives on.</param>
    /// <param name="sourceImageHandle">The native <c>VkImage</c> handle to read; must be in the shader-read-only layout.</param>
    /// <param name="width">The width, in pixels, of the source image.</param>
    /// <param name="height">The height, in pixels, of the source image.</param>
    /// <param name="vulkanFormat">The <c>VkFormat</c> of the source image.</param>
    /// <param name="bytesPerPixel">The number of bytes per pixel for the given format.</param>
    /// <returns>The tightly packed pixel data read back from the image.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourceImageHandle"/> is zero, or a dimension is zero.</exception>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public ReadOnlyMemory<byte> Read(IVulkanDeviceContext deviceContext, nint sourceImageHandle, uint width, uint height, uint vulkanFormat, uint bytesPerPixel) {
        ArgumentNullException.ThrowIfNull(deviceContext);
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
            deviceContext: deviceContext,
            height: height,
            vulkanFormat: vulkanFormat,
            width: width
        );

        var device = m_device!;
        var commandBufferHandle = m_commandResources!.CommandBufferHandles[0];

        m_commandBufferRecordingApi.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: device.Handle
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        m_commandBufferRecordingApi.TransitionImageLayout(
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.TransferRead,
            destinationStageMask: VulkanPipelineStageFlags.Transfer,
            deviceHandle: device.Handle,
            imageHandle: sourceImageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.TransferSourceOptimal,
            oldLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            sourceAccessMask: VulkanAccessFlags.ShaderRead,
            sourceStageMask: VulkanPipelineStageFlags.FragmentShader
        );
        m_commandBufferRecordingApi.CopyImageToBuffer(
            bufferHandle: m_readbackBuffer!.BufferHandle,
            commandBufferHandle: commandBufferHandle,
            deviceHandle: device.Handle,
            height: m_height,
            imageHandle: sourceImageHandle,
            imageLayout: VulkanImageLayout.TransferSourceOptimal,
            width: m_width
        );
        m_commandBufferRecordingApi.TransitionImageLayout(
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.ShaderRead,
            destinationStageMask: VulkanPipelineStageFlags.FragmentShader,
            deviceHandle: device.Handle,
            imageHandle: sourceImageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            oldLayout: VulkanImageLayout.TransferSourceOptimal,
            sourceAccessMask: VulkanAccessFlags.TransferRead,
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

        return m_frameReadbackApi.ReadBuffer(buffer: m_readbackBuffer);
    }

    private void EnsureResources(IVulkanDeviceContext deviceContext, uint width, uint height, uint vulkanFormat, uint bytesPerPixel) {
        var device = deviceContext.LogicalDevice;

        if (
            (m_readbackBuffer is not null) &&
            (m_device is not null) &&
            (m_device.Handle == device.Handle) &&
            (m_width == width) &&
            (m_height == height) &&
            (m_format == vulkanFormat)
        ) {
            return;
        }

        DisposeResources();

        var instance = deviceContext.Instance;

        m_commandResources = m_commandResourcesFactory.Create(
            commandBufferCount: 1,
            logicalDevice: device
        );
        m_bytesPerPixel = bytesPerPixel;
        m_device = device;
        m_format = vulkanFormat;
        m_height = height;
        m_readbackBuffer = m_frameReadbackApi.CreateBuffer(request: new VulkanFrameReadbackBufferCreateRequest(
            DeviceHandle: device.Handle,
            InstanceHandle: instance.Handle,
            PhysicalDeviceHandle: device.PhysicalDevice.Handle,
            SizeBytes: (((ulong)width * height) * bytesPerPixel)
        ));
        m_width = width;
    }
    private void DisposeResources() {
        m_commandResources?.Dispose();
        m_commandResources = null;
        m_readbackBuffer?.Dispose();
        m_readbackBuffer = null;
    }

    /// <summary>Waits for device idle, then frees the readback buffer and command resources. Safe to call more than once.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_device?.WaitIdle();
        DisposeResources();
    }
}
