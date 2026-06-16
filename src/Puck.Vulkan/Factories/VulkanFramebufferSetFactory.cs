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
        nint deviceHandle,
        IReadOnlyList<nint> framebufferHandles,
        IReadOnlyList<nint> imageViewHandles
    ) {
        foreach (var framebufferHandle in framebufferHandles) {
            if (0 != framebufferHandle) {
                m_framebufferSetApi.DestroyFramebuffer(
                    deviceHandle: deviceHandle,
                    framebufferHandle: framebufferHandle
                );
            }
        }

        foreach (var imageViewHandle in imageViewHandles) {
            if (0 != imageViewHandle) {
                m_framebufferSetApi.DestroyImageView(
                    deviceHandle: deviceHandle,
                    imageViewHandle: imageViewHandle
                );
            }
        }
    }
    private nint CreateFramebuffer(
        nint deviceHandle,
        nint renderPassHandle,
        nint imageViewHandle,
        uint width,
        uint height
    ) {
        var request = new VulkanFramebufferCreateRequest(
            DeviceHandle: deviceHandle,
            Height: height,
            ImageViewHandle: imageViewHandle,
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
        nint deviceHandle,
        uint format,
        nint imageHandle
    ) {
        var request = new VulkanImageViewCreateRequest(
            DeviceHandle: deviceHandle,
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
            deviceHandle: logicalDevice.Handle,
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
                    deviceHandle: logicalDevice.Handle,
                    format: swapchain.ImageFormat,
                    imageHandle: swapchainImages[index]
                );
                framebufferHandles[index] = CreateFramebuffer(
                    deviceHandle: logicalDevice.Handle,
                    height: swapchain.ImageExtentHeight,
                    imageViewHandle: imageViewHandles[index],
                    renderPassHandle: renderPass.Handle,
                    width: swapchain.ImageExtentWidth
                );
            }

            return new(
                deviceHandle: logicalDevice.Handle,
                framebufferHandles: framebufferHandles,
                framebufferSetApi: m_framebufferSetApi,
                imageHandles: swapchainImages,
                imageViewHandles: imageViewHandles
            );
        } catch {
            CleanupHandles(
                deviceHandle: logicalDevice.Handle,
                framebufferHandles: framebufferHandles,
                imageViewHandles: imageViewHandles
            );
            throw;
        }
    }
}

