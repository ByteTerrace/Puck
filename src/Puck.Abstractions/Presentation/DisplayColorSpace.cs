using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Abstractions.Presentation;

/// <summary>
/// The color space a swapchain's pixels are encoded in for the display: its primaries, its transfer function and the
/// range a pixel value spans. Each backend maps a member to its native value (Vulkan's <c>VkColorSpaceKHR</c>, DXGI's
/// <c>DXGI_COLOR_SPACE_TYPE</c>).
/// </summary>
[JsonConverter(typeof(StrictEnumConverter<DisplayColorSpace>))]
public enum DisplayColorSpace : uint {
    /// <summary>Standard dynamic range: BT.709 primaries and the sRGB transfer function, a pixel value of one at the SDR
    /// white level (Vulkan <c>SRGB_NONLINEAR_KHR</c>, <c>DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709</c>). The default and the
    /// fallback.</summary>
    Srgb = 0,
    /// <summary>HDR10: BT.2020 primaries and the SMPTE ST 2084 perceptual quantizer, a pixel value of one at 10,000 nits,
    /// in a 10-bit unsigned normalized swapchain (Vulkan <c>HDR10_ST2084_EXT</c>,
    /// <c>DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020</c>).</summary>
    Hdr10 = 1,
    /// <summary>scRGB: BT.709 primaries and a linear transfer function over an extended range, a pixel value of one at the
    /// SDR white level of 80 nits, in a 16-bit floating-point swapchain (Vulkan <c>EXTENDED_SRGB_LINEAR_EXT</c>,
    /// <c>DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709</c>).</summary>
    ScRgb = 2,
}
