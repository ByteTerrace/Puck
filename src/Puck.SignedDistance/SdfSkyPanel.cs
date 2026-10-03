using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>A rectangular emitter at infinity. Its angular half extents are measured in radians about
/// <see cref="Direction"/> in the layer frame; its edge is widened by <see cref="Blur"/> and reflection roughness.</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public record struct SdfSkyPanel : ISdfSkyKind {
    /// <summary>The unit direction toward the panel, normalized on pack.</summary>
    [FieldOffset(0)] public Vector3 Direction;
    /// <summary>The radiance multiplier. Zero emits nothing.</summary>
    [FieldOffset(12)] public float Intensity;
    /// <summary>The linear RGB radiance.</summary>
    [FieldOffset(16)] public Vector3 Color;
    /// <summary>The angular half extents in radians, both positive.</summary>
    [FieldOffset(32)] public Vector2 Size;
    /// <summary>The angular edge softness in radians, nonnegative.</summary>
    [FieldOffset(40)] public float Blur;

    /// <inheritdoc/>
    public static SdfSkyLayerKind Kind => SdfSkyLayerKind.Panel;
    /// <inheritdoc/>
    public static SdfSkyLayerClass Class => SdfSkyLayerClass.Point;
    /// <inheritdoc/>
    public static string Name => "panel";
}
