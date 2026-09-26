using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanFramebufferSetFactory"/>: it creates an image view and framebuffer for
/// each swapchain image and returns an owning <see cref="VulkanFramebufferSet"/>, cleaning up partial
/// progress if creation fails.
/// </summary>
public sealed class VulkanFramebufferSetFactory : IVulkanFramebufferSetFactory {
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanFramebufferSetFactory"/> class.</summary>
    /// <param name="framebufferSetApi">The framebuffer-set API used to create the image views and framebuffers.</param>
    /// <exception cref="ArgumentNullException"><paramref name="framebufferSetApi"/> is <see langword="null"/>.</exception>
    public VulkanFramebufferSetFactory(IVulkanFramebufferSetApi framebufferSetApi) {
        ArgumentNullException.ThrowIfNull(argument: framebufferSetApi);

        m_framebufferSetApi = framebufferSetApi;
    }

    private void CleanupHandles(
        VulkanDeviceCommands device,
        IReadOnlyList<nint> framebufferHandles,
        IReadOnlyList<nint> imageViewHandles
    ) {
        foreach (var framebufferHandle in framebufferHandles) {
            m_framebufferSetApi.DestroyFramebuffer(
                device: device,
                framebufferHandle: framebufferHandle
            );
        }

        foreach (var imageViewHandle in imageViewHandles) {
            m_framebufferSetApi.DestroyImageView(
                device: device,
                imageViewHandle: imageViewHandle
            );
        }
    }
    private nint CreateFramebuffer(
        VulkanDeviceCommands device,
        nint renderPassHandle,
        nint imageViewHandle,
        uint width,
        uint height
    ) {
        var request = new VulkanFramebufferCreateRequest(
            Device: device,
            Height: height,
            ImageViewHandles: [imageViewHandle],
            RenderPassHandle: renderPassHandle,
            Width: width
        );
        var result = m_framebufferSetApi.CreateFramebuffer(
            framebufferHandle: out var framebufferHandle,
            request: request
        );

        result.ThrowIfFailed(operation: "vkCreateFramebuffer");

        if (0 == framebufferHandle) {
            throw new InvalidOperationException(message: "vkCreateFramebuffer returned success without a valid framebuffer handle.");
        }

        return framebufferHandle;
    }
    private nint CreateImageView(
        VulkanDeviceCommands device,
        uint format,
        nint imageHandle
    ) {
        var request = new VulkanImageViewCreateRequest(
            Device: device,
            Format: format,
            ImageHandle: imageHandle
        );
        var result = m_framebufferSetApi.CreateImageView(
            imageViewHandle: out var imageViewHandle,
            request: request
        );

        result.ThrowIfFailed(operation: "vkCreateImageView");

        if (0 == imageViewHandle) {
            throw new InvalidOperationException(message: "vkCreateImageView returned success without a valid image-view handle.");
        }

        return imageViewHandle;
    }

    /// <inheritdoc/>
    public VulkanFramebufferSet Create(
        VulkanLogicalDevice logicalDevice,
        VulkanRenderPass renderPass,
        VulkanSwapchain swapchain
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDevice);
        ArgumentNullException.ThrowIfNull(argument: renderPass);
        ArgumentNullException.ThrowIfNull(argument: swapchain);

        var swapchainImages = m_framebufferSetApi.GetSwapchainImages(
            device: logicalDevice.Commands,
            swapchainHandle: swapchain.Handle
        );

        if (0 == swapchainImages.Count) {
            throw new InvalidOperationException(message: "The Vulkan swapchain did not report any images for framebuffer creation.");
        }

        var imageViewHandles = new nint[swapchainImages.Count];
        var framebufferHandles = new nint[swapchainImages.Count];

        try {
            for (var index = 0; (index < swapchainImages.Count); ++index) {
                imageViewHandles[index] = CreateImageView(
                    device: logicalDevice.Commands,
                    format: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: swapchain.Format),
                    imageHandle: swapchainImages[index]
                );
                framebufferHandles[index] = CreateFramebuffer(
                    device: logicalDevice.Commands,
                    height: swapchain.ImageExtentHeight,
                    imageViewHandle: imageViewHandles[index],
                    renderPassHandle: renderPass.Handle,
                    width: swapchain.ImageExtentWidth
                );
            }

            return new(
                device: logicalDevice.Commands,
                framebufferHandles: framebufferHandles,
                framebufferSetApi: m_framebufferSetApi,
                imageHandles: swapchainImages,
                imageViewHandles: imageViewHandles
            );
        } catch {
            CleanupHandles(
                device: logicalDevice.Commands,
                framebufferHandles: framebufferHandles,
                imageViewHandles: imageViewHandles
            );
            throw;
        }
    }
}

