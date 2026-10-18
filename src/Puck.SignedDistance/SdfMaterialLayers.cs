using System.Numerics;

using Puck.Maths;

namespace Puck.SignedDistance;

/// <summary>A linear color and its roughness/metal response.</summary>
public readonly record struct SdfSurface(Vector3 Color, float Roughness, float Metal);
/// <summary>A surface revealed once coverage reaches Threshold, in (0, 1].</summary>
public readonly record struct SdfRevealStage(float Threshold, SdfSurface Surface);
/// <summary>One radial ramp stop. Radii are non-negative and strictly increasing.</summary>
public readonly record struct SdfRadialStop(float Radius, Vector3 Color);
/// <summary>An authored radial ramp of one to four stops, with optional angular modulation.</summary>
public sealed record SdfRadialPaint(IReadOnlyList<SdfRadialStop> Stops, float Softness = 0f, float ModulationAmplitude = 0f, float ModulationFrequency = 0f, uint Seed = 0);
/// <summary>A refractive paint plane at local z = -Depth. Origin/Rotation are relative to the winning dynamic
/// transform, or world space for static geometry. Ior = 1 samples the same plane without bending the ray.</summary>
public sealed record SdfInset(Vector3 Origin, Quaternion Rotation, float Depth, float Ior, SdfRadialPaint Paint);
/// <summary>A band of paint across a surface: where a hit lies within <paramref name="HalfWidth"/> of the plane through
/// <paramref name="Origin"/> with normal <paramref name="Normal"/>, its albedo becomes <paramref name="Color"/>, with an
/// edge antialiased over the pixel's footprint. Origin and normal are in the winning dynamic transform's frame, or world
/// space for static geometry, as an inset's are; the band paints without changing the field, so it costs the bake
/// nothing and its edge is as sharp as the pixel.</summary>
/// <param name="Origin">A point on the band's centre plane.</param>
/// <param name="Normal">The centre plane's normal, in any length but zero.</param>
/// <param name="HalfWidth">The band's half-width along the normal, in the frame's units; positive.</param>
/// <param name="Color">The band's linear-RGB albedo.</param>
public readonly record struct SdfBand(Vector3 Origin, Vector3 Normal, float HalfWidth, Vector3 Color);
/// <summary>Generic coverage controls. Edge follows curvature, Lines a triplanar pattern, and Settle upward
/// facing surfaces. Reach sets curvature sensitivity. Lane selects one of four anonymous instance channels;
/// Floor supplies minimum coverage. Under contains at most two ascending reveal thresholds. Deposit is required
/// when Settle is active. Pattern coordinates follow the winning dynamic transform.</summary>
public sealed record SdfWeathering(float Edge = 0f, float Lines = 0f, float Settle = 0f, float Reach = 1f,
    uint Seed = 0, float Scale = 1f, float Floor = 0f, int Lane = 0,
    IReadOnlyList<SdfRevealStage>? Under = null, SdfSurface? Deposit = null);
/// <summary>The shared admission rules for engine and document material layers.</summary>
public static class SdfMaterialLayers {
    private static bool Color(Vector3 v) => (VectorFunctions.IsFinite(vector: v) && (v.X >= 0f) && (v.Y >= 0f) && (v.Z >= 0f));

    public static bool NonNegative(float v) => (float.IsFinite(f: v) && (v >= 0f));

    private static bool Positive(float v) => (float.IsFinite(f: v) && (v > 0f));
    private static bool Surface(SdfSurface s) => (Color(v: s.Color) && Unit(v: s.Roughness) && Unit(v: s.Metal));
    private static bool Unit(float v) => (NonNegative(v: v) && (v <= 1f));

    /// <summary>Returns whether all layer inputs are finite and their bounded collections and ranges are valid.</summary>
    /// <param name="inset">The refractive paint layer, or <see langword="null"/> for none.</param>
    /// <param name="weathering">The coverage layer, or <see langword="null"/> for none.</param>
    /// <param name="band">The painted band, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when every present layer is admissible.</returns>
    public static bool IsValid(SdfInset? inset, SdfWeathering? weathering, SdfBand? band) {
        if (band is { } stripe) {
            if (
                !VectorFunctions.IsFinite(vector: stripe.Origin) ||
                !VectorFunctions.IsFinite(vector: stripe.Normal) ||
                (stripe.Normal.LengthSquared() < 1e-12f) ||
                !Positive(v: stripe.HalfWidth) ||
                !Color(v: stripe.Color)
            ) { return false; }
        }
        if (inset is { } layer) {
            if (
                !VectorFunctions.IsFinite(vector: layer.Origin) ||
                !float.IsFinite(f: layer.Rotation.LengthSquared()) ||
                (layer.Rotation.LengthSquared() < 1e-12f) ||
                !Positive(v: layer.Ior) ||
                !NonNegative(v: layer.Depth) ||
                (layer.Paint is not { } paint) ||
                (paint.Stops is not { Count: >= 1 and <= 4 }) ||
                !Unit(v: paint.Softness) ||
                !Unit(v: paint.ModulationAmplitude) ||
                !NonNegative(v: paint.ModulationFrequency)
            ) { return false; }
            var previous = -1f;

            foreach (var stop in paint.Stops) {
                if (
                    !NonNegative(v: stop.Radius) ||
                    (stop.Radius <= previous) ||
                    !Color(v: stop.Color)
                ) { return false; }
                previous = stop.Radius;
            }
        }
        if (weathering is { } w) {
            if (
                !Unit(v: w.Edge) ||
                !Unit(v: w.Lines) ||
                !Unit(v: w.Settle) ||
                !Positive(v: w.Reach) ||
                !NonNegative(v: w.Scale) ||
                !Unit(v: w.Floor) ||
                (((uint)w.Lane) > 3u) ||
                (w.Under is { Count: > 2 }) ||
                (((w.Edge > 0f) || (w.Lines > 0f)) && (w.Under is not { Count: > 0 })) ||
                ((w.Settle > 0f) && (w.Deposit is null))
            ) { return false; }
            var previous = 0f;

            if (w.Under is { } stages) {
                foreach (var stage in stages) {
                    if (
                        !Unit(v: stage.Threshold) ||
                        (stage.Threshold <= previous) ||
                        !Surface(s: stage.Surface)
                    ) { return false; }
                    previous = stage.Threshold;
                }
            }
            if (
                (w.Deposit is { } deposit) &&
                !Surface(s: deposit)
            ) { return false; }
        }
        return true;
    }
}
