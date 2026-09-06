using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>
/// The <see cref="SdfFieldEvaluator"/>'s query surface read through an <see cref="SdfDistanceGrid"/>: exact wherever a
/// decision can depend on the field's value, and the grid's bound everywhere else.
/// </summary>
/// <remarks>
/// <para><see cref="TryDistance"/> answers the exact field wherever the grid's bound falls below <see cref="Band"/>,
/// so a consumer comparing the value against any threshold up to the contact reach the band was built from reads the
/// same value the exact evaluator would; above the band it answers the bound, a value at or below the exact one that
/// never drops below the band. Both gradient overloads always read the exact evaluator.</para>
/// <para>The cast, ground, and visibility verbs run the exact evaluator's own march over samples that are exact
/// wherever the exact march could accept a hit or stop, and the grid's bound elsewhere. A march that never leaves the
/// exact region answers bit-identically to the exact evaluator; one that crosses the bound region takes different
/// steps, so its converged hit lands at a different point within the accept threshold of the same surface, and it
/// spends a separate sample budget on those bound steps. <see cref="Overlap"/> answers identically to the exact
/// evaluator everywhere.</para>
/// </remarks>
public sealed class SdfBandedFieldEvaluator : IWorldQuery, IFieldEvaluator {
    private readonly SdfFieldEvaluator m_exact;
    private readonly SdfDistanceGrid m_grid;
    // The smallest advance a bound sample can produce, in world units: the slack scaled to a safe advance, floored at
    // one tick. Sizes the bound budget of a march from its reach.
    private readonly FixedQ4816 m_boundStepFloor;

    /// <summary>Creates a banded evaluator.</summary>
    /// <param name="exact">The exact evaluator the grid was baked from.</param>
    /// <param name="grid">The grid over <paramref name="exact"/>'s program.</param>
    /// <param name="contactReach">The largest value any consumer of <see cref="TryDistance"/> compares the field
    /// against, in world units — the band starts this far plus the grid's slack from every surface.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exact"/> or <paramref name="grid"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contactReach"/> is negative.</exception>
    public SdfBandedFieldEvaluator(SdfFieldEvaluator exact, SdfDistanceGrid grid, FixedQ4816 contactReach) {
        ArgumentNullException.ThrowIfNull(argument: exact);
        ArgumentNullException.ThrowIfNull(argument: grid);
        ArgumentOutOfRangeException.ThrowIfLessThan(other: FixedQ4816.Zero, value: contactReach);

        m_exact = exact;
        m_grid = grid;
        ContactReach = contactReach;
        Band = (contactReach + grid.Slack);
        m_boundStepFloor = FixedQ4816.Max(
            x: SdfFieldMarch.ScaleDistanceDown(
                distance: grid.Slack,
                scale: exact.StepScale
            ),
            y: FixedQ4816.Epsilon
        );
    }

    /// <summary>Gets the field value below which <see cref="TryDistance"/> always answers the exact evaluator.</summary>
    public FixedQ4816 Band { get; }
    /// <inheritdoc/>
    public FieldEvaluatorCapabilities Capabilities => m_exact.Capabilities;
    /// <summary>Gets the contact reach the band was built from.</summary>
    public FixedQ4816 ContactReach { get; }
    /// <summary>Gets the exact evaluator the grid was baked from.</summary>
    public SdfFieldEvaluator Exact => m_exact;
    /// <summary>Gets the grid.</summary>
    public SdfDistanceGrid Grid => m_grid;
    /// <inheritdoc/>
    QueryCapabilities IWorldQuery.Capabilities => ((IWorldQuery)m_exact).Capabilities;

    // The number of bound samples a march of the given reach can spend: one per bound-step floor, plus one for the
    // sample at the reach itself.
    private int BoundBudget(FixedQ4816 maxDistance) {
        var steps = (((maxDistance.Value + m_boundStepFloor.Value) - 1L) / m_boundStepFloor.Value);

        return ((steps >= int.MaxValue)
            ? int.MaxValue
            : ((int)steps + 1)
        );
    }
    private MarchOutcome March(FixedPosition origin, FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 radius, out RayHit hit) {
        var sampler = new BandedSampler(evaluator: this);

        return SdfFieldMarch.Run(
            boundBudget: BoundBudget(maxDistance: maxDistance),
            direction: direction,
            exactBudget: m_exact.MarchIterations,
            hasShape: m_exact.HasShape,
            hit: out hit,
            maxDistance: maxDistance,
            origin: origin,
            radius: radius,
            sampler: ref sampler,
            stepScale: m_exact.StepScale
        );
    }
    // A bound is admissible for a sweep of the given radius only where the exact march could neither accept nor
    // stop: the bound must clear the radius by more than the accept threshold, and its scaled advance must clear the
    // radius by at least one tick, so the march advances by a positive step whose only difference from the exact
    // step is its length.
    private bool TryBound(FixedPosition position, FixedQ4816 radius, out FixedQ4816 lowerBound, out int material) {
        lowerBound = FixedQ4816.Zero;
        material = 0;

        if (!position.TryDelta(
            delta: out var world,
            origin: FixedPosition.Zero
        )) {
            return false;
        }

        if (!m_grid.TryLowerBound(
            lowerBound: out lowerBound,
            material: out material,
            world: in world
        )) {
            return false;
        }

        var threshold = (FixedQ4816.Max(
            x: (radius * m_grid.LipschitzBound),
            y: (radius + SdfFieldMarch.HitEpsilon)
        ) + m_grid.Slack);

        return (
            (lowerBound >= threshold) &&
            ((SdfFieldMarch.ScaleDistanceDown(
                distance: lowerBound,
                scale: m_exact.StepScale
            ) - radius) >= FixedQ4816.Epsilon)
        );
    }

    /// <inheritdoc/>
    public bool LineOfSight(FixedPosition from, FixedPosition to) {
        var delta = (to - from);
        var distance = delta.Length;

        if (distance <= FixedQ4816.Zero) {
            return true;
        }

        var probeDistance = (distance - SdfFieldMarch.LineOfSightSkin);

        if (probeDistance <= FixedQ4816.Zero) {
            return true;
        }

        return !Raycast(
            dir: delta,
            hit: out _,
            maxDist: probeDistance,
            origin: from
        );
    }
    /// <inheritdoc/>
    /// <remarks>Answers exactly what the exact evaluator answers: the bound decides only where its scaled value
    /// already clears the radius, which the exact value then clears too.</remarks>
    public bool Overlap(FixedPosition center, FixedQ4816 radius) {
        if (
            center.TryDelta(
            delta: out var world,
            origin: FixedPosition.Zero
        ) &&
            m_grid.TryLowerBound(
            lowerBound: out var lowerBound,
            material: out _,
            world: in world
        ) &&
            (SdfFieldMarch.ScaleDistanceDown(
            distance: lowerBound,
            scale: m_exact.StepScale
        ) > FixedQ4816.Max(
            x: radius,
            y: FixedQ4816.Zero
        ))
        ) {
            return false;
        }

        return m_exact.Overlap(
            center: center,
            radius: radius
        );
    }
    /// <inheritdoc/>
    public bool Raycast(FixedPosition origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit) =>
        (March(
            direction: dir,
            hit: out hit,
            maxDistance: maxDist,
            origin: origin,
            radius: FixedQ4816.Zero
        ) != MarchOutcome.Miss);
    /// <inheritdoc/>
    public bool SphereCast(FixedPosition origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit) =>
        (March(
            direction: dir,
            hit: out hit,
            maxDistance: maxDist,
            origin: origin,
            radius: FixedQ4816.Max(
                x: radius,
                y: FixedQ4816.Zero
            )
        ) != MarchOutcome.Miss);
    /// <inheritdoc/>
    /// <remarks>Below <see cref="Band"/> the answer is the exact evaluator's; at or above it the answer is the grid's
    /// bound at the point, which is at most the exact value and never below the band, with the nearest corner's
    /// material.</remarks>
    public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
        if (
            position.TryDelta(
            delta: out var world,
            origin: FixedPosition.Zero
        ) &&
            m_grid.TryLowerBound(
            lowerBound: out distance,
            material: out material,
            world: in world
        ) &&
            (distance >= Band)
        ) {
            return true;
        }

        return m_exact.TryDistance(
            distance: out distance,
            material: out material,
            position: position
        );
    }
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) =>
        m_exact.TryFieldGradient(
            gradient: out gradient,
            position: position
        );
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) =>
        m_exact.TryFieldGradient(
            epsilon: epsilon,
            gradient: out gradient,
            position: position
        );
    /// <inheritdoc/>
    public bool TryGroundHeight(FixedPosition position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY) {
        groundY = FixedQ4816.Zero;

        var probeRange = (probeUp + probeDown);

        if (probeRange <= FixedQ4816.Zero) {
            return false;
        }

        var top = (position + new FixedVector3(
            X: FixedQ4816.Zero,
            Y: probeUp,
            Z: FixedQ4816.Zero
        ));

        if (March(
            direction: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: -FixedQ4816.One,
                Z: FixedQ4816.Zero
            ),
            hit: out var hit,
            maxDistance: probeRange,
            origin: top,
            radius: FixedQ4816.Zero
        ) != MarchOutcome.Hit) {
            return false;
        }

        if (!hit.Point.TryDelta(
            delta: out var world,
            origin: FixedPosition.Zero
        )) {
            return false;
        }

        groundY = world.Y;

        return true;
    }

    // The march sampler over the grid: a bound wherever TryBound admits one for the sweep radius, the exact field
    // everywhere else.
    private readonly struct BandedSampler(SdfBandedFieldEvaluator evaluator) : ISdfMarchSampler {
        public bool TrySample(FixedPosition position, FixedQ4816 radius, out FixedQ4816 distance, out int material, out bool exact) {
            if (evaluator.TryBound(
                lowerBound: out distance,
                material: out material,
                position: position,
                radius: radius
            )) {
                exact = false;

                return true;
            }

            exact = true;

            return evaluator.m_exact.TryDistance(
                distance: out distance,
                material: out material,
                position: position
            );
        }
    }
}
