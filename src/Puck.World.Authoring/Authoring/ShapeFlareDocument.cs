using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>The authored form of <see cref="SdfProgramBuilder.FlareY"/> — a radial flare warp scaling a shape's
/// local XZ cross-section by a profile along local Y, matching the study's <c>shinRadii</c>/<c>curvedSection</c>
/// silhouette (a limb that flares toward the hip, tapers toward the ankle, with a mid-span bulge). Admitted on every
/// primitive (<see cref="ShapeDocument.Flare"/>); composes with <see cref="ShapeDocument.Twist"/> and
/// <see cref="ShapeDocument.Bend"/> in that order — twist, then bend, then flare — on the shape's own local point,
/// before the primitive's own scale. Render-only: <c>Puck.SignedDistance.Queries.SdfFieldEvaluator</c> does not
/// interpret <see cref="SdfOp.FlareY"/>, so a shape carrying it is unreachable for deterministic field contact,
/// matching <see cref="ShapeDocument.Twist"/>/<see cref="ShapeDocument.Bend"/>'s own status.</summary>
/// <param name="Amount">The linear flare rate at the far end of the span (dimensionless; s(1) = 1 + Amount).
/// Clamped to [<see cref="MinAmount"/>, <see cref="MaxAmount"/>].</param>
/// <param name="Bulge">The mid-span sinusoidal bulge amplitude (dimensionless, peaks at the span's midpoint).
/// Clamped to [-<see cref="MaxBulge"/>, <see cref="MaxBulge"/>].</param>
/// <param name="Span">The Y distance the profile runs over, in creation units; finite and strictly greater than
/// zero, or the shape is refused at validation.</param>
/// <param name="Top">The local Y where the profile begins, in creation units (null = 0).</param>
public sealed record ShapeFlareDocument(float Amount, float Bulge, float Span, float? Top = null) {
    /// <summary>The largest authored <see cref="Amount"/>.</summary>
    public const float MaxAmount = 4f;
    /// <summary>The largest authored <see cref="Bulge"/> magnitude.</summary>
    public const float MaxBulge = 1f;
    /// <summary>The smallest authored <see cref="Amount"/> — floors s(1) at 0.1 on its own (bulge can still drive an
    /// interior t below that; see <see cref="SdfProgramBuilder.FlareMinScale"/>).</summary>
    public const float MinAmount = -0.9f;
    /// <summary>The factor a flare grows a shape's reach by — <c>max(s)</c> over the profile
    /// (<see cref="SdfProgram.FlareExtrema"/>), never below 1 since s(0) = 1 is always a candidate. A flared shape's
    /// farthest surface point sits at most this many times farther from its local axis than the un-flared one, so
    /// every cull bound (the pool's per-shape sphere, the static stamper's <c>ShapeStampBound</c>/<c>RenderReach</c>)
    /// multiplies the primitive's own reach by it; <c>SdfProgram.FlareOperatorNorm</c> ranges its shear term over the
    /// same widened radius. Null (no flare) is exactly 1.</summary>
    /// <param name="flare">The authored flare, or <see langword="null"/>.</param>
    /// <returns>The reach factor, at least 1.</returns>
    public static float ReachFactor(ShapeFlareDocument? flare) =>
        ((flare is null)
            ? 1f
            : MathF.Max(x: 1f, y: SdfProgram.FlareExtrema(amount: flare.Amount, bulge: flare.Bulge).MaxS));
    /// <summary>The worst-case <see cref="ReachFactor"/> a probe reserves: the factor at <see cref="MaxAmount"/> and
    /// <see cref="MaxBulge"/>, the flare every stamp-pool probe emits.</summary>
    public static float ProbeReachFactor { get; } = ReachFactor(flare: new ShapeFlareDocument(Amount: MaxAmount, Bulge: MaxBulge, Span: 1f));
}
