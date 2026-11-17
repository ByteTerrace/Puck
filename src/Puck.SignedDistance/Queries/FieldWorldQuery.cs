using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>Gameplay queries over a live field composition, using the same march as compiled SDF programs.</summary>
public sealed class FieldWorldQuery(IFieldEvaluator field) : IWorldQuery, ISdfFieldMarcher {
    /// <inheritdoc/>
    public QueryCapabilities Capabilities => new(true, true, true);
    private readonly struct Sampler(IFieldEvaluator source) : ISdfMarchSampler {
        public bool TrySample(FixedPosition position, FixedQ4816 radius, out FixedQ4816 distance, out int material, out bool exact) {
            exact = true;
            return source.TryDistance(position, out distance, out material);
        }
    }
    MarchOutcome ISdfFieldMarcher.March(FixedPosition origin, FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 radius, out RayHit hit) {
        var sampler = new Sampler(field);
        return SdfFieldMarch.Run(ref sampler, origin, direction, maxDistance, radius, field.Capabilities.StepScale,
            SdfFieldEvaluator.MarchIterationsFor(field.Capabilities.StepScale), 0, true, out hit);
    }
    /// <inheritdoc/>
    public bool Raycast(FixedPosition origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit) =>
        SdfFieldMarch.Raycast(this, origin, dir, maxDist, out hit);
    /// <inheritdoc/>
    public bool SphereCast(FixedPosition origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit) =>
        SdfFieldMarch.SphereCast(this, origin, dir, radius, maxDist, out hit);
    /// <inheritdoc/>
    public bool Overlap(FixedPosition center, FixedQ4816 radius) => !field.TryDistance(center, out var distance, out _) ||
        SdfFieldMarch.ScaleDistanceDown(distance, field.Capabilities.StepScale) <= FixedQ4816.Max(radius, FixedQ4816.Zero);
    /// <inheritdoc/>
    public bool TryGroundHeight(FixedPosition position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY) =>
        SdfFieldMarch.GroundHeight(this, position, probeUp, probeDown, out groundY);
    /// <inheritdoc/>
    public bool LineOfSight(FixedPosition from, FixedPosition to) => SdfFieldMarch.LineOfSight(this, from, to);
}
