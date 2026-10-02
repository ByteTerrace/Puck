using Puck.SignedDistance;

namespace Puck.World;

/// <summary>The numeric constraints of sky kind coordinates, shared by load-time admission and the ordinary live
/// presentation domain guard. Reciprocal values are prepared on the host so shader division never sees a flushed
/// subnormal denominator.</summary>
public static class WorldSkyNumeric {
    private const string ReciprocalRequirement = "requires a finite positive normal binary32 reciprocal";
    /// <summary>The smallest positive normal binary32 value; GPU presentation may flush smaller magnitudes.</summary>
    public const float MinimumNormal = 1.17549435e-38f;

    /// <summary>Computes a reciprocal once in double precision before narrowing to its native shader field.</summary>
    /// <param name="value">The already admitted positive authored denominator.</param>
    /// <returns>The native reciprocal.</returns>
    public static float Reciprocal(float value) => (float)(1d / value);

    private static bool ReciprocalFits(float value) => value > 0f && float.IsFinite(value) && float.IsNormal(Reciprocal(value));
    private static bool Finite(float value) => float.IsFinite(value);

    /// <summary>Resolves the authored cell fraction without a lossy intermediate binary32 divide.</summary>
    /// <param name="fraction">The positive authored radius fraction.</param>
    /// <param name="density">The positive structural cell density.</param>
    /// <returns>The base angular radius uploaded to the star record.</returns>
    public static float AngularRadius(float fraction, float density) => (float)((double)fraction / Math.Max(1d, density) * Math.PI);

    /// <summary>Prepares the star radius's consumed-format constraint.</summary>
    /// <param name="layer">The authored star row.</param>
    /// <returns>The radius fraction and structural cell density.</returns>
    public static WorldValueTuple Stars(WorldRenderSkyLayer.Stars layer) => new("radius", [
        new("radiusFraction", layer.RadiusFraction, .12f, WorldValueDomain.Positive,
            value => float.IsNormal(AngularRadius(value, layer.Density ?? SdfLighting.DefaultStarDensity)),
            "radiusFraction / max(1, density) * pi must produce a finite positive normal binary32 angular radius"),
        new("density", new BindableScalar(layer.Density ?? SdfLighting.DefaultStarDensity), SdfLighting.DefaultStarDensity, WorldValueDomain.Positive),
    ], StarsFit, "radiusFraction / max(1, density) * pi must produce a finite positive normal binary32 angular radius");

    /// <summary>Checks the host-prepared angular radius before shader coverage arithmetic.</summary>
    /// <param name="values">Radius fraction and cell density.</param>
    /// <returns>Whether the native radius is positive and survives subnormal flushing.</returns>
    public static bool StarsFit(ReadOnlySpan<float> values) => values.Length == 2 && values[0] > 0f && values[1] > 0f
        && Finite(values[0]) && Finite(values[1]) && float.IsNormal(AngularRadius(values[0], values[1]));

    /// <summary>Prepares the shared noise coordinate-scale constraint.</summary>
    /// <param name="layer">The authored noise row.</param>
    /// <returns>The prepared numeric operands.</returns>
    public static WorldValueTuple Noise(WorldRenderSkyLayer.Noise layer) => new("coordinates", [
        new("scale", layer.Scale, 1f, WorldValueDomain.Positive, ReciprocalFits, ReciprocalRequirement),
    ], NoiseFits, "scale must have a finite positive normal binary32 reciprocal for direction-to-lattice coordinates");

    /// <summary>Prepares the curtain's coordinate and ridge-width constraints.</summary>
    /// <param name="layer">The authored curtain row.</param>
    /// <returns>The prepared coupled operands.</returns>
    public static WorldValueTuple Aurora(WorldRenderSkyLayer.Aurora layer) => new("coordinates", [
        new("scale", layer.Scale, .15f, WorldValueDomain.Positive, ReciprocalFits, ReciprocalRequirement),
        new("heightScale", layer.HeightScale, .15f, WorldValueDomain.Nonnegative),
        new("width", layer.Width, .18f, WorldValueDomain.Positive, ReciprocalFits, ReciprocalRequirement),
    ], AuroraFits, "scale and width require finite positive normal binary32 reciprocals; heightScale / scale must remain finite binary32");

    /// <summary>Prepares the cloud dome, displacement and normal-stencil constraints together.</summary>
    /// <param name="layer">The authored cloud row.</param>
    /// <returns>The prepared coupled operands.</returns>
    public static WorldValueTuple Clouds(WorldRenderSkyLayer.Clouds layer) => new("coordinates", [
        new("scale", layer.Scale, SdfLighting.DefaultCloudScale, WorldValueDomain.Positive, ReciprocalFits, ReciprocalRequirement),
        new("domeRadius", layer.DomeRadius, 6f, WorldValueDomain.Nonnegative),
        new("warp", layer.Warp, .6f, WorldValueDomain.Nonnegative),
        new("normalTap", layer.NormalTap, .18f, WorldValueDomain.Positive, ReciprocalFits, ReciprocalRequirement),
        new("height", layer.Height, .7f, WorldValueDomain.Nonnegative),
        new("softness", layer.Softness, SdfLighting.DefaultCloudSoftness, new(0d, 1d, MinimumOpen: true), ReciprocalFits, ReciprocalRequirement),
        new("horizonFade", layer.HorizonFade, .05f, new(0d, 1d, MinimumOpen: true), ReciprocalFits, ReciprocalRequirement),
    ], CloudsFit, "scale, normalTap, softness and horizonFade require finite positive normal binary32 reciprocals; 2*sqrt(2*domeRadius+1)/scale + warp/2 + 2*normalTap + 8192 and height/normalTap must remain finite binary32");

    /// <summary>Checks the prepared noise coordinates. Large finite offsets are separately reduced by the shared period.</summary>
    /// <param name="values">Scale.</param>
    /// <returns>Whether the uploaded reciprocal is representable without subnormal flushing.</returns>
    public static bool NoiseFits(ReadOnlySpan<float> values) => values.Length == 1 && ReciprocalFits(values[0]);

    /// <summary>Checks the curtain's actual coordinate product and ridge reciprocal.</summary>
    /// <param name="values">Scale, height scale, width.</param>
    /// <returns>Whether the derived native operands and products remain representable.</returns>
    public static bool AuroraFits(ReadOnlySpan<float> values) => values.Length == 3 && ReciprocalFits(values[0])
        && Finite(values[1]) && values[1] >= 0f && ReciprocalFits(values[2])
        && float.IsFinite(values[1] * Reciprocal(values[0]));

    /// <summary>Checks the maximum dome coordinate, displacement and normal-stencil products in the same order
    /// consumed by the shader. The 8,192 cells cover two separately reduced periodic offsets.</summary>
    /// <param name="values">Scale, dome radius, warp, normal tap, height, softness, horizon fade.</param>
    /// <returns>Whether every ratio and bounded coordinate remains finite in binary32.</returns>
    public static bool CloudsFit(ReadOnlySpan<float> values) {
        if (values.Length != 7 || !ReciprocalFits(values[0]) || !ReciprocalFits(values[3])
            || !ReciprocalFits(values[5]) || !ReciprocalFits(values[6])) { return false; }
        if (!Finite(values[1]) || values[1] < 0f || !Finite(values[2]) || values[2] < 0f
            || !Finite(values[4]) || values[4] < 0f) { return false; }
        // A rotated component is bounded by the sum of two dome components. Evaluate positive bounds in binary32
        // too, so an intermediate rounded overflow cannot pass a double-only final-sum check.
        var coordinate = (float)(2d * Math.Sqrt(2d * values[1] + 1d)) * Reciprocal(values[0]);
        var sampled = coordinate + SdfVolume.NoisePeriodCells;
        sampled += SdfVolume.NoisePeriodCells;
        sampled += values[2] / 2f;
        sampled += 2f * values[3];
        return float.IsFinite(sampled) && float.IsFinite(values[4] * Reciprocal(values[3]));
    }
}
