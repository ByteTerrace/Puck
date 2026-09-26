using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanSwapchainFactory"/>: it clamps the requested extent to the surface's
/// range and creates an owning <see cref="VulkanSwapchain"/>. Format, present mode, and image usage may be
/// requested by the caller; the format is always one of <see cref="SwapchainFormats"/> (<see cref="SelectSurfaceFormat"/>),
/// absent a supported preference it prefers the mailbox then immediate then FIFO present mode, and it uses
/// color-attachment plus transfer-source usage.
/// </summary>
public sealed class VulkanSwapchainFactory : IVulkanSwapchainFactory {
    private const uint ColorAttachmentImageUsage = 0x00000010;
    private const uint ConcurrentSharingMode = 1;
    private const uint ExclusiveSharingMode = 0;
    private const uint OpaqueCompositeAlpha = 0x00000001;
    private const uint SpecialCurrentExtent = 0xFFFFFFFF;
    private const uint TransferSourceImageUsage = 0x00000001;

    private readonly IVulkanSwapchainApi m_swapchainApi;

    private static IReadOnlyList<uint> BuildQueueFamilyIndices(VulkanQueueFamilySelection queueFamilySelection) {
        if (queueFamilySelection.UsesSingleQueueFamily) {
            return [queueFamilySelection.GraphicsFamilyIndex];
        }

        return [
            queueFamilySelection.GraphicsFamilyIndex,
            queueFamilySelection.PresentFamilyIndex
        ];
    }
    private static (uint Width, uint Height) ResolveExtent(
        VulkanSurfaceCapabilities capabilities,
        uint desiredWidth,
        uint desiredHeight
    ) {
        if (
            (capabilities.CurrentExtentWidth != SpecialCurrentExtent) &&
            (capabilities.CurrentExtentHeight != SpecialCurrentExtent)
        ) {
            return (
                capabilities.CurrentExtentWidth,
                capabilities.CurrentExtentHeight
            );
        }

        return (
            Math.Clamp(
            max: capabilities.MaxImageExtentWidth,
            min: capabilities.MinImageExtentWidth,
            value: desiredWidth
        ),
            Math.Clamp(
            max: capabilities.MaxImageExtentHeight,
            min: capabilities.MinImageExtentHeight,
            value: desiredHeight
        )
        );
    }
    private static uint SelectCompositeAlpha(uint supportedCompositeAlpha) {
        if (0 != (supportedCompositeAlpha & OpaqueCompositeAlpha)) {
            return OpaqueCompositeAlpha;
        }

        for (var bit = 0; (bit < (sizeof(uint) * 8)); ++bit) {
            var candidate = (1u << bit);

            if (0 != (supportedCompositeAlpha & candidate)) {
                return candidate;
            }
        }

        throw new InvalidOperationException(message: "The active surface does not report any supported composite-alpha mode.");
    }
    private static uint SelectImageCount(VulkanSurfaceCapabilities capabilities) {
        var imageCount = (capabilities.MinImageCount + 1);

        return (
            ((capabilities.MaxImageCount > 0) &&
            (imageCount > capabilities.MaxImageCount))
            ? capabilities.MaxImageCount
            : imageCount
        );
    }
    private static uint SelectPresentMode(IReadOnlyList<uint> presentModes, uint? preferredPresentMode) {
        if (
            preferredPresentMode.HasValue &&
            presentModes.Contains(value: preferredPresentMode.Value)
        ) {
            return preferredPresentMode.Value;
        }

        if (presentModes.Contains(value: VulkanPresentMode.Mailbox)) {
            return VulkanPresentMode.Mailbox;
        }

        if (presentModes.Contains(value: VulkanPresentMode.Immediate)) {
            return VulkanPresentMode.Immediate;
        }

        if (presentModes.Contains(value: VulkanPresentMode.Fifo)) {
            return VulkanPresentMode.Fifo;
        }

        return presentModes[0];
    }
    private static bool TryFind(IReadOnlyList<VulkanSurfaceFormat> surfaceFormats, GpuPixelFormat format, out VulkanSurfaceFormat surfaceFormat) {
        var vkFormat = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format);

        foreach (var offered in surfaceFormats) {
            if (offered.Format == vkFormat) {
                surfaceFormat = offered;

                return true;
            }
        }

        surfaceFormat = default;

        return false;
    }

    /// <summary>Gets the formats a swapchain may be created in, in the order <see cref="SelectSurfaceFormat"/> prefers
    /// them when the caller's preference is not offered: 8-bit unsigned normalized, then 8-bit sRGB, then 10-bit, then
    /// half float. Every member is a <see cref="GpuPixelFormat"/> both backends map, so the swapchain's format is always
    /// one a render pass and a pipeline can be described in.</summary>
    public static IReadOnlyList<GpuPixelFormat> SwapchainFormats { get; } = [
        GpuPixelFormat.B8G8R8A8Unorm,
        GpuPixelFormat.R8G8B8A8Unorm,
        GpuPixelFormat.B8G8R8A8Srgb,
        GpuPixelFormat.R8G8B8A8Srgb,
        GpuPixelFormat.R10G10B10A2Unorm,
        GpuPixelFormat.R16G16B16A16Float,
    ];

    /// <summary>Selects the format a swapchain is created in: the preferred format when the surface offers it and it is
    /// one of <see cref="SwapchainFormats"/>, otherwise the first of <see cref="SwapchainFormats"/> the surface offers.
    /// A surface's own order never decides, and a format <see cref="GpuPixelFormat"/> does not name is never
    /// chosen.</summary>
    /// <param name="surfaceFormats">The format and color-space pairs the surface offers.</param>
    /// <param name="preferredFormat">The caller's preferred format, or <see langword="null"/> for none.</param>
    /// <returns>The offered pair the swapchain is created with, and the neutral format it is in.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="surfaceFormats"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The surface offers none of <see cref="SwapchainFormats"/>; the message
    /// names the <c>VkFormat</c> values it offers.</exception>
    public static (VulkanSurfaceFormat Surface, GpuPixelFormat Format) SelectSurfaceFormat(IReadOnlyList<VulkanSurfaceFormat> surfaceFormats, GpuPixelFormat? preferredFormat) {
        ArgumentNullException.ThrowIfNull(argument: surfaceFormats);

        if (
            (preferredFormat is { } preferred) &&
            SwapchainFormats.Contains(value: preferred) &&
            TryFind(
                format: preferred,
                surfaceFormat: out var preferredSurface,
                surfaceFormats: surfaceFormats
            )
        ) {
            return (preferredSurface, preferred);
        }

        foreach (var format in SwapchainFormats) {
            if (TryFind(
                format: format,
                surfaceFormat: out var surface,
                surfaceFormats: surfaceFormats
            )) {
                return (surface, format);
            }
        }

        throw new NotSupportedException(message: $"The surface offers no swapchain format Puck names (VkFormat {string.Join(separator: ", ", values: surfaceFormats.Select(selector: static offered => offered.Format))}); a swapchain needs one of {string.Join(separator: ", ", values: SwapchainFormats)}.");
    }
    /// <inheritdoc/>
    public VulkanSwapchain Create(
        VulkanLogicalDevice logicalDevice,
        VulkanSurface surface,
        VulkanSwapchainSupportDetails supportDetails,
        uint desiredWidth,
        uint desiredHeight,
        uint? preferredPresentMode = null,
        GpuPixelFormat? preferredFormat = null,
        uint? imageUsage = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDevice);
        ArgumentNullException.ThrowIfNull(argument: surface);

        if (!supportDetails.IsComplete) {
            throw new InvalidOperationException(message: "Cannot create a Vulkan swapchain without complete swapchain support details.");
        }

        var (surfaceFormat, format) = SelectSurfaceFormat(
            preferredFormat: preferredFormat,
            surfaceFormats: supportDetails.SurfaceFormats
        );

        var (width, height) = ResolveExtent(
            capabilities: supportDetails.Capabilities,
            desiredHeight: desiredHeight,
            desiredWidth: desiredWidth
        );
        var compositeAlpha = SelectCompositeAlpha(supportedCompositeAlpha: supportDetails.Capabilities.SupportedCompositeAlpha);
        var imageCount = SelectImageCount(capabilities: supportDetails.Capabilities);
        var presentMode = SelectPresentMode(
            preferredPresentMode: preferredPresentMode,
            presentModes: supportDetails.PresentModes
        );
        var queueFamilyIndices = BuildQueueFamilyIndices(queueFamilySelection: logicalDevice.PhysicalDevice.QueueFamilySelection);
        var sharingMode = ((queueFamilyIndices.Count == 1)
            ? ExclusiveSharingMode
            : ConcurrentSharingMode
        );
        var request = new VulkanSwapchainCreateRequest(
            CompositeAlpha: compositeAlpha,
            Device: logicalDevice.Commands,
            ImageColorSpace: surfaceFormat.ColorSpace,
            ImageCount: imageCount,
            ImageExtentHeight: height,
            ImageExtentWidth: width,
            ImageFormat: surfaceFormat.Format,
            ImageUsage: (imageUsage ?? (ColorAttachmentImageUsage | TransferSourceImageUsage)),
            PresentMode: presentMode,
            PreTransform: supportDetails.Capabilities.CurrentTransform,
            QueueFamilyIndices: queueFamilyIndices,
            SharingMode: sharingMode,
            SurfaceHandle: surface.Handle
        );

        var result = m_swapchainApi.CreateSwapchain(
            request: request,
            swapchainHandle: out var swapchainHandle
        );

        result.ThrowIfFailed(operation: "vkCreateSwapchainKHR");

        if (0 == swapchainHandle) {
            throw new InvalidOperationException(message: "vkCreateSwapchainKHR returned success without a valid swapchain handle.");
        }

        return new(
            device: logicalDevice.Commands,
            imageExtentHeight: height,
            imageExtentWidth: width,
            format: format,
            swapchainApi: m_swapchainApi,
            swapchainHandle: swapchainHandle
        );
    }

    /// <summary>Initializes a new instance of the <see cref="VulkanSwapchainFactory"/> class.</summary>
    /// <param name="swapchainApi">The swapchain API used to create and own the underlying swapchain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="swapchainApi"/> is <see langword="null"/>.</exception>
    public VulkanSwapchainFactory(IVulkanSwapchainApi swapchainApi) {
        ArgumentNullException.ThrowIfNull(argument: swapchainApi);

        m_swapchainApi = swapchainApi;
    }
}
