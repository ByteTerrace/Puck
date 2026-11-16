using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>The authored form of <see cref="SdfProgramBuilder.DomainWarp"/>: a bounded, cross-coupled sinusoidal
/// warp of the shape's own local point (each axis moved by the sine of the next axis's coordinate) before its
/// primitive evaluates, an organic wobble. Admitted on every primitive (<see cref="ShapeDocument.Warp"/>); applied
/// after <see cref="ShapeDocument.Bumps"/>, before <see cref="ShapeDocument.Elongate"/> and the primitive's own
/// scale. Render-only, like <see cref="ShapeDocument.Flare"/>: contact rides the unwarped primitive.</summary>
/// <param name="Frequency">The per-axis angular frequency, radians per creation unit (each clamped to
/// [-<see cref="MaxFrequency"/>, <see cref="MaxFrequency"/>]).</param>
/// <param name="Amplitude">The farthest the point moves per axis, creation units (clamped to
/// [-<see cref="MaxAmplitude"/>, <see cref="MaxAmplitude"/>]).</param>
public sealed record ShapeWarpDocument(DocumentVector3 Frequency, float Amplitude) {
    /// <summary>The largest authored <see cref="Amplitude"/> magnitude.</summary>
    public const float MaxAmplitude = 0.2f;
    /// <summary>The largest authored frequency magnitude per axis.</summary>
    public const float MaxFrequency = 64f;

    /// <summary>The farthest the warp carries any point: the amplitude on every axis at once.</summary>
    public float Reach => (MathF.Abs(x: Amplitude) * MathF.Sqrt(x: 3f));
    /// <summary>Gets the tightest radius, in creation units, the warp bends a flat surface to (<see cref="ReliefRadius"/>).</summary>
    public float CurvatureRadius => ReliefRadius(amplitude: Amplitude, frequency: Frequency.Value);

    /// <summary>Returns the tightest radius of curvature a sine of <paramref name="amplitude"/> at
    /// <paramref name="frequency"/> bends a flat surface to: one over amplitude times the largest frequency squared, the
    /// curvature at a crest. A shape's bake detail reads it as a round section's radius, since a lattice sized by the
    /// shape's extent steps over a relief whose wavelength is a few of its cells.</summary>
    /// <param name="amplitude">The sine's amplitude, creation units.</param>
    /// <param name="frequency">The per-axis angular frequency, radians per creation unit.</param>
    /// <returns>The radius, creation units; positive infinity for a zero amplitude or frequency.</returns>
    public static float ReliefRadius(float amplitude, Vector3 frequency) {
        var magnitude = Vector3.Abs(value: frequency);
        var peak = MathF.Max(x: magnitude.X, y: MathF.Max(x: magnitude.Y, y: magnitude.Z));
        var bend = (MathF.Abs(x: amplitude) * (peak * peak));

        return ((bend > 0f) ? (1f / bend) : float.PositiveInfinity);
    }
}
/// <summary>The authored form of <see cref="SdfProgramBuilder.Displace"/>: a bounded separable sine relief added to
/// the shape's own field inside its own scope, the SDF-native height map (the relief is real geometry, so it shadows
/// and self-occludes). Admitted on every closed primitive (<see cref="ShapeDocument.Displace"/>), evaluated at the
/// shape's rigid frame like <see cref="ShapeDocument.Cells"/>. Render-only: contact rides the undisplaced
/// primitive.</summary>
/// <param name="Frequency">The per-axis angular frequency, radians per creation unit (each clamped to
/// [-<see cref="MaxFrequency"/>, <see cref="MaxFrequency"/>]).</param>
/// <param name="Amplitude">The relief's height either way, creation units (clamped to
/// [-<see cref="MaxAmplitude"/>, <see cref="MaxAmplitude"/>]).</param>
public sealed record ShapeDisplaceDocument(DocumentVector3 Frequency, float Amplitude) {
    /// <summary>The largest authored <see cref="Amplitude"/> magnitude.</summary>
    public const float MaxAmplitude = 0.2f;
    /// <summary>The largest authored frequency magnitude per axis.</summary>
    public const float MaxFrequency = 64f;

    /// <summary>The farthest the relief carries the surface outward.</summary>
    public float Reach => MathF.Abs(x: Amplitude);
    /// <summary>Gets the tightest radius, in creation units, the relief bends a flat face to
    /// (<see cref="ShapeWarpDocument.ReliefRadius"/>).</summary>
    public float CurvatureRadius => ShapeWarpDocument.ReliefRadius(amplitude: Amplitude, frequency: Frequency.Value);
}
