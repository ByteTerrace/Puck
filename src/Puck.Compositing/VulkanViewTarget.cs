using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Compositing;

public sealed class VulkanViewTarget : IDisposable {
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

    public nint CommandBufferHandle => m_commandResources.CommandBufferHandles[0];
    public nint FramebufferHandle => m_framebufferHandle;
    public uint Height { get; }
    public nint ImageHandle => m_imageHandle;
    public nint ImageViewHandle => m_imageViewHandle;
    public VulkanRenderPass RenderPass => m_renderPass;
    public uint Width { get; }

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
