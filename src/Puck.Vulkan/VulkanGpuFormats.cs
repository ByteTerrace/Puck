namespace Puck.Vulkan;

/// <summary>Maps backend-neutral <see cref="GpuPixelFormat"/> values to their <c>VkFormat</c> equivalents, a
/// <see cref="DisplayColorSpace"/> to its <c>VkColorSpaceKHR</c>, and an image's declared <see cref="GpuImageUsage"/> to
/// its Vulkan usage and view aspect.</summary>
public static class VulkanGpuFormats {
    /// <summary>The <c>VK_COLOR_SPACE_SRGB_NONLINEAR_KHR</c> value.</summary>
    public const uint SrgbNonlinearColorSpace = 0U;
    /// <summary>The <c>VK_COLOR_SPACE_EXTENDED_SRGB_LINEAR_EXT</c> value, which <c>VK_EXT_swapchain_colorspace</c>
    /// reports.</summary>
    public const uint ExtendedSrgbLinearColorSpace = 1_000_104_002U;
    /// <summary>The <c>VK_COLOR_SPACE_HDR10_ST2084_EXT</c> value, which <c>VK_EXT_swapchain_colorspace</c>
    /// reports.</summary>
    public const uint Hdr10St2084ColorSpace = 1_000_104_008U;
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
    /// <summary>Converts a <see cref="DisplayColorSpace"/> to its <c>VkColorSpaceKHR</c> value.</summary>
    /// <param name="colorSpace">The backend-neutral color space.</param>
    /// <returns>The <c>VkColorSpaceKHR</c> value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="colorSpace"/> is not defined.</exception>
    public static uint ToVkColorSpace(DisplayColorSpace colorSpace) => colorSpace switch {
        DisplayColorSpace.Srgb => SrgbNonlinearColorSpace,
        DisplayColorSpace.Hdr10 => Hdr10St2084ColorSpace,
        DisplayColorSpace.ScRgb => ExtendedSrgbLinearColorSpace,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: colorSpace,
            message: "The display color space is not defined.",
            paramName: nameof(colorSpace)
        ),
    };
    /// <summary>Converts a <see cref="GpuPixelFormat"/> to its <c>VkFormat</c> value.</summary>
    /// <param name="gpuPixelFormat">The backend-neutral pixel format.</param>
    /// <returns>The corresponding <see cref="VulkanFormat"/> constant.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gpuPixelFormat"/> is not defined.</exception>
    public static uint ToVkFormat(GpuPixelFormat gpuPixelFormat) => gpuPixelFormat switch {
        GpuPixelFormat.R8G8B8A8Unorm => VulkanFormat.R8G8B8A8Unorm,
        GpuPixelFormat.B8G8R8A8Unorm => VulkanFormat.B8G8R8A8Unorm,
        GpuPixelFormat.R16G16B16A16Float => VulkanFormat.R16G16B16A16Sfloat,
        GpuPixelFormat.R32G32B32A32Float => VulkanFormat.R32G32B32A32Sfloat,
        GpuPixelFormat.D32Float => VulkanFormat.D32Sfloat,
        GpuPixelFormat.Bc4Unorm => VulkanFormat.Bc4UnormBlock,
        GpuPixelFormat.Bc5Unorm => VulkanFormat.Bc5UnormBlock,
        GpuPixelFormat.Bc6hUfloat => VulkanFormat.Bc6hUfloatBlock,
        GpuPixelFormat.Bc7Unorm => VulkanFormat.Bc7UnormBlock,
        GpuPixelFormat.R8G8B8A8Srgb => VulkanFormat.R8G8B8A8Srgb,
        GpuPixelFormat.B8G8R8A8Srgb => VulkanFormat.B8G8R8A8Srgb,
        GpuPixelFormat.R10G10B10A2Unorm => VulkanFormat.A2B10G10R10UnormPack32,
        GpuPixelFormat.R8Unorm => VulkanFormat.R8Unorm,
        GpuPixelFormat.R8G8Unorm => VulkanFormat.R8G8Unorm,
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(gpuPixelFormat),
        actualValue: gpuPixelFormat,
        message: null
    ),
    };
}
