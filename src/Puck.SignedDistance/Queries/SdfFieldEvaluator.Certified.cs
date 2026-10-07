using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>A certified line of sight's verdict.</summary>
public enum SdfCertifiedVisibility {
    /// <summary>Every point of the segment is proved outside the solid.</summary>
    Clear = 0,
    /// <summary>A point of the segment is proved inside the solid.</summary>
    Blocked = 1,
    /// <summary>Neither could be proved: the caller's bounds-query budget ran out, or a one-raw piece of the segment
    /// straddles the surface too closely for its box to decide.</summary>
    Undecided = 2,
}
/// <summary>A certified line of sight's answer: the verdict and what it cost.</summary>
/// <param name="Visibility">The verdict.</param>
/// <param name="BoundsQueries">The bounds queries spent, each one walk of the program over one box: never more than the
/// caller's budget, and never more than <see cref="SdfFieldEvaluator.CertifiedLineOfSightMaximumBoundsQueries"/>
/// whatever the budget.</param>
public readonly record struct SdfCertifiedSight(SdfCertifiedVisibility Visibility, int BoundsQueries);
// THE CERTIFIED QUERIES, built on TryDistanceBounds. Each answers about the exact segment between its endpoints, never a
// sampled approximation of it: a segment's bounding box is formed with outward rounding, so it holds every real point
// of the segment, and the box's interval bounds the field there.
public sealed partial class SdfFieldEvaluator : IFieldBounds, ICertifiedSweepQuery {
    /// <summary>The most bounds queries <see cref="TryCertifiedLineOfSight"/> can spend on one segment, whatever its
    /// budget: the segment splits on the Q16 grid of its own length, so a piece one raw long is never split and no
    /// piece lies deeper than sixteen halvings. That bounds the pieces examined by 2¹⁷ − 1, and each costs at most two
    /// queries, its box and its midpoint's.</summary>
    public const int CertifiedLineOfSightMaximumBoundsQueries = (2 * ((1 << (FixedQ4816.FractionBitCount + 1)) - 1));

    /// <summary>Gets the factor a clearance is multiplied by to propose a step that stays clear: the reciprocal of the
    /// program's Lipschitz bound, conservatively rounded down.</summary>
    public FixedQ4816 StepScale => m_stepScale;

    /// <summary>Advances a sphere along a displacement by conservative advancement over this program's certified bounds
    /// (<see cref="CertifiedFieldSweep.TrySweep"/>).</summary>
    /// <param name="origin">The sphere's centre at the start.</param>
    /// <param name="displacement">The whole motion; the answer is a fraction of it.</param>
    /// <param name="radius">The sphere's radius, at least zero.</param>
    /// <param name="boundsQueryBudget">The most bounds queries the sweep may spend, at least one; each walks the program
    /// once over one box, so this caps the sweep's cost.</param>
    /// <param name="sweep">The certified fraction, the outcome and the queries spent on success.</param>
    /// <returns><see langword="false"/> when the program has no shape or the origin leaves the evaluator's frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative, or
    /// <paramref name="boundsQueryBudget"/> is below one.</exception>
    /// <exception cref="NotSupportedException">The program holds an instruction with no inclusion rule.</exception>
    /// <remarks>Certified means proved, not estimated: every point of the swept sphere up to the returned fraction keeps
    /// both the exact field and every fixed-point answer above zero after a positive advance, however fast the motion,
    /// so a thin wall is never crossed between two samples.</remarks>
    public bool TryCertifiedSweep(FixedPosition origin, FixedVector3 displacement, FixedQ4816 radius, int boundsQueryBudget, out CertifiedSweep sweep) =>
        TryCertifiedSweep(
            boundsQueryBudget: boundsQueryBudget,
            contactTolerance: FixedQ4816.Zero,
            displacement: displacement,
            origin: origin,
            radius: radius,
            sweep: out sweep
        );
    /// <inheritdoc/>
    public bool TryCertifiedSweep(FixedPosition origin, FixedVector3 displacement, FixedQ4816 radius, int boundsQueryBudget, FixedQ4816 contactTolerance, out CertifiedSweep sweep) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: radius, other: FixedQ4816.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: boundsQueryBudget, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: contactTolerance, other: FixedQ4816.Zero);

        sweep = default;

        return (
            m_hasShape &&
            CertifiedFieldSweep.TrySweep(
                boundsQueryBudget: boundsQueryBudget,
                contactTolerance: contactTolerance,
                displacement: displacement,
                field: this,
                origin: origin,
                radius: radius,
                sweep: out sweep
            )
        );
    }
    /// <summary>Decides whether the segment between two points stays outside the solid, by proof: the segment is split
    /// into boxes until each box's bounds are above zero (clear) or a point of it is proved inside (blocked).</summary>
    /// <param name="from">One end of the segment.</param>
    /// <param name="to">The other end.</param>
    /// <param name="boundsQueryBudget">The most bounds queries the verdict may spend, at least one; each walks the
    /// program once over one box. Past <see cref="CertifiedLineOfSightMaximumBoundsQueries"/> a larger budget changes
    /// nothing.</param>
    /// <param name="sight">The verdict and the queries spent on success.</param>
    /// <returns><see langword="false"/> when the program has no shape or an end leaves the evaluator's frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="boundsQueryBudget"/> is below one.</exception>
    /// <exception cref="NotSupportedException">The program holds an instruction with no inclusion rule.</exception>
    /// <remarks>Unlike <see cref="LineOfSight"/>, which marches samples and so can step over a surface thinner than its
    /// step, this answers <see cref="SdfCertifiedVisibility.Clear"/> only when no point of the segment can be inside,
    /// and <see cref="SdfCertifiedVisibility.Blocked"/> only when one surely is; anything it cannot prove within its
    /// budget is <see cref="SdfCertifiedVisibility.Undecided"/>. The pieces are examined depth first, nearest
    /// <paramref name="from"/> first, so a run under a smaller budget is the prefix of a run under a larger one: a
    /// verdict proved under one budget is the same verdict, at the same cost, under every larger one.</remarks>
    public bool TryCertifiedLineOfSight(FixedPosition from, FixedPosition to, int boundsQueryBudget, out SdfCertifiedSight sight) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: boundsQueryBudget, other: 1);

        sight = new(BoundsQueries: 0, Visibility: SdfCertifiedVisibility.Undecided);

        if (
            !m_hasShape ||
            !from.TryDelta(delta: out var start, origin: FixedPosition.Zero) ||
            !to.TryDelta(delta: out var end, origin: FixedPosition.Zero)
        ) {
            return false;
        }

        var one = FixedQ4816.One.Value;
        var pending = new Stack<(long From, long To)>();
        var queries = 0;

        pending.Push(item: (0L, one));

        while (pending.TryPop(result: out var segment)) {
            if (queries >= boundsQueryBudget) {
                sight = new(BoundsQueries: queries, Visibility: SdfCertifiedVisibility.Undecided);
                return true;
            }

            ++queries;

            if (SegmentBounds(end: end, from: segment.From, start: start, to: segment.To).Lower > FixedQ4816.Zero) {
                continue;
            }

            if (queries >= boundsQueryBudget) {
                sight = new(BoundsQueries: queries, Visibility: SdfCertifiedVisibility.Undecided);
                return true;
            }

            var middle = (segment.From + ((segment.To - segment.From) >> 1));

            ++queries;

            // A point of the segment whose own tiny box lies wholly below zero is surely inside.
            if (SegmentBounds(end: end, from: middle, start: start, to: middle).Upper < FixedQ4816.Zero) {
                sight = new(BoundsQueries: queries, Visibility: SdfCertifiedVisibility.Blocked);
                return true;
            }

            if ((segment.To - segment.From) <= 1L) {
                sight = new(BoundsQueries: queries, Visibility: SdfCertifiedVisibility.Undecided);
                return true;
            }

            pending.Push(item: (middle, segment.To));
            pending.Push(item: (segment.From, middle));
        }

        sight = new(BoundsQueries: queries, Visibility: SdfCertifiedVisibility.Clear);
        return true;
    }

    // Endpoint interpolation avoids narrowing a difference that can exceed the carrier even when both ends fit.
    // Each coordinate is affine, so the hull of its two endpoint enclosures holds the whole subsegment.
    private FixedInterval SegmentBounds(FixedVector3 start, FixedVector3 end, long from, long to) {
        var first = Point(value: FixedQ4816.FromRawBits(value: from));
        var last = Point(value: FixedQ4816.FromRawBits(value: to));
        var beforeFirst = (Point(value: FixedQ4816.One) - first);
        var beforeLast = (Point(value: FixedQ4816.One) - last);
        var x = FixedInterval.Union(first: ((Point(value: start.X) * beforeFirst) + (Point(value: end.X) * first)), second: ((Point(value: start.X) * beforeLast) + (Point(value: end.X) * last)));
        var y = FixedInterval.Union(first: ((Point(value: start.Y) * beforeFirst) + (Point(value: end.Y) * first)), second: ((Point(value: start.Y) * beforeLast) + (Point(value: end.Y) * last)));
        var z = FixedInterval.Union(first: ((Point(value: start.Z) * beforeFirst) + (Point(value: end.Z) * first)), second: ((Point(value: start.Z) * beforeLast) + (Point(value: end.Z) * last)));

        return (TryDistanceBounds(
            distance: out var bounds,
            lower: FixedPosition.FromLocal(local: new FixedVector3(X: x.Lower, Y: y.Lower, Z: z.Lower)),
            upper: FixedPosition.FromLocal(local: new FixedVector3(X: x.Upper, Y: y.Upper, Z: z.Upper))
        )
            ? bounds
            : FixedInterval.Entire);
    }
}
