using Puck.Maths;
using Puck.SignedDistance.Queries;

namespace Puck.SignedDistance.Baking;

/// <summary>
/// The field a bake reads: one <see cref="SdfFieldEvaluator"/>, the one fixed-point interpreter of a program, with every
/// program evaluation counted in <see cref="Evaluations"/>. A distance counts one evaluation, a normal the six probes
/// of <see cref="SdfFieldEvaluator.TryFieldGradient(FixedPosition, FixedQ4816, out FixedVector3)"/>, and a ray each
/// sample its march takes. Points are world-space displacements from the program's origin. One instance serves one
/// bake on one thread.
/// </summary>
public sealed class SdfBakeField {
    private readonly SdfFieldEvaluator m_evaluator;

    /// <summary>Initializes a new instance of the <see cref="SdfBakeField"/> class.</summary>
    /// <param name="evaluator">The evaluator of the program being baked.</param>
    /// <exception cref="ArgumentNullException"><paramref name="evaluator"/> is <see langword="null"/>.</exception>
    public SdfBakeField(SdfFieldEvaluator evaluator) {
        ArgumentNullException.ThrowIfNull(argument: evaluator);

        m_evaluator = evaluator;
    }

    /// <summary>Gets the program evaluations this field has made.</summary>
    public long Evaluations { get; private set; }
    /// <summary>Gets the rays this field has marched.</summary>
    public long Rays { get; private set; }
    /// <summary>Gets the program's step scale (the reciprocal of its Lipschitz bound) in fixed point, floored so that a
    /// field value times it is a lower bound on the true distance to the surface.</summary>
    public FixedQ4816 StepScale => m_evaluator.StepScale;

    /// <summary>Evaluates the field at <paramref name="point"/>.</summary>
    /// <param name="point">The world-space point.</param>
    /// <param name="distance">The field value: negative inside, positive outside.</param>
    /// <param name="material">The material of the winning shape.</param>
    /// <returns><see langword="true"/> when the program has a shape and the point is inside its frame.</returns>
    public bool TryDistance(FixedVector3 point, out FixedQ4816 distance, out int material) {
        Evaluations++;

        return m_evaluator.TryDistance(
            distance: out distance,
            material: out material,
            position: FixedPosition.FromLocal(local: point)
        );
    }
    /// <summary>Evaluates the field's unit gradient at <paramref name="point"/> by central differences.</summary>
    /// <param name="point">The world-space point.</param>
    /// <param name="epsilon">The probe span, in world units.</param>
    /// <param name="normal">The unit gradient, pointing out of the surface.</param>
    /// <returns><see langword="true"/> when every probe evaluated and the gradient is not zero.</returns>
    public bool TryNormal(FixedVector3 point, FixedQ4816 epsilon, out FixedVector3 normal) {
        Evaluations += 6;

        return m_evaluator.TryFieldGradient(
            epsilon: epsilon,
            gradient: out normal,
            position: FixedPosition.FromLocal(local: point)
        );
    }
    /// <summary>Marches a ray through the field with the evaluator's own march, counting each sample.</summary>
    /// <param name="origin">The ray's world-space origin.</param>
    /// <param name="direction">The ray's direction; it need not be unit length.</param>
    /// <param name="maxDistance">The farthest travel, in world units.</param>
    /// <param name="hit">The hit, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the march proved a surface within <paramref name="maxDistance"/>; a march
    /// that neither proved a hit nor cleared the ray reports no hit.</returns>
    public bool TryRay(FixedVector3 origin, FixedVector3 direction, FixedQ4816 maxDistance, out RayHit hit) {
        Rays++;

        var sampler = new CountingSampler(field: this);

        return (SdfFieldMarch.Run(
            boundBudget: 0,
            direction: direction,
            exactBudget: m_evaluator.MarchIterations,
            hasShape: m_evaluator.HasShape,
            hit: out hit,
            maxDistance: maxDistance,
            origin: FixedPosition.FromLocal(local: origin),
            radius: FixedQ4816.Zero,
            sampler: ref sampler,
            stepScale: m_evaluator.StepScale
        ) == MarchOutcome.Hit);
    }

    private readonly struct CountingSampler(SdfBakeField field) : ISdfMarchSampler {
        public bool TrySample(FixedPosition position, FixedQ4816 radius, out FixedQ4816 distance, out int material, out bool exact) {
            exact = true;
            field.Evaluations++;

            return field.m_evaluator.TryDistance(
                distance: out distance,
                material: out material,
                position: position
            );
        }
    }
}
