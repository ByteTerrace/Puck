using Puck.SignedDistance;

namespace Puck.World;

public abstract partial record WorldRenderSkyLayer {
    /// <summary>A seamless directional noise field colored between two endpoints. Its shared periodic lattice
    /// returns a signed fractal sum; contrast and bias map that sum into the saturated color interval once.</summary>
    /// <param name="ColorLow">The low endpoint. Absent is black.</param>
    /// <param name="ColorHigh">The high endpoint. Absent is white.</param>
    /// <param name="Intensity">The nonnegative emission gain. Absent is one; zero performs no noise work.</param>
    /// <param name="Scale">The positive size of a lattice cell in direction space. Absent is one.</param>
    /// <param name="Offset">The finite lattice offset, in cells. Absent is zero.</param>
    /// <param name="Seed">The exact 32-bit lattice seed. Absent is zero.</param>
    /// <param name="Octaves">One to eight authored octaves. Absent is four; low quality admits at most three.</param>
    /// <param name="Contrast">The finite signed-noise multiplier. Absent is one half.</param>
    /// <param name="Bias">The finite color-interval offset. Absent is one half.</param>
    [WorldSkyKind(SdfSkyLayerClass.Field, SdfSkyBlend.Over, SdfSkyVisibility.Camera)]
    public sealed record Noise(BindableColor? ColorLow = null, BindableColor? ColorHigh = null,
        BindableScalar? Intensity = null, BindableScalar? Scale = null, BindableVector3? Offset = null,
        uint? Seed = null, int? Octaves = null, BindableScalar? Contrast = null, BindableScalar? Bias = null) : WorldRenderSkyLayer;

    /// <summary>A painted checker field on the shared octahedral direction projection. The common layer transform
    /// rotates the pattern; this kind adds no private orientation or noise source.</summary>
    /// <param name="Checker">The checker grid. Absent uses 24 cells per projected axis.</param>
    /// <param name="Colors">Exactly two colors. Absent is black and white.</param>
    /// <param name="Intensity">The nonnegative emission gain. Absent is one.</param>
    /// <param name="Offset">The finite checker offset, in cells. Absent is zero.</param>
    [WorldSkyKind(SdfSkyLayerClass.Field, SdfSkyBlend.Over, SdfSkyVisibility.Camera)]
    public sealed record Pattern(WorldSkyChecker? Checker = null, IReadOnlyList<BindableColor>? Colors = null,
        BindableScalar? Intensity = null, BindableVector2? Offset = null) : WorldRenderSkyLayer;

    /// <summary>Emissive curtains formed by narrow ridges of the shared directional noise. The height scale stretches
    /// those ridges vertically; the common mask chooses the visible elevation band.</summary>
    /// <param name="Color">The curtain color. Absent is white.</param>
    /// <param name="Intensity">The nonnegative emission gain. Absent is one; zero performs no noise work.</param>
    /// <param name="Offset">The finite noise offset in cells. Absent is zero.</param>
    /// <param name="Scale">The positive noise-cell size in direction space. Absent is 0.15.</param>
    /// <param name="Width">The positive signed-noise distance from the ridge center to its edge. Absent is 0.18.</param>
    /// <param name="Sharpness">The positive ridge exponent. Absent is two.</param>
    /// <param name="HeightScale">The nonnegative vertical noise scale. Absent is 0.15.</param>
    /// <param name="Bias">The ridge center in the signed noise interval [-1, 1]. Absent is zero.</param>
    /// <param name="Seed">The exact 32-bit lattice seed. Absent is zero.</param>
    /// <param name="Octaves">One to eight authored octaves. Absent is four; low quality admits one.</param>
    [WorldSkyKind(SdfSkyLayerClass.Field, SdfSkyBlend.Add, SdfSkyVisibility.Camera)]
    public sealed record Aurora(BindableColor? Color = null, BindableScalar? Intensity = null,
        BindableVector3? Offset = null, BindableScalar? Scale = null, BindableScalar? Width = null,
        BindableScalar? Sharpness = null, BindableScalar? HeightScale = null, BindableScalar? Bias = null,
        uint? Seed = null, int? Octaves = null) : WorldRenderSkyLayer;
}

/// <summary>The structural grid of a painted sky checker.</summary>
/// <param name="Cells">The positive number of cells on each octahedral projection axis. Absent is 24.</param>
public sealed record WorldSkyChecker(int? Cells = null);
