using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Presentation;

/// <summary>
/// Backend-neutral presentation preferences a host registers to steer the swapchain — the present mode and the
/// back-buffer surface format. Both presenters (Vulkan and Direct3D 12) resolve this and honor it, falling back
/// to a supported default when the exact preference is unavailable, so the same options drive either backend.
/// </summary>
public sealed class PresentationOptions {
    /// <summary>The preferred swapchain present mode. Defaults to <see cref="Presentation.PresentMode.Vsync"/>.</summary>
    public PresentMode PresentMode { get; init; } = PresentMode.Vsync;
    /// <summary>The preferred back-buffer format. Defaults to <see cref="GpuPixelFormat.R8G8B8A8Unorm"/>.</summary>
    public GpuPixelFormat SurfaceFormat { get; init; } = GpuPixelFormat.R8G8B8A8Unorm;
}
