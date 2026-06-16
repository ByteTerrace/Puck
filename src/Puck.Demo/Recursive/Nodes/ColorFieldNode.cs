using Puck.Compositing;
using Puck.Hosting;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Recursive.Nodes;

/// <summary>
/// A minimal, SDF-independent render node: it clears its own offscreen surface to an animated color and
/// hands it up. It exists to prove the recursive seam — the SDF engine composites this node's surface into
/// a viewport slot without referencing its type, exactly as a host composites any child's pixels. Uses a
/// transfer clear (no graphics pipeline), submit-and-waits, and leaves the image shader-readable.
/// </summary>
internal sealed class ColorFieldNode : IRenderNode {
    private const uint OutputFormat = VulkanFormat.B8G8R8A8Unorm;

    private readonly IVulkanCommandBufferRecordingApi m_commandBufferRecordingApi;
    private readonly IVulkanCommandResourcesFactory m_commandResourcesFactory;
    private readonly NodeDescriptor m_descriptor;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly IVulkanOffscreenImageApi m_offscreenImageApi;
    private readonly VulkanQueueSubmitter m_queueSubmitter;
    private VulkanCommandResources? m_commandResources;
    private bool m_disposed;
    private uint m_height;
    private nint m_imageHandle;
    private nint m_imageViewHandle;
    private VulkanLogicalDevice? m_logicalDevice;
    private nint m_memoryHandle;
    private uint m_width;

    public ColorFieldNode(
        IVulkanOffscreenImageApi offscreenImageApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        VulkanQueueSubmitter queueSubmitter
    ) {
        ArgumentNullException.ThrowIfNull(commandBufferRecordingApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(offscreenImageApi);
        ArgumentNullException.ThrowIfNull(queueSubmitter);

        m_commandBufferRecordingApi = commandBufferRecordingApi;
        m_commandResourcesFactory = commandResourcesFactory;
        m_descriptor = new NodeDescriptor(
            Name: "color-field",
            SurfaceId: SurfaceId.New()
        );
        m_framebufferSetApi = framebufferSetApi;
        m_offscreenImageApi = offscreenImageApi;
        m_queueSubmitter = queueSubmitter;
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc />
    public Surface ProduceFrame(in FrameContext context) {
        if (
            m_disposed ||
            (0 == context.TargetWidth) ||
            (0 == context.TargetHeight)
        ) {
            return default;
        }

        if (!context.Host.TryResolveCapability<IVulkanDeviceContext>(capability: out var deviceContext)) {
            return default;
        }

        EnsureImage(
            deviceContext: deviceContext,
            height: context.TargetHeight,
            width: context.TargetWidth
        );

        var device = m_logicalDevice!;
        var deviceHandle = device.Handle;
        var commandBufferHandle = m_commandResources!.CommandBufferHandles[0];
        var time = (float)context.RenderSeconds;

        m_commandBufferRecordingApi.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        ).ThrowIfFailed(operation: "vkBeginCommandBuffer");
        m_commandBufferRecordingApi.TransitionImageLayout(
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.TransferWrite,
            destinationStageMask: VulkanPipelineStageFlags.Transfer,
            deviceHandle: deviceHandle,
            imageHandle: m_imageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.TransferDestinationOptimal,
            oldLayout: VulkanImageLayout.Undefined,
            sourceAccessMask: 0,
            sourceStageMask: VulkanPipelineStageFlags.TopOfPipe
        );
        m_commandBufferRecordingApi.ClearColorImage(
            alpha: 1f,
            blue: (0.5f + (0.5f * MathF.Sin(x: (time + 4.188f)))),
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            green: (0.5f + (0.5f * MathF.Sin(x: (time + 2.094f)))),
            imageHandle: m_imageHandle,
            imageLayout: VulkanImageLayout.TransferDestinationOptimal,
            red: (0.5f + (0.5f * MathF.Sin(x: time)))
        );
        m_commandBufferRecordingApi.TransitionImageLayout(
            baseMipLevel: 0,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: VulkanAccessFlags.ShaderRead,
            destinationStageMask: VulkanPipelineStageFlags.FragmentShader,
            deviceHandle: deviceHandle,
            imageHandle: m_imageHandle,
            mipLevelCount: 1,
            newLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            oldLayout: VulkanImageLayout.TransferDestinationOptimal,
            sourceAccessMask: VulkanAccessFlags.TransferWrite,
            sourceStageMask: VulkanPipelineStageFlags.Transfer
        );
        m_commandBufferRecordingApi.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        ).ThrowIfFailed(operation: "vkEndCommandBuffer");

        Span<nint> commandBuffers = [commandBufferHandle];

        m_queueSubmitter.SubmitAndWait(
            commandBufferHandles: commandBuffers,
            deviceHandle: deviceHandle,
            graphicsQueue: device.GraphicsQueue
        );

        return new Surface(
            Format: SurfaceFormat.B8G8R8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_imageViewHandle,
            Width: m_width
        );
    }

    private void EnsureImage(IVulkanDeviceContext deviceContext, uint width, uint height) {
        var logicalDevice = deviceContext.LogicalDevice;

        if (
            (0 != m_imageHandle) &&
            (m_width == width) &&
            (m_height == height) &&
            (m_logicalDevice is not null) &&
            (m_logicalDevice.Handle == logicalDevice.Handle)
        ) {
            return;
        }

        DisposeImage();

        m_logicalDevice = logicalDevice;

        var image = m_offscreenImageApi.CreateColorImage(request: new VulkanOffscreenImageCreateRequest(
            DeviceHandle: logicalDevice.Handle,
            Format: OutputFormat,
            Height: height,
            InstanceHandle: deviceContext.Instance.Handle,
            PhysicalDeviceHandle: deviceContext.PhysicalDevice.Handle,
            UsageFlags: VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferDestination,
            Width: width
        ));

        m_imageHandle = image.ImageHandle;
        m_memoryHandle = image.MemoryHandle;

        m_framebufferSetApi.CreateImageView(
            imageViewHandle: out m_imageViewHandle,
            request: new VulkanImageViewCreateRequest(
                DeviceHandle: logicalDevice.Handle,
                Format: OutputFormat,
                ImageHandle: m_imageHandle
            )
        ).ThrowIfFailed(operation: "vkCreateImageView");

        m_commandResources = m_commandResourcesFactory.Create(
            commandBufferCount: 1,
            logicalDevice: logicalDevice
        );
        m_height = height;
        m_width = width;
    }
    private void DisposeImage() {
        var device = m_logicalDevice;

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
            m_offscreenImageApi.DestroyColorImage(
                deviceHandle: device.Handle,
                imageHandle: m_imageHandle,
                memoryHandle: m_memoryHandle
            );
        }

        m_imageHandle = 0;
        m_memoryHandle = 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_logicalDevice?.WaitIdle();
        DisposeImage();
    }
}
