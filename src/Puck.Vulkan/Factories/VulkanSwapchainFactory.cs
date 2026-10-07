using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan.Factories;

/// <summary>
/// The default <see cref="IVulkanSwapchainFactory"/>: it clamps the requested extent to the surface's
/// range and creates an owning <see cref="VulkanSwapchain"/>. Format, present mode, and image usage may be
/// requested by the caller; the format and color space are always a <see cref="DisplayOutput"/> the surface offers (<see cref="SelectOutput"/>),
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
    // The outputs a surface offers, as the neutral vocabulary names them: each pair whose format is one a swapchain may
    // be created in and whose color space DisplayColorSpace names, in the surface's order.
    private static List<DisplayOutput> Reported(IReadOnlyList<VulkanSurfaceFormat> surfaceFormats) {
        var reported = new List<DisplayOutput>(capacity: surfaceFormats.Count);

        foreach (var offered in surfaceFormats) {
            foreach (var format in DisplayOutput.SdrFormats) {
                if (VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format) != offered.Format) {
                    continue;
                }

                foreach (var colorSpace in Enum.GetValues<DisplayColorSpace>()) {
                    if (VulkanGpuFormats.ToVkColorSpace(colorSpace: colorSpace) == offered.ColorSpace) {
                        reported.Add(item: new DisplayOutput(
                            ColorSpace: colorSpace,
                            Format: format
                        ));
                    }
                }
            }
        }

        return reported;
    }

    /// <summary>Selects the output a swapchain is created in from the format and color-space pairs the surface offers,
    /// through <see cref="DisplayOutput.TrySelect"/>: a requested HDR color space when the surface offers it in its
    /// format, otherwise SDR in the preferred format or the first of <see cref="DisplayOutput.SdrFormats"/> the surface
    /// offers in <c>SRGB_NONLINEAR_KHR</c>. A surface's own order never decides, and a pair
    /// <see cref="DisplayOutput"/> does not name is never chosen.</summary>
    /// <param name="surfaceFormats">The format and color-space pairs the surface offers.</param>
    /// <param name="requestedColorSpace">The color space asked for.</param>
    /// <param name="preferredFormat">The SDR format preferred, or <see langword="null"/> for none.</param>
    /// <returns>The offered pair the swapchain is created with, and the output it presents.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="surfaceFormats"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The surface offers none of <see cref="DisplayOutput.SdrFormats"/> in
    /// <c>SRGB_NONLINEAR_KHR</c> and not the requested HDR output; the message names the <c>VkFormat</c> values it
    /// offers.</exception>
    public static (VulkanSurfaceFormat Surface, DisplayOutput Output) SelectOutput(IReadOnlyList<VulkanSurfaceFormat> surfaceFormats, DisplayColorSpace requestedColorSpace, GpuPixelFormat? preferredFormat) {
        ArgumentNullException.ThrowIfNull(argument: surfaceFormats);

        if (!DisplayOutput.TrySelect(
            chosen: out var output,
            preferredSdrFormat: preferredFormat,
            reported: Reported(surfaceFormats: surfaceFormats),
            requested: requestedColorSpace
        )) {
            throw new NotSupportedException(message: $"The surface offers no swapchain format Puck names (VkFormat {string.Join(separator: ", ", values: surfaceFormats.Select(selector: static offered => offered.Format))}); a swapchain needs one of {string.Join(separator: ", ", values: DisplayOutput.SdrFormats)} in SRGB_NONLINEAR_KHR.");
        }

        return (
            new VulkanSurfaceFormat(
                ColorSpace: VulkanGpuFormats.ToVkColorSpace(colorSpace: output.ColorSpace),
                Format: VulkanGpuFormats.ToVkFormat(gpuPixelFormat: output.Format)
            ),
            output
        );
    }    /// <inheritdoc/>
    public VulkanSwapchain Create(
        VulkanLogicalDevice logicalDevice,
        VulkanSurface surface,
        VulkanSwapchainSupportDetails supportDetails,
        uint desiredWidth,
        uint desiredHeight,
        uint? preferredPresentMode = null,
        GpuPixelFormat? preferredFormat = null,
        DisplayColorSpace requestedColorSpace = DisplayColorSpace.Srgb,
        uint? imageUsage = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: logicalDevice);
        ArgumentNullException.ThrowIfNull(argument: surface);

        if (!supportDetails.IsComplete) {
            throw new InvalidOperationException(message: "Cannot create a Vulkan swapchain without complete swapchain support details.");
        }

        var (surfaceFormat, output) = SelectOutput(
            preferredFormat: preferredFormat,
            requestedColorSpace: requestedColorSpace,
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

        result.ThrowIfFailed(device: logicalDevice.Commands, operation: "vkCreateSwapchainKHR");

        if (0 == swapchainHandle) {
            throw new InvalidOperationException(message: "vkCreateSwapchainKHR returned success without a valid swapchain handle.");
        }

        return new(
            device: logicalDevice.Commands,
            imageExtentHeight: height,
            imageExtentWidth: width,
            output: output,
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
