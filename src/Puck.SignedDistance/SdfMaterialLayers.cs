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
/// <summary>Generic coverage controls. Edge follows curvature, Lines a triplanar pattern, and Settle upward
/// facing surfaces. Reach sets curvature sensitivity. Lane selects one of four anonymous instance channels;
/// Floor supplies minimum coverage. Under contains at most two ascending reveal thresholds. Deposit is required
/// when Settle is active. Noise reveals a seeded lattice mask independently of curvature. PatternScale multiplies
/// each pattern coordinate axis; all components must be positive. Softness widens the reveal ramp in coverage units.
/// Pattern coordinates follow the material frame. The pattern is one octave of the lattice read in pattern coordinates;
/// Octaves = 2 adds a second octave at OctaveRatio times the frequency, weighted OctaveGain relative to the first and
/// normalised so the sum keeps one octave's spread (each octave fades by its own pixel support). Turn rotates the lattice
/// off the pattern axes before either octave reads it; null is no turn. The defaults are the plain single, unturned
/// octave.</summary>
public sealed record SdfWeathering(float Edge = 0f, float Lines = 0f, float Settle = 0f, float Reach = 1f,
    uint Seed = 0, float Scale = 1f, float Floor = 0f, int Lane = 0,
    IReadOnlyList<SdfRevealStage>? Under = null, SdfSurface? Deposit = null, float Noise = 0f, Vector3? PatternScale = null, float Softness = 0f,
    int Octaves = 1, float OctaveRatio = 2f, float OctaveGain = 0.5f, Quaternion? Turn = null) {
    /// <summary>The most octaves of the pattern lattice a weathering reads.</summary>
    public const int MaxOctaves = 2;
}
/// <summary>The shared admission rules for engine and document material layers.</summary>
public static class SdfMaterialLayers {
    private static bool Color(Vector3 v) => (VectorFunctions.IsFinite(vector: v) && (v.X >= 0f) && (v.Y >= 0f) && (v.Z >= 0f));

    public static bool NonNegative(float v) => (float.IsFinite(f: v) && (v >= 0f));

    private static bool Positive(float v) => (float.IsFinite(f: v) && (v > 0f));
    private static bool Surface(SdfSurface s) => IsSurface(surface: s);
    private static bool Unit(float v) => (NonNegative(v: v) && (v <= 1f));

    /// <summary>Returns whether a surface is admissible: a finite, non-negative colour and a roughness and metal in
    /// [0, 1].</summary>
    /// <param name="surface">The surface.</param>
    /// <returns><see langword="true"/> for an admissible surface.</returns>
    public static bool IsSurface(SdfSurface surface) => (Color(v: surface.Color) && Unit(v: surface.Roughness) && Unit(v: surface.Metal));

    /// <summary>Returns whether all layer inputs are finite and their bounded collections and ranges are valid.</summary>
    /// <param name="inset">The refractive paint layer, or <see langword="null"/> for none.</param>
    /// <param name="weathering">The coverage layer, or <see langword="null"/> for none.</param>
    /// <param name="paints">The paints, or <see langword="null"/> for none.</param>
    /// <param name="flow">The flow layer, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when every present layer is admissible.</returns>
    public static bool IsValid(SdfInset? inset, SdfWeathering? weathering, IReadOnlyList<SdfPaint>? paints, SdfFlow? flow) {
        if ((flow is not null) && !flow.IsValid) {
            return false;
        }
        foreach (var paint in (paints ?? [])) {
            if (!SdfPaint.IsValid(paint: paint)) {
                return false;
            }
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
            var pattern = w.PatternScale ?? Vector3.One;
            if (!VectorFunctions.IsFinite(pattern) || pattern.X <= 0f || pattern.Y <= 0f || pattern.Z <= 0f ||
                !VectorFunctions.IsFinite(pattern * w.Scale) ||
                ((w.Octaves == 2) && !VectorFunctions.IsFinite((pattern * w.Scale) * w.OctaveRatio))) { return false; }
            if (
                (w.Octaves < 1) ||
                (w.Octaves > SdfWeathering.MaxOctaves) ||
                !float.IsFinite(w.OctaveRatio) ||
                (w.OctaveRatio <= 1f) ||
                !Positive(v: w.OctaveGain) ||
                (w.OctaveGain > 1f) ||
                ((w.Turn is { } turn) && (!float.IsFinite(turn.LengthSquared()) || (turn.LengthSquared() < 1e-12f)))
            ) { return false; }
            if (
                !Unit(v: w.Edge) ||
                !Unit(v: w.Lines) ||
                !Unit(v: w.Noise) ||
                !Unit(v: w.Softness) ||
                !Unit(v: w.Settle) ||
                !Positive(v: w.Reach) ||
                !NonNegative(v: w.Scale) ||
                !Unit(v: w.Floor) ||
                (((uint)w.Lane) > 3u) ||
                (w.Under is { Count: > 2 }) ||
                (((w.Edge > 0f) || (w.Lines > 0f) || (w.Noise > 0f)) && (w.Under is not { Count: > 0 })) ||
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
