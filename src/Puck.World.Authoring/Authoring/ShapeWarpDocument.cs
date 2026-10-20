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
}
