using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>The authored form of one <see cref="SdfProgramBuilder.GaussianPush"/> bump: a gaussian displacement of
/// the shape's local point applied before the primitive evaluates — <c>p -= Push · exp(-|(p - Center) / Radii|²)</c>
/// (see <see cref="ShapeDocument.Bumps"/>). The study's <c>faceOffset</c> cheek/muzzle bumps (pushed along local +Z)
/// are two of these. Render-only, like <see cref="ShapeDocument.Twist"/>/<see cref="ShapeDocument.Bend"/>:
/// <c>Puck.SignedDistance.Queries.SdfFieldEvaluator</c> does not interpret <see cref="SdfOp.GaussianPush"/>, so a
/// shape carrying one is unreachable for deterministic field contact.</summary>
/// <param name="Center">The bump's local center, in creation units.</param>
/// <param name="Radii">The per-axis Gaussian falloff radii, in creation units — clamped away from zero at
/// normalization (<see cref="SdfProgramBuilder.GaussianPushMinRadius"/>) so the exponent's divisor is never zero.
/// Refused by name if any authored component is negative or non-finite (a magnitude is what the field reads; a
/// negative radius states nothing a positive one does not).</param>
/// <param name="Push">The peak displacement at the center, in creation units (zero = an exact identity, still
/// admitted — it still charges the per-copy instance count).</param>
public sealed record ShapeBumpDocument(DocumentVector3 Center, DocumentVector3 Radii, DocumentVector3 Push) {
    /// <summary>The most bumps one shape's <see cref="ShapeDocument.Bumps"/> carries.</summary>
    public const int MaxBumps = 4;
    /// <summary>The largest authored <see cref="Push"/> magnitude.</summary>
    public const float MaxPushMagnitude = 2f;

    /// <summary>Returns the worst-case outward reach a shape's bumps add to its cull bound: each bump's Gaussian push
    /// can move a surface point by at most its own <see cref="Push"/> magnitude (the Gaussian weight is in [0, 1]),
    /// and the bumps compose sequentially, so the sum over the list is a sound (if pessimistic — the peaks of
    /// distinct bumps rarely align) additive bound. Zero when <paramref name="bumps"/> is null or empty.</summary>
    /// <param name="bumps">The authored bumps, or <see langword="null"/>.</param>
    /// <returns>The additive reach, in creation units, at least 0.</returns>
    public static float ReachExtra(IReadOnlyList<ShapeBumpDocument>? bumps) {
        if (bumps is not { Count: > 0 }) {
            return 0f;
        }

        var extra = 0f;

        foreach (var bump in bumps) {
            extra += bump.Push.Length();
        }

        return extra;
    }
}
