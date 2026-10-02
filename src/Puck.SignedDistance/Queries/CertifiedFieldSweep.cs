using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>
/// Conservative advancement over any <see cref="IFieldBounds"/>: the one certified sweep, which
/// <see cref="SdfFieldEvaluator.TryCertifiedSweep(FixedPosition, FixedVector3, FixedQ4816, int, out CertifiedSweep)"/> runs over a program and an instance runs over the field it wraps,
/// such as a <see cref="FieldBoundsUnion"/>.
/// </summary>
/// <remarks>Each step is the certified clearance at the current point times the field's step scale, and the step's
/// whole segment, expanded by the sphere's radius, is then proved clear by one bounds query over its box, halving the
/// step until the proof holds. The step scale only proposes a step; the bounds query proves it, so the answer does not
/// rest on the field's Lipschitz bound being exact for its quantized constants. A segment's box is formed with outward
/// rounding, so it holds every real point of the segment, and the box's interval bounds the field there.</remarks>
/// <param name="field">The field whose bounds prove each step.</param>
public sealed class CertifiedFieldSweep(IFieldBounds field) : ICertifiedSweepQuery {
    private readonly IFieldBounds m_field = (field ?? throw new ArgumentNullException(paramName: nameof(field)));

    // The fraction runs at Q32 of the displacement, so a step of one unit of it moves a long sweep a sliver.
    private const int FractionBits = 32;
    private const long Whole = (1L << FractionBits);

    /// <inheritdoc/>
    public bool TryCertifiedSweep(FixedPosition origin, FixedVector3 displacement, FixedQ4816 radius, int boundsQueryBudget, FixedQ4816 contactTolerance, out CertifiedSweep sweep) =>
        TrySweep(
            boundsQueryBudget: boundsQueryBudget,
            contactTolerance: contactTolerance,
            displacement: displacement,
            field: m_field,
            origin: origin,
            radius: radius,
            sweep: out sweep
        );
    /// <summary>Advances a sphere along a displacement by conservative advancement over a field's certified bounds.</summary>
    /// <param name="field">The field whose bounds prove each step.</param>
    /// <param name="origin">The sphere's centre at the start.</param>
    /// <param name="displacement">The whole motion; the answer is a fraction of it.</param>
    /// <param name="radius">The sphere's radius, at least zero.</param>
    /// <param name="boundsQueryBudget">The most bounds queries the sweep may spend, at least one; each walks the field
    /// once over one box, so this caps the sweep's cost.</param>
    /// <param name="contactTolerance">The clearance, at least zero, at or below which the sweep stops in
    /// <see cref="CertifiedSweepOutcome.Contact"/>; zero stops only where no clearance is proved.</param>
    /// <param name="sweep">The certified fraction, the outcome and the queries spent on success.</param>
    /// <returns><see langword="false"/> when the origin leaves the frame <see cref="FixedPosition.Zero"/>
    /// measures from.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> or <paramref name="contactTolerance"/>
    /// is negative, or <paramref name="boundsQueryBudget"/> is below one.</exception>
    /// <remarks>Certified means proved, not estimated: every point of the swept sphere up to the returned fraction keeps
    /// every answer of the field above zero after a positive advance, however fast the motion, so a thin wall is never
    /// crossed between two samples. An initial sphere that cannot be proved clear returns
    /// <see cref="CertifiedSweepOutcome.Contact"/> at zero travel. A box the field refuses to bound, or whose bounds
    /// are unbounded, proves nothing, so the sweep stops there <see cref="CertifiedSweepOutcome.Exhausted"/>. The count
    /// is checked before every query, so a smaller budget runs a prefix of a larger one: a sweep cut short keeps only
    /// the ground it proved. Conservative advancement approaches a surface ever more slowly, so a positive tolerance
    /// ends it in contact once the proved clearance is that small, instead of spending its budget on the last
    /// sliver.</remarks>
    public static bool TrySweep(IFieldBounds field, FixedPosition origin, FixedVector3 displacement, FixedQ4816 radius, int boundsQueryBudget, FixedQ4816 contactTolerance, out CertifiedSweep sweep) {
        ArgumentNullException.ThrowIfNull(argument: field);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: radius, other: FixedQ4816.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: contactTolerance, other: FixedQ4816.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: boundsQueryBudget, other: 1);

        sweep = default;

        if (!origin.TryDelta(delta: out var start, origin: FixedPosition.Zero)) {
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

        var stepScale = field.StepScale;
        var fraction = 0L;
        var queries = 0;
        var outcome = CertifiedSweepOutcome.Exhausted;

        while (true) {
            if (fraction >= Whole) {
                outcome = CertifiedSweepOutcome.Clear;
                break;
            }

            if (queries >= boundsQueryBudget) {
                break;
            }

            var here = SweepBounds(displacement: displacement, field: field, from: fraction, radius: radius, start: start, to: fraction);
            var clearance = here.Lower;

            ++queries;

            // An unbounded box proves nothing: the sweep stops undecided rather than reading its endpoints as a clearance.
            if (here.IsUnbounded) {
                break;
            }

            if (clearance <= contactTolerance) {
                outcome = CertifiedSweepOutcome.Contact;
                break;
            }

            // The Lipschitz ball's radius, floored, as a fraction of the displacement's length rounded up.
            var reach = ((((Int128)clearance.Value) * stepScale.Value) >> FixedQ4816.FractionBitCount);
            var proposed = ((lengthRaw == 0L)
                ? Whole
                : (long)Int128.Min(x: Whole, y: ((reach << FractionBits) / lengthRaw)));
            var next = Math.Min(val1: Whole, val2: (fraction + Math.Max(val1: proposed, val2: 1L)));
            var proved = false;

            // Prove the whole segment; halve the step until it holds, while the budget lasts.
            while (queries < boundsQueryBudget) {
                ++queries;

                var proof = SweepBounds(displacement: displacement, field: field, from: fraction, radius: radius, start: start, to: next);

                if (!proof.IsUnbounded && (proof.Lower > FixedQ4816.Zero)) {
                    proved = true;
                    break;
                }

                var half = ((next - fraction) >> 1);

                // A step that cannot shrink further ends in contact only when its box proved a surface may be near; an
                // unbounded box proved nothing, so the sweep stops undecided.
                if (half == 0L) {
                    if (!proof.IsUnbounded) {
                        outcome = CertifiedSweepOutcome.Contact;
                    }

                    break;
                }

                next = (fraction + half);
            }

            if (!proved) {
                break;
            }

            fraction = next;
        }

        // The point box at the reached fraction lies inside the last certified segment's box, so any corner of it is
        // proved clear.
        var reached = SweepBox(displacement: displacement, from: fraction, start: start, to: fraction);

        sweep = new(
            Fraction: FixedQ4816.FromRawBits(value: (fraction >> (FractionBits - FixedQ4816.FractionBitCount))),
            Reached: FixedPosition.FromLocal(local: new FixedVector3(X: reached.X.Lower, Y: reached.Y.Lower, Z: reached.Z.Lower)),
            Outcome: outcome,
            BoundsQueries: queries
        );
        return true;
    }

    // The bounds over the box of start + displacement·t for t in [from, to], fractions at Q32 (read as Q16 values scaled
    // by 2¹⁶, then divided back out with outward rounding), expanded by the sphere's radius on every axis. A box whose
    // ends left the carrier, or that the field refuses, is unbounded.
    private static FixedInterval SweepBounds(IFieldBounds field, FixedVector3 start, FixedVector3 displacement, FixedQ4816 radius, long from, long to) {
        var box = SweepBox(displacement: displacement, from: from, start: start, to: to);
        var extent = new FixedInterval(lower: -radius, upper: radius);

        box = ((box.X + extent), (box.Y + extent), (box.Z + extent));

        var outsideFrame = (box.X.IsUnbounded || box.Y.IsUnbounded || box.Z.IsUnbounded);

        // Charge one field walk even when the box's unbounded ends prevent certification.
        return ((field.TryDistanceBounds(
            distance: out var bounds,
            lower: FixedPosition.FromLocal(local: new FixedVector3(X: box.X.Lower, Y: box.Y.Lower, Z: box.Z.Lower)),
            upper: FixedPosition.FromLocal(local: new FixedVector3(X: box.X.Upper, Y: box.Y.Upper, Z: box.Z.Upper))
        ) && !outsideFrame)
            ? bounds
            : FixedInterval.Entire);
    }
    private static (FixedInterval X, FixedInterval Y, FixedInterval Z) SweepBox(FixedVector3 start, FixedVector3 displacement, long from, long to) {
        var t = new FixedInterval(lower: FixedQ4816.FromRawBits(value: from), upper: FixedQ4816.FromRawBits(value: to));
        var scale = FixedInterval.FromPoint(value: FixedQ4816.FromInteger(value: (1L << FixedQ4816.FractionBitCount)));

        return (
            X: (FixedInterval.FromPoint(value: start.X) + ((FixedInterval.FromPoint(value: displacement.X) * t) / scale)),
            Y: (FixedInterval.FromPoint(value: start.Y) + ((FixedInterval.FromPoint(value: displacement.Y) * t) / scale)),
            Z: (FixedInterval.FromPoint(value: start.Z) + ((FixedInterval.FromPoint(value: displacement.Z) * t) / scale))
        );
    }
}
