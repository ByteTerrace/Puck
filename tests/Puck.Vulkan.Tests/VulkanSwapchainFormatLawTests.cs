using Puck.Abstractions.Gpu;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, that a swapchain is only ever created in a format the neutral vocabulary names:
/// every format the selector may choose maps to one <c>VkFormat</c> and back, the selector's own preference order
/// decides rather than the surface's, and a surface offering none of them refuses at swapchain creation.</summary>
public sealed class VulkanSwapchainFormatLawTests {
    private const uint SrgbNonlinear = 0U;
    // VK_FORMAT_R5G6B5_UNORM_PACK16 and VK_FORMAT_A2R10G10B10_UNORM_PACK32: formats a surface may offer that no
    // GpuPixelFormat names.
    private const uint UnnamedPacked565 = 4U;
    private const uint UnnamedA2R10G10B10 = 58U;

    private static VulkanSurfaceFormat Offered(uint vkFormat) => new(
        ColorSpace: SrgbNonlinear,
        Format: vkFormat
    );

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
    public void EverySwapchainFormatIsChosenWhenItIsAllTheSurfaceOffers() {
        foreach (var format in VulkanSwapchainFactory.SwapchainFormats) {
            var vkFormat = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format);

            var (surface, chosen) = VulkanSwapchainFactory.SelectSurfaceFormat(
                preferredFormat: null,
                surfaceFormats: [Offered(vkFormat: UnnamedA2R10G10B10), Offered(vkFormat: vkFormat)]
            );

            Assert.Equal(
                actual: chosen,
                expected: format
            );
            Assert.Equal(
                expected: vkFormat,
                actual: surface.Format
            );
        }
    }
    [Fact]
    public void TheSelectorsOrderDecidesOverTheSurfaces() {
        var (surface, chosen) = VulkanSwapchainFactory.SelectSurfaceFormat(
            preferredFormat: null,
            surfaceFormats: [
                Offered(vkFormat: VulkanFormat.B8G8R8A8Srgb),
                Offered(vkFormat: VulkanFormat.A2B10G10R10UnormPack32),
                Offered(vkFormat: VulkanFormat.B8G8R8A8Unorm),
            ]
        );

        Assert.Equal(
            actual: chosen,
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
            actual: VulkanSwapchainFactory.SelectSurfaceFormat(
                preferredFormat: GpuPixelFormat.R8G8B8A8Unorm,
                surfaceFormats: offered
            ).Format
        );
        Assert.Equal(
            expected: GpuPixelFormat.B8G8R8A8Unorm,
            actual: VulkanSwapchainFactory.SelectSurfaceFormat(
                preferredFormat: GpuPixelFormat.R10G10B10A2Unorm,
                surfaceFormats: offered
            ).Format
        );
    }
    [Fact]
    public void APreferenceNoSwapchainMayTakeIsNotChosen() {
        var (_, chosen) = VulkanSwapchainFactory.SelectSurfaceFormat(
            preferredFormat: GpuPixelFormat.D32Float,
            surfaceFormats: [Offered(vkFormat: VulkanFormat.D32Sfloat), Offered(vkFormat: VulkanFormat.R8G8B8A8Srgb)]
        );

        Assert.Equal(
            actual: chosen,
            expected: GpuPixelFormat.R8G8B8A8Srgb
        );
    }
    [Fact]
    public void ASurfaceWithOnlyUnnamedFormatsRefusesByName() {
        var refusal = Assert.Throws<NotSupportedException>(testCode: static () => VulkanSwapchainFactory.SelectSurfaceFormat(
            preferredFormat: GpuPixelFormat.B8G8R8A8Unorm,
            surfaceFormats: [Offered(vkFormat: UnnamedA2R10G10B10), Offered(vkFormat: UnnamedPacked565)]
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
