using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.World;

public abstract partial record WorldRenderSkyLayer {
    /// <summary>An equirectangular frame sampled by direction. It uses the same producer, camera and probe sources
    /// as other world frame consumers, with longitude wrapping and latitude clamping at the poles.</summary>
    /// <param name="Source">The existing frame source; its output remains owned and leased by the ordinary source instance.</param>
    /// <param name="Tint">The linear RGB multiplier. Absent is white.</param>
    /// <param name="Intensity">The nonnegative emission gain. Absent is one; zero performs no texture loads.</param>
    /// <param name="Filter">Nearest or linear filtering. Absent is linear.</param>
    [WorldSkyKind(SdfSkyLayerClass.Field, SdfSkyBlend.Over, SdfSkyVisibility.Camera)]
    public sealed record Panorama(WorldFrameSource Source, BindableColor? Tint = null,
        BindableScalar? Intensity = null, GpuSamplerFilter? Filter = null) : WorldRenderSkyLayer;
}
