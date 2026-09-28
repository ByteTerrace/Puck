using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Presentation;

/// <summary>
/// Backend-neutral presentation preferences a host registers to steer the swapchain — the present mode, the SDR
/// back-buffer format and the color space asked for — and the paper-white level the HUD and overlays compose at. Both
/// presenters (Vulkan and Direct3D 12) resolve this and honor it, falling back to a supported default when the exact
/// preference is unavailable (<see cref="DisplayOutput.TrySelect"/> for the format and color space), so the same options
/// drive either backend.
/// </summary>
public sealed class PresentationOptions {
    /// <summary>The preferred swapchain present mode. Defaults to <see cref="Presentation.PresentMode.Vsync"/>.</summary>
    public PresentMode PresentMode { get; init; } = PresentMode.Vsync;
    /// <summary>The preferred back-buffer format of an SDR output. Defaults to
    /// <see cref="GpuPixelFormat.R8G8B8A8Unorm"/>.</summary>
    public GpuPixelFormat SurfaceFormat { get; init; } = GpuPixelFormat.R8G8B8A8Unorm;
    /// <summary>The color space asked for; an HDR color space is chosen only when the display reports it, and SDR is
    /// the fallback. Defaults to <see cref="DisplayColorSpace.Srgb"/>; a World asks for another through its host
    /// section's <c>colorSpace</c>.</summary>
    public DisplayColorSpace ColorSpace { get; init; } = DisplayColorSpace.Srgb;
    /// <summary>The luminance, in nits, that SDR white shows at in an HDR output, the HUD and overlays included, which
    /// <see cref="DisplayOutput.WhiteScale"/> turns into the output's pixel value and the display encode scales the
    /// frame by; an SDR output shows white at its own white level whatever this is. Defaults to
    /// <see cref="DisplayOutput.SdrWhiteNits"/>; a World sets it through its host section's
    /// <c>paperWhiteNits</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level set is not a number, below
    /// <see cref="DisplayOutput.SdrWhiteNits"/> or above <see cref="DisplayOutput.MaxPaperWhiteNits"/>.</exception>
    public double PaperWhiteNits {
        get;
        init => field = DisplayOutput.RequirePaperWhite(nits: value);
    } = DisplayOutput.SdrWhiteNits;
}
