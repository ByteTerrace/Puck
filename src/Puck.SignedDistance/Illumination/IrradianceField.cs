using Puck.Maths;
using Puck.SignedDistance.Queries;

namespace Puck.SignedDistance.Illumination;

/// <summary>What a ray cast through the field ended on.</summary>
public enum IrradianceRayKind {
    /// <summary>The ray reached its maximum distance without touching a surface.</summary>
    Miss = 0,
    /// <summary>The ray reached a surface whose distance the march proved within its accept threshold.</summary>
    Hit = 1,
    /// <summary>The march could not finish its proof (its budget ended, or no safe advance remained), so the ray proves
    /// neither a hit nor a miss and carries no light.</summary>
    Unresolved = 2,
}
/// <summary>One ray cast through the field.</summary>
/// <param name="Kind">What the ray ended on.</param>
/// <param name="Distance">The distance, in world units, the ray travelled.</param>
/// <param name="Point">The point the ray ended at: the surface for a hit, the last proven point otherwise.</param>
/// <param name="Material">The surface's material for a hit; meaningless otherwise.</param>
public readonly record struct IrradianceRay(IrradianceRayKind Kind, double Distance, Double3 Point, int Material);
/// <summary>
/// The field as the illumination reference and its CPU model read it: distances, gradients, rays and segments through
/// <see cref="SdfFieldEvaluator"/>, the one CPU interpreter, converted to and from the doubles the model computes in.
/// Every query is deterministic, and every answer the evaluator cannot prove is reported as unresolved or blocked, never
/// as clear.
/// </summary>
public sealed class IrradianceField {
    private readonly SdfFieldEvaluator m_evaluator;

    /// <summary>Initializes a new instance of the <see cref="IrradianceField"/> class over a program.</summary>
    /// <param name="program">The program; it must use only the operations <see cref="SdfFieldEvaluator"/>
    /// interprets.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="program"/> holds an operation the evaluator refuses; the
    /// message names it.</exception>
    public IrradianceField(SdfProgram program) {
        m_evaluator = new SdfFieldEvaluator(program: program);
    }

    /// <summary>Gets the count of rays and segments cast through the field since construction.</summary>
    public long Casts { get; private set; }
    /// <summary>Gets the count of point distance and gradient queries since construction.</summary>
    public long Samples { get; private set; }

    /// <summary>Reads the field's distance at a point in the march's clamped units, which never overstate the distance
    /// to a surface.</summary>
    /// <param name="point">The point, in world units.</param>
    /// <param name="distance">The clamped distance, in world units; negative inside a solid.</param>
    /// <param name="material">The material that wins at the point.</param>
    /// <returns><see langword="true"/> when the evaluator answered.</returns>
    public bool TryClampedDistance(Double3 point, out double distance, out int material) {
        Samples++;

        if (!m_evaluator.TryDistance(
            distance: out var raw,
            material: out material,
            position: ToPosition(point: point)
        )) {
            distance = 0.0;

            return false;
        }

        var clamped = ((raw >= FixedQ4816.Zero)
            ? SdfFieldMarch.ScaleDistanceDown(
                distance: raw,
                scale: m_evaluator.StepScale
            )
            : raw);

        distance = ((double)clamped);

        return true;
    }
    /// <summary>Reads the field's unit gradient at a point, which points away from the nearest surface.</summary>
    /// <param name="point">The point, in world units.</param>
    /// <param name="gradient">The unit gradient.</param>
    /// <returns><see langword="true"/> when every sample answered and the gradient is not zero.</returns>
    public bool TryGradient(Double3 point, out Double3 gradient) {
        Samples += 6;

        if (!m_evaluator.TryFieldGradient(
            gradient: out var fixedGradient,
            position: ToPosition(point: point)
        )) {
            gradient = Double3.Zero;

            return false;
        }

        gradient = new Double3(
            X: ((double)fixedGradient.X),
            Y: ((double)fixedGradient.Y),
            Z: ((double)fixedGradient.Z)
        ).Normalize();

        return (gradient != Double3.Zero);
    }
    /// <summary>Casts a ray through the field.</summary>
    /// <param name="origin">The origin, in world units.</param>
    /// <param name="direction">The direction; it need not be unit length.</param>
    /// <param name="maxDistance">The farthest distance, in world units, the ray travels.</param>
    /// <returns>What the ray ended on.</returns>
    public IrradianceRay Cast(Double3 origin, Double3 direction, double maxDistance) {
        Casts++;

        var unit = direction.Normalize();

        if (!m_evaluator.Raycast(
            dir: ToVector(value: unit),
            hit: out var hit,
            maxDist: FixedQ4816.FromDouble(value: maxDistance),
            origin: ToPosition(point: origin)
        )) {
            return new IrradianceRay(
                Distance: maxDistance,
                Kind: IrradianceRayKind.Miss,
                Material: 0,
                Point: (origin + (unit * maxDistance))
            );
        }

        var distance = ((double)hit.Distance);

        return new IrradianceRay(
            Distance: distance,
            Kind: ((hit.Confidence == WorldQueryConfidence.Exact) ? IrradianceRayKind.Hit : IrradianceRayKind.Unresolved),
            Material: hit.Material,
            Point: (origin + (unit * distance))
        );
    }
    /// <summary>Tests whether the straight segment between two points crosses no surface. The whole segment is
    /// marched, so <paramref name="from"/> must lie clear of every surface by more than the march's accept threshold; a
    /// march that cannot finish its proof reports the segment blocked.</summary>
    /// <param name="from">The start, in world units.</param>
    /// <param name="to">The end, in world units.</param>
    /// <returns><see langword="true"/> when the field proves the segment clear.</returns>
    public bool SegmentClear(Double3 from, Double3 to) {
        var delta = (to - from);
        var length = delta.Length;

        if (length <= 0.0) {
            return true;
        }

        return (Cast(
            direction: delta,
            maxDistance: length,
            origin: from
        ).Kind == IrradianceRayKind.Miss);
    }

    private static FixedPosition ToPosition(Double3 point) => FixedPosition.FromLocal(local: ToVector(value: point));
    private static FixedVector3 ToVector(Double3 value) => new(
        X: FixedQ4816.FromDouble(value: value.X),
        Y: FixedQ4816.FromDouble(value: value.Y),
        Z: FixedQ4816.FromDouble(value: value.Z)
    );
}
