using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Backend-neutral GPU resource pixel formats. Each backend maps these to its native format values.
/// Deliberately distinct from <see cref="SurfaceFormat"/>: that enum is the presentable-surface vocabulary
/// (what a swapchain, window, or capture produces), while this one describes GPU resources (storage images,
/// render targets) and is free to grow GPU-only members with no presentable equivalent. The explicit
/// <see cref="GpuPixelFormats.FromSurfaceFormat"/> bridge marks exactly where a presentable format enters
/// GPU-resource land.
/// </summary>
public enum GpuPixelFormat : uint {
    /// <summary>The R8G8B8A8 unsigned normalized format.</summary>
    R8G8B8A8Unorm = 1,
    /// <summary>The B8G8R8A8 unsigned normalized format.</summary>
    B8G8R8A8Unorm = 2,
}

/// <summary>
/// Conversions into the <see cref="GpuPixelFormat"/> vocabulary.
/// </summary>
public static class GpuPixelFormats {
    /// <summary>Converts a <see cref="SurfaceFormat"/> to its <see cref="GpuPixelFormat"/> equivalent.</summary>
    public static GpuPixelFormat FromSurfaceFormat(SurfaceFormat format) {
        return format switch {
            SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
            SurfaceFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: format,
                message: "The surface format has no GPU pixel format mapping.",
                paramName: nameof(format)
            ),
        };
    }
}
