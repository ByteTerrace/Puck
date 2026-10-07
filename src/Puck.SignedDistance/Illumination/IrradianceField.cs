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
    private const int CandidateSteps = 32;

    private readonly SdfFieldEvaluator m_evaluator;

    /// <summary>Initializes a new instance of the <see cref="IrradianceField"/> class over a program.</summary>
    /// <param name="program">The program; it must use only the operations <see cref="SdfFieldEvaluator"/>
    /// interprets.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="program"/> holds an operation the evaluator refuses; the
    /// message names it. Independently composable Receive/Off instance policies are also refused because this evaluator has no
    /// render-policy filter; its ordinary collision field must not be mislabeled as the indirect caster field.</exception>
    public IrradianceField(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);
        if (program.IndirectInstancesComposable && program.Instances.Any(predicate: instance => (instance.Active && (instance.Indirect is SdfIndirectParticipation.Receive or SdfIndirectParticipation.Off)))) {
            throw new ArgumentException(message: "The CPU indirect field does not yet interpret per-instance Receive/Off participation.", paramName: nameof(program));
        }
        m_evaluator = new SdfFieldEvaluator(program: program);
    }

    /// <summary>Gets the program's conservative reciprocal Lipschitz bound: multiplying the field by this scale
    /// gives a distance lower bound. It imposes no minimum rate of growth away from a surface.</summary>
    public double StepScale => ((double)m_evaluator.StepScale);
    /// <summary>Gets the field's position resolution, in world units: one fixed-point tick. A solid thinner than this
    /// along a line is below the format, and the evaluator's own march does not resolve it either.</summary>
    public static double Resolution => ((double)FixedQ4816.Epsilon);
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
        var point = origin;
        var travelled = 0.0;

        for (var candidate = 0; (candidate < CandidateSteps); candidate++) {
            if (!m_evaluator.Raycast(
                dir: ToVector(value: unit),
                hit: out var hit,
                maxDist: FixedQ4816.FromDouble(value: (maxDistance - travelled)),
                origin: ToPosition(point: point)
            )) {
                return new IrradianceRay(Distance: maxDistance, Kind: IrradianceRayKind.Miss, Material: 0, Point: (origin + (unit * maxDistance)));
            }

            travelled += ((double)hit.Distance);
            var sample = FromPosition(position: hit.Point);

            if (hit.Distance > FixedQ4816.Zero) {
                point = sample;
            }

            if (hit.Confidence != WorldQueryConfidence.Exact) {
                break;
            }

            if (BracketsSurface(point: sample, radius: IrradianceAcceptance.SurfaceEpsilon)) {
                return new IrradianceRay(Distance: travelled, Kind: IrradianceRayKind.Hit, Material: hit.Material, Point: sample);
            }

            // The shared march's raw-field hit is only a candidate for illumination. A positive clamped value still
            // certifies a safe advance; a grazing ray whose candidates never approach a zero remains unresolved.
            if (!TryClampedDistance(distance: out var advance, material: out _, point: sample) || (travelled >= maxDistance)) {
                break;
            }

            // Keep sub-tick progress between candidates; reassigning the rounded sample on a zero-distance hit would
            // pin a shallow ray to one fixed-point height forever. Inset the sample's ball by the rounding offset.
            advance -= (sample - point).Length;

            if (advance <= 0.0) {
                break;
            }

            advance = Math.Min(val1: advance, val2: (maxDistance - travelled));
            point += (unit * advance);
            travelled += advance;
        }

        return new IrradianceRay(Distance: travelled, Kind: IrradianceRayKind.Unresolved, Material: 0, Point: point);
    }
    /// <summary>Sweeps a sphere along a ray through the field and returns how far its centre travelled before the sphere
    /// first touched a surface: a ray that stops on anything within <paramref name="radius"/> of its line.</summary>
    /// <param name="origin">The sphere's starting centre, in world units; it must start clear by more than the radius.</param>
    /// <param name="direction">The direction; it need not be unit length.</param>
    /// <param name="radius">The sphere's radius, in world units.</param>
    /// <param name="maxDistance">The farthest distance, in world units, the centre travels.</param>
    /// <returns>What the sweep ended on; for a hit, <see cref="IrradianceRay.Distance"/> is the centre's travel.</returns>
    public IrradianceRay Sweep(Double3 origin, Double3 direction, double radius, double maxDistance) {
        Casts++;

        var unit = direction.Normalize();

        if (!m_evaluator.SphereCast(
            dir: ToVector(value: unit),
            hit: out var hit,
            maxDist: FixedQ4816.FromDouble(value: maxDistance),
            origin: ToPosition(point: origin),
            radius: FixedQ4816.FromDouble(value: radius)
        )) {
            return new IrradianceRay(Distance: maxDistance, Kind: IrradianceRayKind.Miss, Material: 0, Point: (origin + (unit * maxDistance)));
        }

        var distance = ((double)hit.Distance);
        var point = FromPosition(position: hit.Point);

        return new IrradianceRay(
            Distance: distance,
            Kind: (((hit.Confidence == WorldQueryConfidence.Exact) && BracketsSurface(point: point, radius: (radius + IrradianceAcceptance.SurfaceEpsilon))) ? IrradianceRayKind.Hit : IrradianceRayKind.Unresolved),
            Material: hit.Material,
            Point: point
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

    // Opposite signs at two points certify a zero between them, whereas a small positive value alone is only a
    // distance lower bound. Snap the witness to the evaluator's coordinates and measure that actual segment. The
    // two-tick inset leaves room for rounding the witness without exceeding the requested radius.
    private bool BracketsSurface(Double3 point, double radius) {
        Samples++;

        if (!m_evaluator.TryDistance(distance: out var distance, material: out _, position: ToPosition(point: point))) {
            return false;
        }

        if (!TryGradient(gradient: out var normal, point: point)) {
            return false;
        }

        var reach = Math.Max(val1: 0.0, val2: (radius - (2.0 * ((double)FixedQ4816.Epsilon))));
        var witness = FromPosition(position: ToPosition(point: (point + (normal * ((distance >= FixedQ4816.Zero) ? -reach : reach)))));

        Samples++;

        if (((witness - point).Length > radius) ||
            !m_evaluator.TryDistance(distance: out var other, material: out _, position: ToPosition(point: witness))) {
            return false;
        }

        if (distance != FixedQ4816.Zero) {
            return ((distance > FixedQ4816.Zero) ? (other < FixedQ4816.Zero) : (other > FixedQ4816.Zero));
        }

        // A quantized zero can cover more than the accept radius in an eccentric gauge. Require strict signs on
        // both sides, so a wide zero plateau cannot be mistaken for a nearby surface either.
        var outward = FromPosition(position: ToPosition(point: (point + (normal * reach))));

        Samples++;

        return ((other < FixedQ4816.Zero) && ((outward - point).Length <= radius) &&
            m_evaluator.TryDistance(distance: out var outside, material: out _, position: ToPosition(point: outward)) &&
            (outside > FixedQ4816.Zero));
    }
    private static Double3 FromPosition(FixedPosition position) {
        var delta = (position - FixedPosition.Zero);

        return new Double3(X: ((double)delta.X), Y: ((double)delta.Y), Z: ((double)delta.Z));
    }
    private static FixedPosition ToPosition(Double3 point) => FixedPosition.FromLocal(local: ToVector(value: point));
    private static FixedVector3 ToVector(Double3 value) => new(
        X: FixedQ4816.FromDouble(value: value.X),
        Y: FixedQ4816.FromDouble(value: value.Y),
        Z: FixedQ4816.FromDouble(value: value.Z)
    );
}
