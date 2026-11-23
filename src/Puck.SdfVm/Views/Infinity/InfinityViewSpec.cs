using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Maths;

namespace Puck.SdfVm.Views;

/// <summary>What an infinity view renders: the session of another world, or only prototypes of the viewer's own.</summary>
public enum InfinityViewKind : byte {
    /// <summary>A destination world, drawn from its endpoint's mirror as a session screen draws it.</summary>
    World = 0,
    /// <summary>Far geometry: a residency that holds only the named prototypes of a world.</summary>
    Far = 1,
}
/// <summary>The extras an infinity view's dressing turns on. Both are off unless a view asks: an infinity view is dressed
/// at reduced cost.</summary>
[Flags]
public enum InfinityViewLevers : byte {
    /// <summary>Neither.</summary>
    None = 0,
    /// <summary>The key light's soft shadow.</summary>
    Shadows = 1,
    /// <summary>Ambient occlusion.</summary>
    AmbientOcclusion = 2,
}
/// <summary>
/// The sky region an infinity view covers, as the cone that bounds it: every direction within
/// <paramref name="HalfAngle"/> of <paramref name="Axis"/>. A layer's mask of any shape (a disc, a band, a planet's
/// bound) is covered by the cone around it, and <see cref="InfinityViewFrame"/> renders only the rectangle that cone
/// projects to.
/// </summary>
/// <param name="Axis">The cone's axis in the viewer's world frame, any nonzero length (normalized by
/// <see cref="Direction"/>).</param>
/// <param name="HalfAngle">The cone's half-angle in radians, in <c>(0, π/2)</c>.</param>
public readonly record struct InfinityViewMask(Vector3 Axis, float HalfAngle) {
    /// <summary>Gets the unit axis.</summary>
    public Vector3 Direction => Vector3.Normalize(value: Axis);
}
/// <summary>The sphere that bounds what a view shows, in its source's coordinates: a moon, a planet, an island. A view
/// that states one renders only the cone the sphere subtends from its camera, wherever that camera stands.</summary>
/// <param name="Center">The sphere's centre in the source.</param>
/// <param name="Radius">The sphere's radius, positive.</param>
public readonly record struct InfinityViewBound(Vector3 Center, float Radius);
/// <summary>
/// One infinity view, the neutral record the host renders: a second <c>sdf.world</c> instance seen from an anchor in its
/// source and turned with the viewer's camera. By default the camera stays at the anchor whatever the viewer does, so
/// the source is a backdrop at infinity; a view that <see cref="Follows"/> moves its camera with the viewer's, so the
/// source is a place at a distance that grows as the viewer nears it. The sky layer and body shape that author one
/// (<c>view</c> and <c>far</c>) lower to this record, and every rule below it (the rect rendered, the frames that
/// render it, the nesting, the cap) reads nothing else.
/// </summary>
/// <param name="Name">The layer's or body's name, free of the generated-name joiner; the instance is
/// <c>sky$&lt;name&gt;</c>.</param>
/// <param name="Kind">What it renders.</param>
/// <param name="Anchor">The point in the source the camera sits at, whatever the viewer does; for a view that
/// <see cref="Follows"/>, the point of the source the viewer's world origin stands at.</param>
/// <param name="Orientation">The rotation that carries a direction of the viewer's world into the source's. Identity
/// shows the source's axes as the viewer's.</param>
/// <param name="Mask">The region it covers, or <see langword="null"/> for the whole sky: the viewer's whole frustum.</param>
/// <param name="Scale">Its render scale against the viewer's pixel density, in <c>(0, 1]</c>. Below
/// <see cref="QualityTier.High"/> the view renders at half of it (<see cref="EffectiveScale"/>).</param>
/// <param name="Refresh">It renders at most once every this many frames, at least 1.</param>
/// <param name="Levers">The extras its dressing turns on.</param>
/// <param name="FarDistance">Its own far distance in world units, the depth its march ends at.</param>
/// <param name="Fallback">The linear colour its layer draws where the view cannot render: past the nesting depth, or
/// before its first image.</param>
/// <param name="MinimumTier">The lowest tier it draws at; below it the view is not drawn at all.</param>
public sealed record InfinityViewSpec(
    string Name,
    InfinityViewKind Kind,
    Vector3 Anchor,
    Quaternion Orientation,
    InfinityViewMask? Mask,
    float Scale,
    int Refresh,
    InfinityViewLevers Levers,
    float FarDistance,
    Vector3 Fallback,
    QualityTier MinimumTier
) {
    /// <summary>The factor a view below <see cref="QualityTier.High"/> scales its render by.</summary>
    public const float ReducedScale = 0.5f;

    /// <summary>Gets a value indicating whether the camera moves with the viewer's: it sits at <see cref="Anchor"/>
    /// plus the viewer's position carried by <see cref="Orientation"/>, so the two worlds share one frame.</summary>
    public bool Follows { get; init; }
    /// <summary>Gets the sphere that bounds what the view shows, or <see langword="null"/> for none. With one and no
    /// <see cref="Mask"/>, the view renders the cone the sphere subtends from its camera.</summary>
    public InfinityViewBound? Bound { get; init; }
    /// <summary>Gets a value indicating whether a view of a world draws only that world's geometry, its image alpha
    /// the geometry's coverage, so the viewer's own sky shows around it.</summary>
    public bool GeometryCoverage { get; init; }
    /// <summary>Gets a value indicating whether the view's image alpha is its coverage: far geometry always, a world
    /// when it asks for <see cref="GeometryCoverage"/>.</summary>
    public bool Covers => ((Kind == InfinityViewKind.Far) || GeometryCoverage);

    /// <summary>Returns the scale the view renders at on a tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns><see cref="Scale"/> at <see cref="QualityTier.High"/>, half of it below.</returns>
    public float EffectiveScale(QualityTier tier) => ((tier == QualityTier.High)
        ? Scale
        : (Scale * ReducedScale));
    /// <summary>Returns whether the view draws on a tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns><see langword="true"/> at or above <see cref="MinimumTier"/>.</returns>
    public bool DrawsAt(QualityTier tier) => (tier >= MinimumTier);
    /// <summary>Checks the record: every number finite and in its range, the name a single part.</summary>
    /// <param name="reason">The first fault by name, or empty.</param>
    /// <returns><see langword="true"/> when the record is sound.</returns>
    public bool TryValidate(out string reason) {
        if (string.IsNullOrEmpty(value: Name) || Name.Contains(value: '$') || Name.Contains(value: '~')) {
            reason = $"an infinity view's name is one part, free of '$' and '~'; got '{Name}'.";

            return false;
        }
        if (!(Scale is > 0f and <= 1f)) {
            reason = $"infinity view '{Name}' has scale {Scale}; a scale lies in (0, 1].";

            return false;
        }
        if (Refresh < 1) {
            reason = $"infinity view '{Name}' has refresh {Refresh}; it renders at most once every N frames, N at least 1.";

            return false;
        }
        if (!(float.IsFinite(f: FarDistance) && (FarDistance > 0f))) {
            reason = $"infinity view '{Name}' has far distance {FarDistance}; it is finite and positive.";

            return false;
        }
        if (!VectorFunctions.IsFinite(vector: Anchor)) {
            reason = $"infinity view '{Name}' has a non-finite anchor.";

            return false;
        }
        if (!VectorFunctions.IsFinite(vector: Fallback)) {
            reason = $"infinity view '{Name}' has a non-finite fallback colour.";

            return false;
        }
        if (!(VectorFunctions.IsFinite(rotation: Orientation) && (MathF.Abs(x: (Orientation.Length() - 1f)) < 1e-3f))) {
            reason = $"infinity view '{Name}' has an orientation that is not a unit rotation.";

            return false;
        }
        if ((Bound is { } bound) && !(VectorFunctions.IsFinite(vector: bound.Center) && float.IsFinite(f: bound.Radius) && (bound.Radius > 0f))) {
            reason = $"infinity view '{Name}' has a bound that is not a finite centre and a positive radius.";

            return false;
        }
        if (Mask is { } mask) {
            var length = mask.Axis.Length();

            if (!(float.IsFinite(f: length) && (length > 1e-6f))) {
                reason = $"infinity view '{Name}' has a mask axis of no direction.";

                return false;
            }
            if (!((mask.HalfAngle > 0f) && (mask.HalfAngle < (MathF.PI / 2f)))) {
                reason = $"infinity view '{Name}' has a mask half-angle of {mask.HalfAngle} rad; a cone's half-angle lies in (0, π/2).";

                return false;
            }
        }

        reason = string.Empty;

        return true;
    }
}
