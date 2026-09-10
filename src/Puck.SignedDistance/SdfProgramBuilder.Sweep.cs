using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    /// <summary>The most helical strands one <see cref="Sweep"/> may declare.</summary>
    public const int MaxSweepStrands = 4;
    /// <summary>The fewest helical strands one <see cref="Sweep"/> may declare.</summary>
    public const int MinSweepStrands = 1;
    /// <summary>The largest admitted <c>|bulge|</c>, as a multiple of <c>max(radiusStart, radiusEnd)</c> — past this
    /// ratio the closed-form closest-point approximation's conservative margin (calibrated over a randomized grid;
    /// see <see cref="SweepConservativeMargin"/> and <c>SweepLawTests</c>) is no longer proven sound, so
    /// <see cref="Sweep"/> refuses the declaration by name rather than emit a field that can overestimate true
    /// distance.</summary>
    public const float MaxSweepBulgeRatio = 16f;
    /// <summary>The largest admitted <c>|radiusEnd - radiusStart|</c>, as a multiple of
    /// <c>min(radiusStart, radiusEnd)</c> — see <see cref="MaxSweepBulgeRatio"/>'s remarks; the same calibration
    /// covers taper.</summary>
    public const float MaxSweepTaperRatio = 4f;
    /// <summary>The largest admitted <see cref="Sweep"/> <c>strandOffset</c>, as a multiple of
    /// <c>max(radiusStart, radiusEnd)</c> — see <see cref="MaxSweepBulgeRatio"/>'s remarks; a strand orbiting far
    /// outside the tube's own radius is the combination the calibration grid found unsound even with a generous
    /// margin, so it is decoupled from and capped tighter than the bulge ratio.</summary>
    public const float MaxSweepStrandOffsetRatio = 2f;
    // The three conservative-margin coefficients SweepConservativeMargin folds against the curve's own authored
    // bulge/strand-offset/taper — calibrated over a randomized grid against a fine-sampled reference tube within the
    // MaxSweepBulgeRatio/MaxSweepTaperRatio/MaxSweepStrandOffsetRatio envelope above (see SweepLawTests). KEEP IN
    // SYNC with sdfSweepConservativeMargin (sdf-vm.hlsli) and the fixed-point mirror
    // (Puck.SignedDistance.Queries.SdfFieldEvaluator).
    internal const float SweepBulgeMarginFactor = 1.0f;
    internal const float SweepStrandMarginFactor = 0.7f;
    internal const float SweepTaperMarginFactor = 0.9f;

    private readonly List<SdfSweepCurve> m_sweepCurves = [];

    /// <summary>Returns the conservative margin <see cref="Sweep"/> subtracts from its raw closest-point-minus-radius
    /// candidate — see <see cref="MaxSweepBulgeRatio"/>'s remarks for what this margin covers and how it was
    /// calibrated. KEEP IN SYNC with sdfSweepConservativeMargin (sdf-vm.hlsli) and the fixed-point mirror.</summary>
    /// <param name="bulge">The mid-span radius bulge amplitude.</param>
    /// <param name="strandOffset">The strand orbit radius.</param>
    /// <param name="twist">The strand orbit rate, in turns.</param>
    /// <param name="radiusStart">The sweep radius at <c>t = 0</c>.</param>
    /// <param name="radiusEnd">The sweep radius at <c>t = 1</c>.</param>
    /// <returns>The non-negative margin.</returns>
    public static float SweepConservativeMargin(float bulge, float strandOffset, float twist, float radiusStart, float radiusEnd) =>
        ((SweepBulgeMarginFactor * MathF.Abs(x: bulge)) +
        ((SweepStrandMarginFactor * strandOffset) * (1f + MathF.Abs(x: twist)))) +
        (SweepTaperMarginFactor * MathF.Abs(x: (radiusEnd - radiusStart)));
    /// <summary>Adds a quadratic Bezier curve swept with a tapering, optionally bulging radius, optionally as
    /// helical strands orbiting the curve — see <see cref="SdfShapeType.Sweep"/>. The control points and radius
    /// endpoints live in a side table this program's own word stream carries; the instruction's Data0.x carries only
    /// a packed table-offset reference <see cref="SdfProgram"/> patches in at <see cref="Build"/>.</summary>
    /// <param name="a">The curve's first control point, in the shape's local frame.</param>
    /// <param name="b">The curve's middle control point.</param>
    /// <param name="c">The curve's last control point.</param>
    /// <param name="radiusStart">The sweep radius at <c>t = 0</c>; finite and strictly positive.</param>
    /// <param name="radiusEnd">The sweep radius at <c>t = 1</c>; finite and strictly positive.</param>
    /// <param name="bulge">The mid-span radius bulge amplitude, added by <c>bulge·sin(π·t)^0.65</c>; refused past
    /// <see cref="MaxSweepBulgeRatio"/> times <c>max(radiusStart, radiusEnd)</c>.</param>
    /// <param name="strands">The helical strand count, in [<see cref="MinSweepStrands"/>, <see cref="MaxSweepStrands"/>].</param>
    /// <param name="twist">The strand orbit rate, in turns along the curve; finite.</param>
    /// <param name="strandOffset">The strand orbit radius, in creation units; non-negative, finite, refused past
    /// <see cref="MaxSweepStrandOffsetRatio"/> times <c>max(radiusStart, radiusEnd)</c>.</param>
    /// <param name="material">The material index assigned to the shape.</param>
    /// <param name="blend">The operation used to combine the shape with the accumulated field.</param>
    /// <param name="smooth">The blend smoothing radius.</param>
    /// <param name="detail">Whether the emitted shape instruction is shading-only (<see cref="SdfInstruction.Detail"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="a"/>/<paramref name="b"/>/<paramref name="c"/>
    /// is not finite; <paramref name="radiusStart"/>/<paramref name="radiusEnd"/> is not finite and strictly
    /// positive; <paramref name="bulge"/>/<paramref name="twist"/> is not finite; <paramref name="strandOffset"/> is
    /// not finite and non-negative; <paramref name="strands"/> is outside [<see cref="MinSweepStrands"/>,
    /// <see cref="MaxSweepStrands"/>]; or <paramref name="bulge"/>/<paramref name="strandOffset"/>/the radius taper
    /// exceeds the ratio this shape's conservative margin is calibrated against (see
    /// <see cref="MaxSweepBulgeRatio"/>).</exception>
    public SdfProgramBuilder Sweep(Vector3 a, Vector3 b, Vector3 c, float radiusStart, float radiusEnd, float bulge, int strands, float twist, float strandOffset, int material, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, bool detail = false) {
        RequireFinite(value: a, paramName: nameof(a), subject: "A sweep control point");
        RequireFinite(value: b, paramName: nameof(b), subject: "A sweep control point");
        RequireFinite(value: c, paramName: nameof(c), subject: "A sweep control point");
        RequirePositive(value: radiusStart, paramName: nameof(radiusStart), subject: "A sweep start radius");
        RequirePositive(value: radiusEnd, paramName: nameof(radiusEnd), subject: "A sweep end radius");
        RequireFinite(value: bulge, paramName: nameof(bulge), subject: "A sweep bulge");
        RequireFinite(value: twist, paramName: nameof(twist), subject: "A sweep twist rate");
        RequireNonNegative(value: strandOffset, paramName: nameof(strandOffset), subject: "A sweep strand offset");

        if (
            (strands < MinSweepStrands) ||
            (strands > MaxSweepStrands)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(strands),
                message: $"A sweep must declare {MinSweepStrands}..{MaxSweepStrands} strands; got {strands}."
            );
        }

        var maxRadius = MathF.Max(x: radiusStart, y: radiusEnd);
        var minRadius = MathF.Min(x: radiusStart, y: radiusEnd);

        if (MathF.Abs(x: bulge) > (MaxSweepBulgeRatio * maxRadius)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(bulge),
                message: $"A sweep bulge magnitude ({MathF.Abs(x: bulge)}) exceeds {MaxSweepBulgeRatio} times the largest authored radius ({maxRadius}) — past this ratio the conservative margin SdfProgramBuilder.SweepConservativeMargin is calibrated against is no longer proven sound. Shrink the bulge or widen the radius."
            );
        }

        if (MathF.Abs(x: (radiusEnd - radiusStart)) > (MaxSweepTaperRatio * minRadius)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(radiusEnd),
                message: $"A sweep's radius taper (|{radiusEnd} - {radiusStart}|) exceeds {MaxSweepTaperRatio} times the smaller authored radius ({minRadius}) — past this ratio the conservative margin is no longer proven sound. Shrink the taper or raise the smaller radius."
            );
        }

        if (strandOffset > (MaxSweepStrandOffsetRatio * maxRadius)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(strandOffset),
                message: $"A sweep strand offset ({strandOffset}) exceeds {MaxSweepStrandOffsetRatio} times the largest authored radius ({maxRadius}) — past this ratio the conservative margin is no longer proven sound. Shrink the offset or widen the radius."
            );
        }

        var instructionIndex = m_instructions.Count;

        m_sweepCurves.Add(item: new SdfSweepCurve(
            A: a,
            B: b,
            Bulge: bulge,
            C: c,
            InstructionIndex: instructionIndex,
            RadiusEnd: radiusEnd,
            RadiusStart: radiusStart
        ));

        // Data0.x carries a placeholder (SdfProgram.Build patches in the real table offset once every curve's
        // side-table layout is known); Data0.y/z/w carry strands/twist/strandOffset directly (small values, always
        // exact in float) — no table lookup needed for those at evaluation time.
        return Shape(
            blend: blend,
            detail: detail,
            dimensions: new Vector4(
                w: strandOffset,
                x: 0f,
                y: strands,
                z: twist
            ),
            material: material,
            shape: SdfShapeType.Sweep,
            smooth: smooth
        );
    }
}
