using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Backend-neutral pixel format constants. Each backend maps these to its native format values.
/// </summary>
public static class GpuPixelFormat {
    /// <summary>The R8G8B8A8 unsigned normalized format.</summary>
    public const uint R8G8B8A8Unorm = 1;
    /// <summary>The B8G8R8A8 unsigned normalized format.</summary>
    public const uint B8G8R8A8Unorm = 2;

    /// <summary>Converts a <see cref="SurfaceFormat"/> to a <see cref="GpuPixelFormat"/> constant.</summary>
    public static uint FromSurfaceFormat(SurfaceFormat format) {
        return format switch {
            SurfaceFormat.B8G8R8A8Unorm => B8G8R8A8Unorm,
            SurfaceFormat.R8G8B8A8Unorm => R8G8B8A8Unorm,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: format,
                message: "The surface format has no GPU pixel format mapping.",
                paramName: nameof(format)
            ),
        };
    }
}
