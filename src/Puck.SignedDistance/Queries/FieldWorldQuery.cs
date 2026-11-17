using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>Gameplay queries over a live field composition, using the same march as compiled SDF programs.</summary>
public sealed class FieldWorldQuery(IFieldEvaluator field) : IWorldQuery, ISdfFieldMarcher {
    /// <inheritdoc/>
    public QueryCapabilities Capabilities => new(HasBlocked: true, HasHeightfield: true, HasOccupancy: true);

    MarchOutcome ISdfFieldMarcher.March(FixedPosition origin, FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 radius, out RayHit hit) {
        var sampler = new SdfFieldMarch.ExactSampler(evaluator: field);

        return SdfFieldMarch.Run(ref sampler, origin, direction, maxDistance, radius, field.Capabilities.StepScale,
            SdfFieldMarch.IterationsFor(stepScale: field.Capabilities.StepScale), 0, true, out hit);
    }

    /// <inheritdoc/>
    public bool Raycast(FixedPosition origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit) =>
        SdfFieldMarch.Raycast(direction: dir, hit: out hit, marcher: this, maxDistance: maxDist, origin: origin);
    /// <inheritdoc/>
    public bool SphereCast(FixedPosition origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit) =>
        SdfFieldMarch.SphereCast(direction: dir, hit: out hit, marcher: this, maxDistance: maxDist, origin: origin, radius: radius);
    /// <inheritdoc/>
    public bool Overlap(FixedPosition center, FixedQ4816 radius) => SdfFieldMarch.Overlap(center: center, evaluator: field, hasShape: true, radius: radius);
    /// <inheritdoc/>
    public bool TryGroundHeight(FixedPosition position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY) =>
        SdfFieldMarch.GroundHeight(groundY: out groundY, marcher: this, position: position, probeDown: probeDown, probeUp: probeUp);
    /// <inheritdoc/>
    public bool LineOfSight(FixedPosition from, FixedPosition to) => SdfFieldMarch.LineOfSight(from: from, marcher: this, to: to);
}
