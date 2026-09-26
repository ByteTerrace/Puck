using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, that a swapchain is only ever created in an output the neutral vocabulary names:
/// every format the selector may choose maps to one <c>VkFormat</c> and back, the selector's own preference order
/// decides rather than the surface's, an SDR output takes an <c>SRGB_NONLINEAR_KHR</c> pair even where the surface
/// offers its format in another color space, an HDR output is chosen only when requested and offered, and a surface
/// offering none of them refuses at swapchain creation.</summary>
public sealed class VulkanSwapchainFormatLawTests {
    // VK_FORMAT_R5G6B5_UNORM_PACK16 and VK_FORMAT_A2R10G10B10_UNORM_PACK32: formats a surface may offer that no
    // GpuPixelFormat names.
    private const uint UnnamedPacked565 = 4U;
    private const uint UnnamedA2R10G10B10 = 58U;
    // VK_COLOR_SPACE_DISPLAY_P3_NONLINEAR_EXT: a color space DisplayColorSpace does not name.
    private const uint UnnamedDisplayP3 = 1_000_104_001U;

    private static VulkanSurfaceFormat Offered(uint vkFormat, uint colorSpace = VulkanGpuFormats.SrgbNonlinearColorSpace) => new(
        ColorSpace: colorSpace,
        Format: vkFormat
    );
    private static (VulkanSurfaceFormat Surface, DisplayOutput Output) Select(IReadOnlyList<VulkanSurfaceFormat> offered, GpuPixelFormat? preferred = null, DisplayColorSpace requested = DisplayColorSpace.Srgb) =>
        VulkanSwapchainFactory.SelectOutput(
            preferredFormat: preferred,
            requestedColorSpace: requested,
            surfaceFormats: offered
        );
    // A surface on an HDR display: the SDR pairs, then the same formats in the HDR color spaces.
    private static VulkanSurfaceFormat[] HdrSurface() => [
        Offered(vkFormat: VulkanFormat.B8G8R8A8Unorm),
        Offered(vkFormat: VulkanFormat.A2B10G10R10UnormPack32),
        Offered(vkFormat: VulkanFormat.R16G16B16A16Sfloat),
        Offered(
            colorSpace: VulkanGpuFormats.Hdr10St2084ColorSpace,
            vkFormat: VulkanFormat.A2B10G10R10UnormPack32
        ),
        Offered(
            colorSpace: VulkanGpuFormats.ExtendedSrgbLinearColorSpace,
            vkFormat: VulkanFormat.R16G16B16A16Sfloat
        ),
        Offered(
            colorSpace: UnnamedDisplayP3,
            vkFormat: VulkanFormat.B8G8R8A8Unorm
        ),
    ];

    [Fact]
    public void EveryPixelFormatMapsToItsOwnVkFormat() {
        var formats = Enum.GetValues<GpuPixelFormat>();
        var vkFormats = formats.Select(selector: static format => VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format)).ToArray();

        Assert.Equal(
            expected: formats.Length,
            actual: vkFormats.Distinct().Count()
        );
    }
    [Fact]
    public void EverySdrFormatIsChosenWhenItIsAllTheSurfaceOffers() {
        foreach (var format in DisplayOutput.SdrFormats) {
            var vkFormat = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format);
            var (surface, chosen) = Select(offered: [Offered(vkFormat: UnnamedA2R10G10B10), Offered(vkFormat: vkFormat)]);

            Assert.Equal(
                actual: chosen,
                expected: DisplayOutput.Sdr(format: format)
            );
            Assert.Equal(
                expected: Offered(vkFormat: vkFormat),
                actual: surface
            );
        }
    }
    [Fact]
    public void TheSelectorsOrderDecidesOverTheSurfaces() {
        var (surface, chosen) = Select(offered: [
            Offered(vkFormat: VulkanFormat.B8G8R8A8Srgb),
            Offered(vkFormat: VulkanFormat.A2B10G10R10UnormPack32),
            Offered(vkFormat: VulkanFormat.B8G8R8A8Unorm),
        ]);

        Assert.Equal(
            actual: chosen.Format,
            expected: GpuPixelFormat.B8G8R8A8Unorm
        );
        Assert.Equal(
            expected: VulkanFormat.B8G8R8A8Unorm,
            actual: surface.Format
        );
    }
    [Fact]
    public void AnOfferedPreferenceIsChosenAndAnAbsentOneFallsBack() {
        IReadOnlyList<VulkanSurfaceFormat> offered = [Offered(vkFormat: VulkanFormat.B8G8R8A8Unorm), Offered(vkFormat: VulkanFormat.R8G8B8A8Unorm)];

        Assert.Equal(
            expected: GpuPixelFormat.R8G8B8A8Unorm,
            actual: Select(
                offered: offered,
                preferred: GpuPixelFormat.R8G8B8A8Unorm
            ).Output.Format
        );
        Assert.Equal(
            expected: GpuPixelFormat.B8G8R8A8Unorm,
            actual: Select(
                offered: offered,
                preferred: GpuPixelFormat.R10G10B10A2Unorm
            ).Output.Format
        );
    }
    [Fact]
    public void APreferenceNoSwapchainMayTakeIsNotChosen() {
        var (_, chosen) = Select(
            offered: [Offered(vkFormat: VulkanFormat.D32Sfloat), Offered(vkFormat: VulkanFormat.R8G8B8A8Srgb)],
            preferred: GpuPixelFormat.D32Float
        );

        Assert.Equal(
            actual: chosen.Format,
            expected: GpuPixelFormat.R8G8B8A8Srgb
        );
    }
    [Fact]
    public void AnSdrOutputTakesTheSrgbPairWhereverTheSurfaceListsIt() {
        var (surface, chosen) = Select(offered: [
            Offered(
                colorSpace: UnnamedDisplayP3,
                vkFormat: VulkanFormat.B8G8R8A8Unorm
            ),
            Offered(
                colorSpace: VulkanGpuFormats.ExtendedSrgbLinearColorSpace,
                vkFormat: VulkanFormat.B8G8R8A8Unorm
            ),
            Offered(vkFormat: VulkanFormat.B8G8R8A8Unorm),
        ]);

        Assert.Equal(
            actual: (chosen, surface),
            expected: (DisplayOutput.Sdr(format: GpuPixelFormat.B8G8R8A8Unorm), Offered(vkFormat: VulkanFormat.B8G8R8A8Unorm))
        );
    }
    [Fact]
    public void AnHdrOutputIsChosenOnlyWhenRequestedAndOffered() {
        var sdr = Select(offered: HdrSurface());
        var hdr10 = Select(
            offered: HdrSurface(),
            requested: DisplayColorSpace.Hdr10
        );
        var scRgb = Select(
            offered: HdrSurface(),
            requested: DisplayColorSpace.ScRgb
        );
        var absent = Select(
            offered: [Offered(vkFormat: VulkanFormat.B8G8R8A8Unorm), Offered(vkFormat: VulkanFormat.A2B10G10R10UnormPack32)],
            requested: DisplayColorSpace.Hdr10
        );

        Assert.Equal(
            actual: sdr.Output,
            expected: DisplayOutput.Sdr(format: GpuPixelFormat.B8G8R8A8Unorm)
        );
        Assert.Equal(
            actual: hdr10,
            expected: (Offered(
                colorSpace: VulkanGpuFormats.Hdr10St2084ColorSpace,
                vkFormat: VulkanFormat.A2B10G10R10UnormPack32
            ), new DisplayOutput(
                ColorSpace: DisplayColorSpace.Hdr10,
                Format: GpuPixelFormat.R10G10B10A2Unorm
            ))
        );
        Assert.Equal(
            actual: scRgb,
            expected: (Offered(
                colorSpace: VulkanGpuFormats.ExtendedSrgbLinearColorSpace,
                vkFormat: VulkanFormat.R16G16B16A16Sfloat
            ), new DisplayOutput(
                ColorSpace: DisplayColorSpace.ScRgb,
                Format: GpuPixelFormat.R16G16B16A16Float
            ))
        );
        Assert.Equal(
            actual: absent.Output,
            expected: DisplayOutput.Sdr(format: GpuPixelFormat.B8G8R8A8Unorm)
        );
    }
    [Fact]
    public void ASurfaceWithOnlyUnnamedFormatsRefusesByName() {
        var refusal = Assert.Throws<NotSupportedException>(testCode: static () => Select(
            offered: [Offered(vkFormat: UnnamedA2R10G10B10), Offered(vkFormat: UnnamedPacked565)],
            preferred: GpuPixelFormat.B8G8R8A8Unorm
        ));

        Assert.Contains(
            expectedSubstring: "VkFormat 58, 4",
            actualString: refusal.Message
        );
        Assert.Contains(
            expectedSubstring: nameof(GpuPixelFormat.B8G8R8A8Unorm),
            actualString: refusal.Message
        );
    }
}
