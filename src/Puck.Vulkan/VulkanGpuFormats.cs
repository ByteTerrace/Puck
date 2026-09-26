namespace Puck.Vulkan;

/// <summary>Maps backend-neutral <see cref="GpuPixelFormat"/> values to their <c>VkFormat</c> equivalents, and an image's
/// declared <see cref="GpuImageUsage"/> to its Vulkan usage and view aspect.</summary>
public static class VulkanGpuFormats {
    /// <summary>The <c>VK_IMAGE_ASPECT_COLOR_BIT</c> value.</summary>
    public const uint ColorAspect = 0x00000001;
    /// <summary>The <c>VK_IMAGE_ASPECT_DEPTH_BIT</c> value.</summary>
    public const uint DepthAspect = 0x00000002;

    /// <summary>Gets the aspect an image of a format is viewed and transitioned through: depth for a depth format, color
    /// otherwise.</summary>
    /// <param name="format">The image format.</param>
    /// <returns>The <c>VkImageAspectFlags</c> value.</returns>
    public static uint AspectOf(GpuPixelFormat format) => (GpuPixelFormats.IsDepth(format: format)
        ? DepthAspect
        : ColorAspect
    );
    /// <summary>Converts declared image usages to <c>VkImageUsageFlags</c>. A color image is also a transfer source and
    /// destination, which readbacks, uploads and zero clears need; a depth image is only a depth attachment.</summary>
    /// <param name="usage">The declared usages.</param>
    /// <returns>The Vulkan usage flags.</returns>
    public static uint ToVkImageUsage(GpuImageUsage usage) {
        if (usage == GpuImageUsage.DepthAttachment) {
            return VulkanImageUsageFlags.DepthStencilAttachment;
        }

        var flags = VulkanImageUsageFlags.TransferSource | VulkanImageUsageFlags.TransferDestination;

        if ((usage & GpuImageUsage.Sampled) != 0) {
            flags |= VulkanImageUsageFlags.Sampled;
        }

        if ((usage & GpuImageUsage.Storage) != 0) {
            flags |= VulkanImageUsageFlags.Storage;
        }

        if ((usage & GpuImageUsage.ColorAttachment) != 0) {
            flags |= VulkanImageUsageFlags.ColorAttachment;
        }

        return flags;
    }
    /// <summary>Converts a <c>VkFormat</c> value to the <see cref="GpuPixelFormat"/> <see cref="ToVkFormat"/> maps to
    /// it, such as a swapchain's image format.</summary>
    /// <param name="vkFormat">The <see cref="VulkanFormat"/> constant.</param>
    /// <returns>The backend-neutral pixel format.</returns>
    /// <exception cref="NotSupportedException"><paramref name="vkFormat"/> is no format <see cref="GpuPixelFormat"/>
    /// names.</exception>
    public static GpuPixelFormat FromVkFormat(uint vkFormat) => vkFormat switch {
        VulkanFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
        VulkanFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
        VulkanFormat.R16G16B16A16Sfloat => GpuPixelFormat.R16G16B16A16Float,
        VulkanFormat.R32G32B32A32Sfloat => GpuPixelFormat.R32G32B32A32Float,
        VulkanFormat.D32Sfloat => GpuPixelFormat.D32Float,
        _ => throw new NotSupportedException(message: $"VkFormat {vkFormat} is no GpuPixelFormat."),
    };    /// <summary>Converts a <see cref="GpuPixelFormat"/> to its <c>VkFormat</c> value.</summary>
          /// <param name="gpuPixelFormat">The backend-neutral pixel format.</param>
          /// <returns>The corresponding <see cref="VulkanFormat"/> constant.</returns>
    public static uint ToVkFormat(GpuPixelFormat gpuPixelFormat) => gpuPixelFormat switch {
        GpuPixelFormat.R8G8B8A8Unorm => VulkanFormat.R8G8B8A8Unorm,
        GpuPixelFormat.B8G8R8A8Unorm => VulkanFormat.B8G8R8A8Unorm,
        GpuPixelFormat.R16G16B16A16Float => VulkanFormat.R16G16B16A16Sfloat,
        GpuPixelFormat.R32G32B32A32Float => VulkanFormat.R32G32B32A32Sfloat,
        GpuPixelFormat.D32Float => VulkanFormat.D32Sfloat,
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(gpuPixelFormat),
        actualValue: gpuPixelFormat,
        message: null
    ),
    };
}
