using Puck.Maths;

namespace Puck.Physics;

/// <summary>The surface of a <see cref="FixedStaticColliderKind.Field"/> collider: the zero set of a scalar field read
/// in the collider's own frame (a point's offset from <see cref="FixedStaticCollider.Center"/>, turned back by
/// <see cref="FixedStaticCollider.Rotation"/>), such as a relieved creation's own fixed-point program.</summary>
/// <param name="field">The field, read in the collider's frame; negative inside.</param>
/// <param name="boundScale">The factor <c>s</c> in <c>(0, 1]</c> that makes <c>s·F</c> 1-Lipschitz, so <c>|s·F(p)|</c>
/// never exceeds the distance from <c>p</c> to the surface.</param>
/// <param name="gradientStep">The tetrahedral tap offset, in metres, of the gradient taps that measure the
/// surface's normal and its distance; positive.</param>
/// <param name="bounded">Whether <see cref="FixedStaticCollider.Extent"/> bounds the surface about the collider's
/// centre; an unbounded field (relief over a plane) joins every query.</param>
/// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="boundScale"/> is outside <c>(0, 1]</c> or
/// <paramref name="gradientStep"/> is not positive.</exception>
public sealed class FixedColliderField(IFieldEvaluator field, FixedQ4816 boundScale, FixedQ4816 gradientStep, bool bounded = true) {
    /// <summary>Gets the field, read in the collider's frame.</summary>
    public IFieldEvaluator Field { get; } = (field ?? throw new ArgumentNullException(paramName: nameof(field)));
    /// <summary>Gets the factor that makes the field 1-Lipschitz.</summary>
    public FixedQ4816 BoundScale { get; } = (((boundScale > FixedQ4816.Zero) && (boundScale <= FixedQ4816.One))
        ? boundScale
        : throw new ArgumentOutOfRangeException(paramName: nameof(boundScale), message: "A field's bound scale must lie in (0, 1]."));
    /// <summary>Gets the gradient taps' half-span, in metres.</summary>
    public FixedQ4816 GradientStep { get; } = ((gradientStep > FixedQ4816.Zero)
        ? gradientStep
        : throw new ArgumentOutOfRangeException(paramName: nameof(gradientStep), message: "A field's gradient step must be positive."));
    /// <summary>Gets whether the collider's extent bounds the surface.</summary>
    public bool Bounded { get; } = bounded;
    /// <summary>Gets a cheaper field that bounds the surface, read in the same frame, or <see langword="null"/>: the
    /// surface lies where <see cref="EnvelopeScale"/> times it reads at most <see cref="EnvelopeGrowth"/>, so a point
    /// that much plus a body's reach outside that set is proved clear without reading <see cref="Field"/> (a relieved
    /// creation's shapes without their relief, grown by its amplitude).</summary>
    public IFieldEvaluator? Envelope { get; init; }
    /// <summary>Gets the factor that makes <see cref="Envelope"/> 1-Lipschitz, in <c>(0, 1]</c>.</summary>
    public FixedQ4816 EnvelopeScale { get; init; } = FixedQ4816.One;
    /// <summary>Gets the scaled envelope reading, in metres, at or under which the surface may lie.</summary>
    public FixedQ4816 EnvelopeGrowth { get; init; }
    /// <summary>Gets the analytic colliders whose surfaces the field relieves, in world space at the collider's pose
    /// when it was compiled, which a diagnostic samples witnesses from before projecting them onto the field; empty when
    /// none are known.</summary>
    public IReadOnlyList<FixedStaticCollider> Bases { get; init; } = [];
}
/// <summary>Contact against a <see cref="FixedStaticColliderKind.Field"/> collider. A field is no Euclidean distance,
/// so every distance here is measured: the field's value over its gradient's length at the sampled point (the first
/// order distance to its zero set), along the gradient's direction. The field's bound scale makes <c>s·F</c> a lower
/// bound on the distance, which proves a body clear in one sample before any gradient is taken.</summary>
public static class FixedFieldColliderContact {
    // The core samples a capsule's search spends at most, and the halving rounds that refine the least of them.
    private const int MaximumCoreSamples = 16;
    private const int RefineRounds = 3;
    // The projection's iteration ceiling and the step below which it has reached the surface.
    private const int ProjectionIterations = 16;
    private static readonly FixedQ4816 ProjectionTolerance = FixedQ4816.FromRawBits(value: 16L);
    private static readonly FixedQ4816 Two = FixedQ4816.FromInteger(value: 2L);

    // One query's reads of a field in its collider's frame, counted.
    private ref struct Sampler(FixedColliderField field) {
        public readonly FixedColliderField Field = field;
        public int Samples;
        public int Bounds;

        // Whether the envelope proves a point at least the reach from the surface.
        public bool EnvelopeClears(FixedVector3 local, FixedQ4816 reach) {
            if (Field.Envelope is not { } envelope) {
                return false;
            }

            Bounds++;
            return (envelope.TryDistance(position: FixedPosition.FromLocal(local: local), distance: out var value, material: out _) &&
                (((value * Field.EnvelopeScale) - Field.EnvelopeGrowth) >= reach));
        }

        public bool TryValue(FixedVector3 local, out FixedQ4816 value) {
            Samples++;
            return Field.Field.TryDistance(position: FixedPosition.FromLocal(local: local), distance: out value, material: out _);
        }
        // The measured distance and unit gradient direction at a local point.
        public bool TryMeasure(FixedVector3 local, out FixedQ4816 distance, out FixedVector3 normal) {
            distance = FixedQ4816.Zero;
            normal = FixedVector3.Zero;
            return (TryValue(local: local, value: out var value) && TryMeasure(distance: out distance, local: local, normal: out normal, value: value));
        }
        // The same from the value already read at the point. The gradient is the tetrahedral difference: the four taps
        // at h·(±1, ±1, ±1) with an even count of minus signs, Σ kᵢ·F(p + h·kᵢ) / 4h, a first-order
        // difference at four reads in place of six.
        public bool TryMeasure(FixedVector3 local, FixedQ4816 value, out FixedQ4816 distance, out FixedVector3 normal) {
            distance = FixedQ4816.Zero;
            normal = FixedVector3.Zero;

            var h = Field.GradientStep;
            var a = new FixedVector3(X: h, Y: -h, Z: -h);
            var b = new FixedVector3(X: -h, Y: -h, Z: h);
            var c = new FixedVector3(X: -h, Y: h, Z: -h);
            var d = new FixedVector3(X: h, Y: h, Z: h);

            if (
                !TryValue(local: (local + a), value: out var fa) || !TryValue(local: (local + b), value: out var fb) ||
                !TryValue(local: (local + c), value: out var fc) || !TryValue(local: (local + d), value: out var fd)
            ) {
                return false;
            }

            var span = ((h + h) + (h + h));
            var gradient = new FixedVector3(
                X: ((((fa - fb) - fc) + fd) / span),
                Y: ((((fc + fd) - fa) - fb) / span),
                Z: ((((fb + fd) - fa) - fc) / span)
            );
            var length = gradient.Length;

            // Field scaling changes this magnitude without changing the surface: only zero has no direction.
            if (length == FixedQ4816.Zero) {
                return false;
            }

            normal = gradient.Normalize();
            distance = (value / length);
            return true;
        }
        // The measured distance and normal at the core point nearest the surface: false when the envelope or one sample
        // at the core's middle proves every core point at least the reach from the surface (s·F is 1-Lipschitz).
        // A local normal cannot certify the rest of a nonconvex core: a floor at one end can hide an overhang at the
        // other. Sample the whole core and refine around the least reading, including an endpoint minimum.
        public bool TryCore(FixedVector3 lower, FixedVector3 axis, FixedQ4816 radius, FixedQ4816 reach, out FixedQ4816 distance, out FixedVector3 normal) {
            distance = FixedQ4816.Zero;
            normal = FixedVector3.Zero;

            var length = axis.Length;
            var middle = (lower + (axis / Two));

            if (
                EnvelopeClears(local: middle, reach: (reach + (length / Two))) ||
                !TryValue(local: middle, value: out var middleValue) ||
                (((middleValue * Field.BoundScale) - (length / Two)) >= reach)
            ) {
                return false;
            }

            if (length == FixedQ4816.Zero) {
                return TryMeasure(distance: out distance, local: middle, normal: out normal, value: middleValue);
            }

            var count = ((radius > FixedQ4816.Zero)
                ? (int)Math.Clamp(value: ((length.Value + (radius.Value - 1L)) / radius.Value), min: 1L, max: MaximumCoreSamples)
                : MaximumCoreSamples);
            var countFixed = FixedQ4816.FromInteger(value: count);
            var bestT = FixedQ4816.Zero;
            var bestValue = FixedQ4816.MaxValue;

            for (var sample = 0; (sample <= count); sample++) {
                var t = (FixedQ4816.FromInteger(value: sample) / countFixed);
                FixedQ4816 value;

                if ((sample + sample) == count) {
                    value = middleValue;
                } else if (!TryValue(local: (lower + (axis * t)), value: out value)) {
                    continue;
                }

                if (value < bestValue) {
                    (bestT, bestValue) = (t, value);
                }
            }

            if (bestValue == FixedQ4816.MaxValue) {
                return false;
            }

            var step = (FixedQ4816.One / countFixed);

            for (var round = 0; (round < RefineRounds); round++) {
                step /= Two;

                var (below, above) = ((bestT - step), (bestT + step));

                foreach (var t in (ReadOnlySpan<FixedQ4816>)[below, above]) {
                    if ((t < FixedQ4816.Zero) || (t > FixedQ4816.One) || !TryValue(local: (lower + (axis * t)), value: out var value)) {
                        continue;
                    }

                    if (value < bestValue) {
                        (bestT, bestValue) = (t, value);
                    }
                }
            }

            return TryMeasure(distance: out distance, local: (lower + (axis * bestT)), normal: out normal, value: bestValue);
        }
    }

    private static FixedColliderField Field(in FixedStaticCollider collider) =>
        (collider.Field ?? throw new InvalidOperationException(message: "A field collider carries no field."));
    private static FixedVector3 ToLocal(in FixedStaticCollider collider, FixedVector3 world) =>
        collider.Rotation.RotateInverse(vector: (world - collider.Center));

    /// <summary>Measures a field collider's surface from a world point: the first-order distance to it and its outward
    /// normal there.</summary>
    /// <param name="collider">The field collider.</param>
    /// <param name="point">The world-space point.</param>
    /// <param name="distance">The signed distance, in metres, negative inside.</param>
    /// <param name="normal">The unit outward normal, in world space.</param>
    /// <returns><see langword="false"/> when the field does not answer at the point or its gradient there is too short to
    /// give a direction.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="collider"/> carries no field.</exception>
    public static bool TryMeasure(in FixedStaticCollider collider, FixedVector3 point, out FixedQ4816 distance, out FixedVector3 normal) {
        var sampler = new Sampler(field: Field(collider: in collider));
        var measured = sampler.TryMeasure(local: ToLocal(collider: in collider, world: point), distance: out distance, normal: out var local);

        normal = (measured ? collider.Rotation.Rotate(vector: local) : FixedVector3.Zero);
        return measured;
    }
    /// <summary>Projects a world point onto a field collider's surface by repeated first-order steps along the measured
    /// normal.</summary>
    /// <param name="collider">The field collider.</param>
    /// <param name="probe">The world-space point to project.</param>
    /// <returns>The surface point and its unit outward normal; the probe itself and <c>+Y</c> where the field gives no
    /// measured direction there.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="collider"/> carries no field.</exception>
    public static (FixedVector3 Point, FixedVector3 Normal) Nearest(in FixedStaticCollider collider, FixedVector3 probe) {
        var sampler = new Sampler(field: Field(collider: in collider));
        var local = ToLocal(collider: in collider, world: probe);
        var normal = FixedVector3.UnitY;

        for (var iteration = 0; (iteration < ProjectionIterations); iteration++) {
            if (!sampler.TryMeasure(local: local, distance: out var distance, normal: out var measured)) {
                break;
            }

            normal = measured;
            local -= (measured * distance);
            if (FixedQ4816.Abs(value: distance) <= ProjectionTolerance) {
                break;
            }
        }

        FixedStaticContactWork.Process.CountField(bounds: sampler.Bounds, samples: sampler.Samples);
        return ((collider.Center + collider.Rotation.Rotate(vector: local)), collider.Rotation.Rotate(vector: normal));
    }
    /// <summary>Returns the push that keeps a body volume the contact skin outside a field collider's surface. A sphere
    /// or capsule measures its core where the field reads least (one sample proves a whole core clear), a box or
    /// ellipsoid at its centre with its support along the measured normal.</summary>
    /// <param name="collider">The field collider.</param>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local volume.</param>
    /// <param name="skin">The contact skin, in metres.</param>
    /// <param name="push">The push along the measured outward normal, when one is needed.</param>
    /// <returns><see langword="true"/> when the volume lies within the skin of the surface.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="collider"/> carries no field.</exception>
    public static bool TryPush(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 skin, out FixedContactPush push) {
        var sampler = new Sampler(field: Field(collider: in collider));
        var pushed = TryPush(sampler: ref sampler, collider: in collider, orientation: in orientation, position: position, push: out push, skin: skin, volume: in volume);

        FixedStaticContactWork.Process.CountField(bounds: sampler.Bounds, samples: sampler.Samples);
        return pushed;
    }
    private static bool TryPush(ref Sampler sampler, in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 skin, out FixedContactPush push) {
        push = default;

        if (volume.Kind is FixedBodyColliderKind.Box or FixedBodyColliderKind.Ellipsoid) {
            var worldCenter = (position + orientation.Rotate(vector: volume.Center));
            var center = ToLocal(collider: in collider, world: worldCenter);

            if (
                sampler.EnvelopeClears(local: center, reach: (volume.HalfExtents.Length + skin)) ||
                !sampler.TryValue(local: center, value: out var value) ||
                ((value * sampler.Field.BoundScale) >= (volume.HalfExtents.Length + skin)) ||
                !sampler.TryMeasure(distance: out var distance, local: center, normal: out var local, value: value)
            ) {
                return false;
            }

            var normal = collider.Rotation.Rotate(vector: local);
            var support = (FixedVector3.Dot(left: worldCenter, right: normal) - FixedColliderBounds.MinimumProjection(
                normal: normal, orientation: in orientation, position: position, volume: in volume));
            var penetration = ((support + skin) - distance);

            if (penetration <= FixedQ4816.Zero) {
                return false;
            }

            push = new FixedContactPush(Normal: normal, Penetration: penetration);
            return true;
        }

        var (start, end) = FixedDynamicBodyContacts.Segment(orientation: orientation, position: position, volume: in volume);
        var lower = ToLocal(collider: in collider, world: start);
        var axis = (ToLocal(collider: in collider, world: end) - lower);
        var reach = (volume.Radius + skin);

        if (!sampler.TryCore(axis: axis, distance: out var coreDistance, lower: lower, normal: out var coreNormal, radius: volume.Radius, reach: reach)) {
            return false;
        }

        var corePenetration = (reach - coreDistance);

        if (corePenetration <= FixedQ4816.Zero) {
            return false;
        }

        push = new FixedContactPush(Normal: collider.Rotation.Rotate(vector: coreNormal), Penetration: corePenetration);
        return true;
    }
}
