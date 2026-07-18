using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>Owns an offscreen Vulkan color target and the resources needed to render into it.</summary>
public sealed class VulkanViewTarget : IGpuRenderTarget, IVulkanRenderTarget {
    private readonly VulkanCommandResources m_commandResources;
    private readonly nint m_deviceHandle;
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;
    private readonly nint m_framebufferHandle;
    private readonly nint m_imageHandle;
    private readonly nint m_imageViewHandle;
    private readonly nint m_memoryHandle;
    private readonly IVulkanOffscreenImageApi m_offscreenImageApi;
    private readonly VulkanRenderPass m_renderPass;
    private bool m_disposed;

    /// <summary>Creates an offscreen render target.</summary>
    /// <param name="logicalDevice">The device that owns the target.</param>
    /// <param name="instance">The Vulkan instance associated with the device.</param>
    /// <param name="format">The target's Vulkan pixel format.</param>
    /// <param name="width">The target width in pixels.</param>
    /// <param name="height">The target height in pixels.</param>
    /// <param name="offscreenImageApi">The API used to allocate the color image.</param>
    /// <param name="renderPassApi">The API used to create the compatible render pass.</param>
    /// <param name="framebufferSetApi">The API used to create the image view and framebuffer.</param>
    /// <param name="commandResourcesFactory">The factory for the target's command buffer.</param>
    public VulkanViewTarget(
        VulkanLogicalDevice logicalDevice,
        VulkanInstance instance,
        uint format,
        uint width,
        uint height,
        IVulkanOffscreenImageApi offscreenImageApi,
        IVulkanRenderPassApi renderPassApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanCommandResourcesFactory commandResourcesFactory
    ) {
        ArgumentNullException.ThrowIfNull(logicalDevice);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(offscreenImageApi);
        ArgumentNullException.ThrowIfNull(renderPassApi);
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(commandResourcesFactory);

        m_deviceHandle = logicalDevice.Handle;
        m_framebufferSetApi = framebufferSetApi;
        m_offscreenImageApi = offscreenImageApi;
        Width = width;
        Height = height;

        var image = offscreenImageApi.CreateColorImage(request: new VulkanOffscreenImageCreateRequest(
            DeviceHandle: m_deviceHandle,
            Format: format,
            Height: height,
            InstanceHandle: instance.Handle,
            PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            UsageFlags: VulkanImageUsageFlags.ColorAttachment | VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferSource,
            Width: width
        ));

        m_imageHandle = image.ImageHandle;
        m_memoryHandle = image.MemoryHandle;

        framebufferSetApi.CreateImageView(
            imageViewHandle: out m_imageViewHandle,
            request: new VulkanImageViewCreateRequest(
                DeviceHandle: m_deviceHandle,
                Format: format,
                ImageHandle: m_imageHandle
            )
        ).ThrowIfFailed(operation: "vkCreateImageView");

        renderPassApi.CreateRenderPass(
            renderPassHandle: out var renderPassHandle,
            request: VulkanRenderPassRequests.Sampled(
                colorFormat: format,
                deviceHandle: m_deviceHandle,
                preserveExistingContents: false
            )
        ).ThrowIfFailed(operation: "vkCreateRenderPass");
        m_renderPass = new VulkanRenderPass(
            deviceHandle: m_deviceHandle,
            renderPassApi: renderPassApi,
            renderPassHandle: renderPassHandle
        );

        framebufferSetApi.CreateFramebuffer(
            framebufferHandle: out m_framebufferHandle,
            request: new VulkanFramebufferCreateRequest(
                DeviceHandle: m_deviceHandle,
                Height: height,
                ImageViewHandle: m_imageViewHandle,
                RenderPassHandle: renderPassHandle,
                Width: width
            )
        ).ThrowIfFailed(operation: "vkCreateFramebuffer");

        m_commandResources = commandResourcesFactory.Create(
            commandBufferCount: 1,
            logicalDevice: logicalDevice
        );
    }

    /// <summary>Gets the native command buffer used to render the target.</summary>
    public nint CommandBufferHandle => m_commandResources.CommandBufferHandles[0];
    /// <summary>Gets the native framebuffer handle.</summary>
    public nint FramebufferHandle => m_framebufferHandle;
    /// <summary>Gets the target height in pixels.</summary>
    public uint Height { get; }
    /// <summary>Gets the native image handle.</summary>
    public nint ImageHandle => m_imageHandle;
    /// <summary>Gets the native image-view handle used to sample the target.</summary>
    public nint ImageViewHandle => m_imageViewHandle;
    /// <summary>Gets the render-pass owner.</summary>
    public VulkanRenderPass RenderPass => m_renderPass;
    /// <summary>Gets the native render-pass handle.</summary>
    public nint RenderPassHandle => m_renderPass.Handle;
    /// <summary>Gets the target width in pixels.</summary>
    public uint Width { get; }

    /// <summary>Releases the target and all owned Vulkan resources.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_commandResources.Dispose();

        if (m_framebufferHandle != 0) {
            m_framebufferSetApi.DestroyFramebuffer(
                deviceHandle: m_deviceHandle,
                framebufferHandle: m_framebufferHandle
            );
        }

        m_renderPass.Dispose();

        if (m_imageViewHandle != 0) {
            m_framebufferSetApi.DestroyImageView(
                deviceHandle: m_deviceHandle,
                imageViewHandle: m_imageViewHandle
            );
        }

        m_offscreenImageApi.DestroyColorImage(
            deviceHandle: m_deviceHandle,
            imageHandle: m_imageHandle,
            memoryHandle: m_memoryHandle
        );
    }
}
