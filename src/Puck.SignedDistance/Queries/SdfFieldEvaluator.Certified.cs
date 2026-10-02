using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>How a certified sweep ended.</summary>
public enum SdfCertifiedSweepOutcome {
    /// <summary>The whole displacement is certified clear.</summary>
    Clear = 0,
    /// <summary>The sweep stopped where the certified clearance no longer exceeds the radius: a surface may lie within
    /// one more step.</summary>
    Contact = 1,
    /// <summary>The step budget ran out first; the fraction reached is still certified clear.</summary>
    Exhausted = 2,
}
/// <summary>A certified sweep's answer: how far along the displacement the sphere is proved clear, and why it
/// stopped.</summary>
/// <param name="Fraction">The fraction of the displacement, in <c>[0, 1]</c> and floored to the Q16 grid, along which
/// every point of the moving sphere is proved to keep the field above its radius.</param>
/// <param name="Reached">A representable centre at the end of the certified travel, itself proved clear: the sweep
/// carries its fraction at 2⁻³² of the displacement, finer than <paramref name="Fraction"/>, so this is where a
/// fast body may be placed.</param>
/// <param name="Outcome">Why the sweep stopped.</param>
/// <param name="Steps">The bounds queries the sweep spent, each over one segment's box.</param>
public readonly record struct SdfCertifiedSweep(FixedQ4816 Fraction, FixedPosition Reached, SdfCertifiedSweepOutcome Outcome, int Steps);
/// <summary>A certified line of sight's verdict.</summary>
public enum SdfCertifiedVisibility {
    /// <summary>Every point of the segment is proved outside the solid.</summary>
    Clear = 0,
    /// <summary>A point of the segment is proved inside the solid.</summary>
    Blocked = 1,
    /// <summary>The box budget ran out before either could be proved.</summary>
    Undecided = 2,
}
// THE CERTIFIED QUERIES, built on TryDistanceBounds. Each answers about the exact segment between its endpoints, never a
// sampled approximation of it: a segment's bounding box is formed with outward rounding, so it holds every real point
// of the segment, and the box's interval bounds the field there.
public sealed partial class SdfFieldEvaluator {
    private const int CertifiedSweepStepBudget = 4096;
    private const int CertifiedLineOfSightBoxBudget = 4096;

    /// <summary>Advances a sphere along a displacement by conservative advancement, certifying every step: the step
    /// is the certified clearance at the current point divided by the program's Lipschitz bound, and each step's whole
    /// segment is then proved clear by one bounds query over its box, halving the step until the proof holds.</summary>
    /// <param name="origin">The sphere's centre at the start.</param>
    /// <param name="displacement">The whole motion; the answer is a fraction of it.</param>
    /// <param name="radius">The sphere's radius, at least zero.</param>
    /// <param name="sweep">The certified fraction, the outcome and the steps on success.</param>
    /// <returns><see langword="false"/> when the program has no shape or the origin leaves the evaluator's frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative.</exception>
    /// <exception cref="NotSupportedException">The program holds an instruction with no inclusion rule.</exception>
    /// <remarks>Certified means proved, not estimated: every point of the swept sphere up to the returned fraction keeps
    /// both the exact field and every fixed-point answer above zero, however fast the motion, so a thin wall is never
    /// crossed between two samples. The Lipschitz bound only proposes a step; the bounds query proves it, so the answer
    /// does not rest on the bound being exact for the program's quantized constants.</remarks>
    public bool TryCertifiedSweep(FixedPosition origin, FixedVector3 displacement, FixedQ4816 radius, out SdfCertifiedSweep sweep) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: radius, other: FixedQ4816.Zero);

        sweep = default;

        if (!m_hasShape || !origin.TryDelta(delta: out var start, origin: FixedPosition.Zero)) {
            return false;
        }

        if (!FixedDirectedRounding.TryCeilingMagnitude(
            result: out var lengthRaw,
            x: displacement.X.Value,
            y: displacement.Y.Value,
            z: displacement.Z.Value
        )) {
            lengthRaw = long.MaxValue;
        }

        // The fraction runs at Q32 of the displacement, so a step of one unit of it moves a long sweep a sliver.
        const int FractionBits = 32;
        const long Whole = (1L << FractionBits);
        var fraction = 0L;
        var steps = 0;
        var outcome = SdfCertifiedSweepOutcome.Exhausted;

        while (steps < CertifiedSweepStepBudget) {
            if (fraction >= Whole) {
                outcome = SdfCertifiedSweepOutcome.Clear;
                break;
            }

            var here = SweepBounds(displacement: displacement, from: fraction, start: start, to: fraction);
            var clearance = (here.Lower - radius);

            ++steps;

            if ((clearance <= FixedQ4816.Zero) || here.IsUnboundedBelow) {
                outcome = SdfCertifiedSweepOutcome.Contact;
                break;
            }

            // The Lipschitz ball's radius, floored, as a fraction of the displacement's length rounded up.
            var reach = ((((Int128)clearance.Value) * m_stepScale.Value) >> FixedQ4816.FractionBitCount);
            var proposed = ((lengthRaw == 0L)
                ? Whole
                : (long)Int128.Min(x: Whole, y: ((reach << FractionBits) / lengthRaw)));
            var next = Math.Min(val1: Whole, val2: (fraction + Math.Max(val1: proposed, val2: 1L)));

            // Prove the whole segment; halve the step until it holds.
            while (SweepBounds(displacement: displacement, from: fraction, start: start, to: next).Lower <= radius) {
                ++steps;

                var half = ((next - fraction) >> 1);

                if (half == 0L) {
                    next = fraction;
                    outcome = SdfCertifiedSweepOutcome.Contact;
                    break;
                }

                next = (fraction + half);
            }

            if (next == fraction) {
                break;
            }

            fraction = next;
        }

        if ((outcome == SdfCertifiedSweepOutcome.Exhausted) && (fraction >= Whole)) {
            outcome = SdfCertifiedSweepOutcome.Clear;
        }

        // The point box at the reached fraction lies inside the last certified segment's box, so any corner of it is
        // proved clear.
        var reached = SweepPoint(displacement: displacement, fraction: fraction, start: start);

        sweep = new(
            Fraction: FixedQ4816.FromRawBits(value: (fraction >> (FractionBits - FixedQ4816.FractionBitCount))),
            Reached: FixedPosition.FromLocal(local: reached),
            Outcome: outcome,
            Steps: steps
        );
        return true;
    }
    /// <summary>Decides whether the segment between two points stays outside the solid, by proof: the segment is split
    /// into boxes until each box's bounds are above zero (clear) or a point of it is proved inside (blocked).</summary>
    /// <param name="from">One end of the segment.</param>
    /// <param name="to">The other end.</param>
    /// <param name="visibility">The verdict on success.</param>
    /// <returns><see langword="false"/> when the program has no shape or an end leaves the evaluator's frame.</returns>
    /// <exception cref="NotSupportedException">The program holds an instruction with no inclusion rule.</exception>
    /// <remarks>Unlike <see cref="LineOfSight"/>, which marches samples and so can step over a surface thinner than its
    /// step, this answers <see cref="SdfCertifiedVisibility.Clear"/> only when no point of the segment can be inside,
    /// and <see cref="SdfCertifiedVisibility.Blocked"/> only when one surely is; anything it cannot prove within its
    /// box budget is <see cref="SdfCertifiedVisibility.Undecided"/>.</remarks>
    public bool TryCertifiedLineOfSight(FixedPosition from, FixedPosition to, out SdfCertifiedVisibility visibility) {
        visibility = SdfCertifiedVisibility.Undecided;

        if (
            !m_hasShape ||
            !from.TryDelta(delta: out var start, origin: FixedPosition.Zero) ||
            !to.TryDelta(delta: out var end, origin: FixedPosition.Zero)
        ) {
            return false;
        }

        var displacement = (end - start);
        var one = FixedQ4816.One.Value;
        var pending = new Stack<(long From, long To)>();
        var boxes = 0;

        pending.Push(item: (0L, one));

        while (pending.TryPop(result: out var segment)) {
            if (++boxes > CertifiedLineOfSightBoxBudget) {
                return true;
            }

            var bounds = SegmentBounds(displacement: displacement, from: segment.From, start: start, to: segment.To);

            if (bounds.Lower > FixedQ4816.Zero) {
                continue;
            }

            var middle = (segment.From + ((segment.To - segment.From) >> 1));

            // A point of the segment whose own tiny box lies wholly below zero is surely inside.
            if (SegmentBounds(displacement: displacement, from: middle, start: start, to: middle).Upper < FixedQ4816.Zero) {
                visibility = SdfCertifiedVisibility.Blocked;
                return true;
            }

            if ((segment.To - segment.From) <= 1L) {
                return true;
            }

            pending.Push(item: (middle, segment.To));
            pending.Push(item: (segment.From, middle));
        }

        visibility = SdfCertifiedVisibility.Clear;
        return true;
    }

    // The bounds over the box of start + displacement·t for t in [from, to], fractions at Q32 (read as Q16 values scaled
    // by 2¹⁶, then divided back out with outward rounding).
    private FixedInterval SweepBounds(FixedVector3 start, FixedVector3 displacement, long from, long to) {
        var box = SweepBox(displacement: displacement, from: from, start: start, to: to);

        return (TryDistanceBounds(
            distance: out var bounds,
            lower: FixedPosition.FromLocal(local: new FixedVector3(X: box.X.Lower, Y: box.Y.Lower, Z: box.Z.Lower)),
            upper: FixedPosition.FromLocal(local: new FixedVector3(X: box.X.Upper, Y: box.Y.Upper, Z: box.Z.Upper))
        )
            ? bounds
            : FixedInterval.Entire);
    }
    private static FixedVector3 SweepPoint(FixedVector3 start, FixedVector3 displacement, long fraction) {
        var box = SweepBox(displacement: displacement, from: fraction, start: start, to: fraction);

        return new(X: box.X.Lower, Y: box.Y.Lower, Z: box.Z.Lower);
    }
    private static IntervalVector3 SweepBox(FixedVector3 start, FixedVector3 displacement, long from, long to) {
        var t = new FixedInterval(lower: FixedQ4816.FromRawBits(value: from), upper: FixedQ4816.FromRawBits(value: to));
        var scale = FixedInterval.FromPoint(value: FixedQ4816.FromInteger(value: (1L << FixedQ4816.FractionBitCount)));

        return new(
            X: (FixedInterval.FromPoint(value: start.X) + ((FixedInterval.FromPoint(value: displacement.X) * t) / scale)),
            Y: (FixedInterval.FromPoint(value: start.Y) + ((FixedInterval.FromPoint(value: displacement.Y) * t) / scale)),
            Z: (FixedInterval.FromPoint(value: start.Z) + ((FixedInterval.FromPoint(value: displacement.Z) * t) / scale))
        );
    }
    // The bounds over the bounding box of start + displacement·t for t in [from, to] (raw fractions), each corner formed
    // with outward rounding so the box holds the real segment.
    private FixedInterval SegmentBounds(FixedVector3 start, FixedVector3 displacement, long from, long to) {
        var t = new FixedInterval(lower: FixedQ4816.FromRawBits(value: from), upper: FixedQ4816.FromRawBits(value: to));
        var x = (FixedInterval.FromPoint(value: start.X) + (FixedInterval.FromPoint(value: displacement.X) * t));
        var y = (FixedInterval.FromPoint(value: start.Y) + (FixedInterval.FromPoint(value: displacement.Y) * t));
        var z = (FixedInterval.FromPoint(value: start.Z) + (FixedInterval.FromPoint(value: displacement.Z) * t));

        return (TryDistanceBounds(
            distance: out var bounds,
            lower: FixedPosition.FromLocal(local: new FixedVector3(X: x.Lower, Y: y.Lower, Z: z.Lower)),
            upper: FixedPosition.FromLocal(local: new FixedVector3(X: x.Upper, Y: y.Upper, Z: z.Upper))
        )
            ? bounds
            : FixedInterval.Entire);
    }
}
