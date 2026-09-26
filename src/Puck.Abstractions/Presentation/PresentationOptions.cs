using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Presentation;

/// <summary>
/// Backend-neutral presentation preferences a host registers to steer the swapchain — the present mode, the SDR
/// back-buffer format and the color space asked for. Both presenters (Vulkan and Direct3D 12) resolve this and honor it,
/// falling back to a supported default when the exact preference is unavailable (<see cref="DisplayOutput.TrySelect"/>
/// for the format and color space), so the same options drive either backend.
/// </summary>
public sealed class PresentationOptions {
    /// <summary>The preferred swapchain present mode. Defaults to <see cref="Presentation.PresentMode.Vsync"/>.</summary>
    public PresentMode PresentMode { get; init; } = PresentMode.Vsync;
    /// <summary>The preferred back-buffer format of an SDR output. Defaults to
    /// <see cref="GpuPixelFormat.R8G8B8A8Unorm"/>.</summary>
    public GpuPixelFormat SurfaceFormat { get; init; } = GpuPixelFormat.R8G8B8A8Unorm;
    /// <summary>The color space asked for; an HDR color space is chosen only when the display reports it. Defaults to
    /// <see cref="DisplayColorSpace.Srgb"/>, which no host changes: nothing encodes a frame for an HDR display
    /// yet.</summary>
    public DisplayColorSpace ColorSpace { get; init; } = DisplayColorSpace.Srgb;
}
