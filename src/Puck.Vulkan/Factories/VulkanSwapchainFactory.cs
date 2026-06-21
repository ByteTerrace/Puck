using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanSwapchainFactory"/>: it clamps the requested extent to the surface's
/// range and creates an owning <see cref="VulkanSwapchain"/>. Format, present mode, and image usage may be
/// requested by the caller; absent a (supported) preference it picks the surface's first format, prefers
/// the mailbox then immediate then FIFO present mode, and uses color-attachment plus transfer-source usage.
/// </summary>
public sealed class VulkanSwapchainFactory : IVulkanSwapchainFactory {
    private const uint ColorAttachmentImageUsage = 0x00000010;
    private const uint ConcurrentSharingMode = 1;
    private const uint ExclusiveSharingMode = 0;
    private const uint OpaqueCompositeAlpha = 0x00000001;
    private const uint SpecialCurrentExtent = 0xFFFFFFFF;
    private const uint TransferSourceImageUsage = 0x00000001;

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
                : imageCount);
    }
    private static uint SelectPresentMode(IReadOnlyList<uint> presentModes, uint? preferredPresentMode) {
        if (preferredPresentMode.HasValue && presentModes.Contains(value: preferredPresentMode.Value)) {
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
    private static VulkanSurfaceFormat SelectSurfaceFormat(IReadOnlyList<VulkanSurfaceFormat> surfaceFormats, VulkanSurfaceFormat? preferredSurfaceFormat) {
        if (preferredSurfaceFormat.HasValue && surfaceFormats.Contains(value: preferredSurfaceFormat.Value)) {
            return preferredSurfaceFormat.Value;
        }

        return surfaceFormats[0];
    }

    private readonly IVulkanSwapchainApi m_swapchainApi;

    /// <summary>Initializes a new instance of the <see cref="VulkanSwapchainFactory"/> class.</summary>
    /// <param name="swapchainApi">The swapchain API used to create and own the underlying swapchain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="swapchainApi"/> is <see langword="null"/>.</exception>
    public VulkanSwapchainFactory(IVulkanSwapchainApi swapchainApi) {
        ArgumentNullException.ThrowIfNull(argument: swapchainApi);

        m_swapchainApi = swapchainApi;
    }

    /// <inheritdoc/>
    public VulkanSwapchain Create(
        VulkanLogicalDevice logicalDevice,
        VulkanSurface surface,
        VulkanSwapchainSupportDetails supportDetails,
        uint desiredWidth,
        uint desiredHeight,
        uint? preferredPresentMode = null,
        VulkanSurfaceFormat? preferredSurfaceFormat = null,
        uint? imageUsage = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDevice);
        ArgumentNullException.ThrowIfNull(argument: surface);

        if (!supportDetails.IsComplete) {
            throw new InvalidOperationException(message: "Cannot create a Vulkan swapchain without complete swapchain support details.");
        }

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
            : ConcurrentSharingMode);
        var surfaceFormat = SelectSurfaceFormat(
            preferredSurfaceFormat: preferredSurfaceFormat,
            surfaceFormats: supportDetails.SurfaceFormats
        );
        var request = new VulkanSwapchainCreateRequest(
            CompositeAlpha: compositeAlpha,
            DeviceHandle: logicalDevice.Handle,
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
            deviceHandle: logicalDevice.Handle,
            imageExtentHeight: height,
            imageExtentWidth: width,
            imageFormat: surfaceFormat.Format,
            swapchainApi: m_swapchainApi,
            swapchainHandle: swapchainHandle
        );
    }
}
