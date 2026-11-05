using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.Physics;

/// <summary>The surface of a <see cref="FixedStaticColliderKind.Field"/> collider: the zero set of a scalar field read
/// in the collider's own frame (a point's offset from <see cref="FixedStaticCollider.Center"/>, turned back by
/// <see cref="FixedStaticCollider.Rotation"/>), such as a relieved creation's own fixed-point program.</summary>
/// <param name="field">The field, read in the collider's frame; negative inside. Its
/// <see cref="FieldEvaluatorCapabilities.StepScale"/> only proposes a ray's steps; every step is checked by the field's
/// sign, and clearance rests on <see cref="FixedColliderField.Bounds"/> alone.</param>
/// <param name="gradientStep">The tetrahedral tap offset, in metres, of the gradient taps that measure the field's
/// direction; positive.</param>
/// <param name="bounded">Whether <see cref="FixedStaticCollider.Extent"/> bounds the surface about the collider's
/// centre; an unbounded field (relief over a plane, a world-wide field) joins every query.</param>
/// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="gradientStep"/> is not positive.</exception>
public sealed class FixedColliderField(IFieldEvaluator field, FixedQ4816 gradientStep, bool bounded = true) {
    /// <summary>The tap offset, in metres, a field measures its direction with when nothing finer is asked of it: a
    /// thirty-second of a metre.</summary>
    public static FixedQ4816 DefaultGradientStep { get; } = FixedQ4816.FromRawBits(value: (FixedQ4816.One.Value / 32L));

    /// <summary>Gets the field, read in the collider's frame.</summary>
    public IFieldEvaluator Field { get; } = (field ?? throw new ArgumentNullException(paramName: nameof(field)));
    /// <summary>Gets the factor <c>s</c> in <c>[0, 1]</c> that scales a reading into a proposed ray step: the reciprocal
    /// of the field's Lipschitz bound. A proposal only; a ray never takes a step its readings' signs do not check.</summary>
    public FixedQ4816 BoundScale { get; } = FixedQ4816.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Zero, value: field.Capabilities.StepScale);
    /// <summary>Gets the gradient taps' half-span, in metres.</summary>
    public FixedQ4816 GradientStep { get; } = ((gradientStep > FixedQ4816.Zero)
        ? gradientStep
        : throw new ArgumentOutOfRangeException(paramName: nameof(gradientStep), message: "A field's gradient step must be positive."));
    /// <summary>Gets whether the collider's extent bounds the surface.</summary>
    public bool Bounded { get; } = bounded;
    /// <summary>Gets certified bounds on every answer <see cref="Field"/> gives over a box, read in the same frame, or
    /// <see langword="null"/>: the one proof of clearance. A box whose bounds stay above zero holds no point of the
    /// field's solid, so a body inside it is clear; without bounds no body is proved clear, only in contact or
    /// unproved.</summary>
    public IFieldBounds? Bounds { get; init; }
    /// <summary>Gets the analytic colliders whose surfaces the field relieves, in world space at the collider's pose
    /// when it was compiled, which a diagnostic samples witnesses from before projecting them onto the field; empty when
    /// none are known.</summary>
    public IReadOnlyList<FixedStaticCollider> Bases { get; init; } = [];
}
/// <summary>How one certified field contact solve ended.</summary>
public enum FixedFieldContactOutcome {
    /// <summary>The field's certified box bounds prove every point within the body's reach of its core outside the
    /// solid.</summary>
    Clear = 0,
    /// <summary>A surface point the field brackets lies within the body's reach of its core, or within a bracket's width
    /// past it (a body resting its skin outside, which is not pushed).</summary>
    Contact = 1,
    /// <summary>A point of the body's core reads inside the solid. The answer carries the field's measured direction out
    /// of it, or none where every reading the solve takes is symmetric: an embedded body the field gives no way out
    /// of, which is neither clear nor pushed.</summary>
    Embedded = 2,
    /// <summary>Neither proved clear nor in contact: the bound cannot prove the body clear, and the solve found no surface
    /// within its reach (the nearest it converged on lies farther), or the field gave no answer.</summary>
    Unproved = 3,
}
/// <summary>One certified field contact solve's answer, in world space.</summary>
/// <param name="Outcome">How the solve ended.</param>
/// <param name="Normal">The unit outward normal the push runs along: from the surface point to the core point it is
/// nearest for a contact, the field's measured direction out of the solid for an embedded core; zero otherwise,
/// including an embedded core the field measures no direction for.</param>
/// <param name="Penetration">How far, in metres, the body moves along <paramref name="Normal"/> to stand its reach outside
/// the surface; zero unless the body is pushed.</param>
/// <param name="Distance">The measured distance, in metres, from the core to the surface: the distance to a bracketed
/// surface point for a contact (never below the true distance), the negated depth of the deepest core point for an
/// embedded core; zero otherwise.</param>
public readonly record struct FixedFieldContact(FixedFieldContactOutcome Outcome, FixedVector3 Normal, FixedQ4816 Penetration, FixedQ4816 Distance) {
    /// <summary>Gets whether the body is pushed: a contact it penetrates, or an embedded core with a measured way out.</summary>
    public bool Pushes => ((Penetration > FixedQ4816.Zero) && (Normal != FixedVector3.Zero) &&
        (Outcome is FixedFieldContactOutcome.Contact or FixedFieldContactOutcome.Embedded));
    /// <summary>Gets whether the body is embedded with no measured way out.</summary>
    public bool Stuck => ((Outcome == FixedFieldContactOutcome.Embedded) && (Normal == FixedVector3.Zero));
    /// <summary>Gets whether the solve proved neither clearance nor its contact: <see cref="FixedFieldContactOutcome.Unproved"/>
    /// or <see cref="Stuck"/>. A caller that must not act on an unproved pose (a carry release, a step's landing) refuses
    /// it.</summary>
    public bool Uncertain => ((Outcome == FixedFieldContactOutcome.Unproved) || Stuck);
}
/// <summary>The one certified surface solve every field contact reads: relief colliders through
/// <see cref="FixedStaticContactSolver"/> and the world-wide field through <see cref="FixedFieldContactSolver"/>.</summary>
/// <remarks>
/// <para>A field's value is not a distance, so the solve never reads it as one. Clearance rests only on the field's
/// certified box bounds (<see cref="FixedColliderField.Bounds"/>), which enclose every answer the field gives in a box:
/// the whole core grown by the body's reach, then one core piece at a time, each piece no longer than the body's radius
/// and spanning its own end points, so the pieces cover the core end to end. Where the bounds cannot prove a piece, a
/// converged surface solve measures the distance: from the unproved core point the field reads least, a ray along the
/// field's direction to a bracketed zero crossing, re-aimed along the surface's own direction there until the two
/// agree, alternating with the core point nearest that surface point. A bracketed crossing is a surface point, so the
/// measured distance never falls below the true one and a push never carries a body past the skin.</para>
/// <para>An embedded core leaves through the surface it crosses: along the field's direction where the core crosses
/// the surface, or at the deepest core point that measures one. A core whose every reading is symmetric measures no
/// direction and is stuck, never clear, and never given a direction the field does not measure.</para>
/// </remarks>
public static class FixedFieldColliderContact {
    // The most core pieces a capsule's proof reads.
    private const int MaximumCorePieces = 15;
    // The ray search's iteration ceilings: advancing to a sign change, then narrowing it.
    private const int AdvanceIterations = 16;
    private const int BracketIterations = 48;
    // The rays one projection casts (the first and one re-aimed), and the core-to-surface alternations of one capsule.
    private const int AimRounds = 2;
    private const int PairRounds = 3;
    // The width, in metres, of the bracket a contact's surface point is accepted from: 32 raws, under a millimetre. A
    // measurement (a probe, a projection an attachment or a diagnostic reads) narrows its bracket to two raws.
    private static readonly FixedQ4816 Tolerance = FixedQ4816.FromRawBits(value: 32L);
    private static readonly FixedQ4816 MeasureTolerance = FixedQ4816.FromRawBits(value: 2L);
    // The cosine past which a ray and the surface's direction at its crossing agree: about 3.6°, where the distance
    // along the ray exceeds the perpendicular one by under 0.2%.
    private static readonly FixedQ4816 Aligned = (FixedQ4816.One - FixedQ4816.FromRawBits(value: 128L));
    // How far a projection searches for a surface no caller bounds, in metres.
    private static readonly FixedQ4816 OpenReach = FixedQ4816.FromInteger(value: 64L);
    private static readonly FixedQ4816 Two = FixedQ4816.FromInteger(value: 2L);
    // The raws a computed core point may lie from the exact point of the segment it stands for, past which a clearance box
    // still holds it: one rounding of the axis product and one of the sum, per coordinate.
    private static readonly FixedQ4816 PointSlack = FixedQ4816.FromRawBits(value: 2L);

    // One query's reads of a field in its collider's frame, counted, and the bracket width its crossings narrow to.
    private ref struct Sampler(FixedColliderField field, FixedQ4816 width) {
        public readonly FixedColliderField Field = field;
        public readonly FixedQ4816 Width = width;
        public int Samples;
        public int Bounds;

        // Whether the field's box bounds prove every point within the reach of the segment between two points outside the
        // solid: the box spanning the two points, grown by the reach and by the rounding the points carry, reads above
        // zero everywhere.
        public bool Clears(FixedVector3 from, FixedVector3 to, FixedQ4816 reach) {
            if (Field.Bounds is not { } bounds) {
                return false;
            }

            Bounds++;

            var grow = (reach + PointSlack);
            var spread = new FixedVector3(X: grow, Y: grow, Z: grow);
            var lower = new FixedVector3(X: FixedQ4816.Min(x: from.X, y: to.X), Y: FixedQ4816.Min(x: from.Y, y: to.Y), Z: FixedQ4816.Min(x: from.Z, y: to.Z));

            // A box the carrier cannot hold proves nothing.
            return (FixedVector3.TrySubtract(difference: out var least, left: lower, right: spread) &&
                FixedVector3.TryAdd(left: FixedVector3.Max(left: from, right: to), right: spread, sum: out var most) &&
                bounds.TryDistanceBounds(distance: out var interval, lower: FixedPosition.FromLocal(local: least), upper: FixedPosition.FromLocal(local: most)) &&
                !interval.IsUnbounded && (interval.Lower > FixedQ4816.Zero));
        }
        public bool TryValue(FixedVector3 local, out FixedQ4816 value) {
            Samples++;
            return Field.Field.TryDistance(position: FixedPosition.FromLocal(local: local), distance: out value, material: out _);
        }
        // The field's tetrahedral gradient: the four taps at h·(±1, ±1, ±1) with an even count of minus signs,
        // Σ kᵢ·F(p + h·kᵢ) / 4h, as a unit direction and its length; false where a tap does not answer or every tap reads
        // alike.
        public bool TryDirection(FixedVector3 local, out FixedVector3 direction, out FixedQ4816 length) {
            direction = FixedVector3.Zero;
            length = FixedQ4816.Zero;

            var h = Field.GradientStep;

            if (
                !TryValue(local: (local + new FixedVector3(X: h, Y: -h, Z: -h)), value: out var fa) ||
                !TryValue(local: (local + new FixedVector3(X: -h, Y: -h, Z: h)), value: out var fb) ||
                !TryValue(local: (local + new FixedVector3(X: -h, Y: h, Z: -h)), value: out var fc) ||
                !TryValue(local: (local + new FixedVector3(X: h, Y: h, Z: h)), value: out var fd)
            ) {
                return false;
            }

            var span = ((h + h) + (h + h));

            return TryUnit(
                length: out length,
                unit: out direction,
                vector: new FixedVector3(
                    X: ((((fa - fb) - fc) + fd) / span),
                    Y: ((((fc + fd) - fa) - fb) / span),
                    Z: ((((fb + fd) - fa) - fc) / span)
                )
            );
        }
        // Where the field first changes the sign it reads at the start, along start + direction·t for t in (0, limit]:
        // the far end of a bracket narrowed to the tolerance (or as far as its iterations reach), so the field reads the
        // other sign there and a zero lies within the bracket. Each advance is at least the reading scaled by the field's proposed step scale,
        // and a secant through the last two readings when that reaches farther; an overshoot only brackets a farther zero,
        // which is still a surface point.
        public bool TryCrossing(FixedVector3 start, FixedVector3 direction, FixedQ4816 value, FixedQ4816 guess, FixedQ4816 limit, out FixedQ4816 crossing) {
            crossing = FixedQ4816.Zero;

            var outside = (value > FixedQ4816.Zero);
            var (low, lowReading) = (FixedQ4816.Zero, FixedQ4816.Abs(value: value));
            var high = FixedQ4816.Zero;
            var highReading = FixedQ4816.Zero;
            var bracketed = false;
            var t = FixedQ4816.Clamp(maximum: limit, minimum: FixedQ4816.Min(x: Width, y: limit), value: guess);

            for (var iteration = 0; (iteration < AdvanceIterations); iteration++) {
                if (!TryValue(local: (start + (direction * t)), value: out var reading)) {
                    return false;
                }

                var ahead = (outside ? reading : -reading);

                if (ahead <= FixedQ4816.Zero) {
                    (high, highReading, bracketed) = (t, ahead, true);
                    break;
                }

                if (t >= limit) {
                    return false;
                }

                var clear = FixedQ4816.Max(x: Width, y: (ahead * Field.BoundScale));
                var next = (t + clear);

                if (ahead < lowReading) {
                    next = FixedQ4816.Max(x: next, y: (t + ((ahead * (t - low)) / (lowReading - ahead))));
                }

                (low, lowReading) = (t, ahead);
                t = FixedQ4816.Min(x: next, y: limit);
            }

            if (!bracketed) {
                return false;
            }

            // Illinois regula falsi: the end kept twice in a row has its reading halved, so neither end stalls, and a
            // step that did not halve the bracket is followed by a bisection, so the bracket at least halves every two.
            var kept = 0;
            var halved = true;

            for (var iteration = 0; ((iteration < BracketIterations) && ((high - low) > Width)); iteration++) {
                var width = (high - low);
                var quarter = FixedQ4816.Min(x: (Width / Two), y: (width / FixedQ4816.FromInteger(value: 4L)));
                // Halving a kept end's reading can leave both readings zero, where the secant has no root to aim at and the step bisects.
                var span = (lowReading - highReading);
                var middle = ((halved && (span > FixedQ4816.Zero))
                    ? FixedQ4816.Clamp(
                        maximum: (high - quarter),
                        minimum: (low + quarter),
                        value: (low + ((width * lowReading) / span))
                    )
                    : (low + (width / Two)));

                if (!TryValue(local: (start + (direction * middle)), value: out var reading)) {
                    return false;
                }

                var ahead = (outside ? reading : -reading);

                if (ahead <= FixedQ4816.Zero) {
                    (high, highReading) = (middle, ahead);
                    lowReading = ((kept < 0) ? (lowReading / Two) : lowReading);
                    kept = -1;
                } else {
                    (low, lowReading) = (middle, ahead);
                    highReading = ((kept > 0) ? (highReading / Two) : highReading);
                    kept = 1;
                }

                halved = ((high - low) <= (width / Two));
            }

            crossing = high;
            return true;
        }
        // The surface point nearest an outside point, as far as the solve converges, within the limit: a ray along the
        // field's direction to its crossing, re-aimed along the surface's direction at the crossing until the two
        // agree. Every crossing found is a surface point, so the least distance is never below the true one.
        public bool TryProject(FixedVector3 local, FixedQ4816 value, FixedQ4816 limit, out FixedVector3 surface, out FixedQ4816 distance) {
            surface = local;
            distance = FixedQ4816.MaxValue;

            if (!TryDirection(direction: out var outward, length: out var length, local: local)) {
                return false;
            }

            var aim = -outward;
            var guess = (value / length);

            for (var round = 0; (round < AimRounds); round++) {
                if (!TryCrossing(crossing: out var t, direction: aim, guess: guess, limit: limit, start: local, value: value)) {
                    break;
                }

                var point = (local + (aim * t));

                if (t < distance) {
                    (surface, distance) = (point, t);
                }

                if (
                    ((round + 1) == AimRounds) ||
                    !TryDirection(direction: out var normal, length: out _, local: point) ||
                    (FixedVector3.Dot(left: normal, right: -aim) >= Aligned)
                ) {
                    break;
                }

                (aim, guess) = (-normal, t);
            }

            return (distance != FixedQ4816.MaxValue);
        }
        // The way out of the solid from an inside point: the field's direction there and the depth to the crossing along
        // it.
        public bool TryExit(FixedVector3 local, FixedQ4816 value, out FixedVector3 normal, out FixedQ4816 depth) {
            depth = FixedQ4816.Zero;

            if (!TryDirection(direction: out normal, length: out var length, local: local)) {
                return false;
            }

            return TryExitAlong(depth: out depth, guess: (-value / length), local: local, normal: normal, value: value);
        }
        public bool TryExitAlong(FixedVector3 local, FixedQ4816 value, FixedVector3 normal, FixedQ4816 guess, out FixedQ4816 depth) {
            depth = FixedQ4816.Zero;

            return ((value == FixedQ4816.Zero) ||
                TryCrossing(crossing: out depth, direction: normal, guess: guess, limit: OpenReach, start: local, value: value));
        }
    }

    // A vector's unit direction and length; false for zero or a length past the carrier.
    private static bool TryUnit(FixedVector3 vector, out FixedVector3 unit, out FixedQ4816 length) {
        if (
            !FixedVectorMath.TryNormalizeWithMagnitude(
                rawMagnitude: out var magnitude,
                unitX: out var x,
                unitY: out var y,
                unitZ: out var z,
                x: vector.X.Value,
                y: vector.Y.Value,
                z: vector.Z.Value
            ) ||
            (magnitude == 0UL) ||
            (magnitude > ((ulong)long.MaxValue))
        ) {
            unit = FixedVector3.Zero;
            length = FixedQ4816.Zero;
            return false;
        }

        unit = new(X: FixedQ4816.FromRawBits(value: x), Y: FixedQ4816.FromRawBits(value: y), Z: FixedQ4816.FromRawBits(value: z));
        length = FixedQ4816.FromRawBits(value: ((long)magnitude));
        return true;
    }
    private static FixedColliderField Field(in FixedStaticCollider collider) =>
        (collider.Field ?? throw new InvalidOperationException(message: "A field collider carries no field."));
    private static FixedVector3 ToLocal(in FixedStaticCollider collider, FixedVector3 world) =>
        collider.Rotation.RotateInverse(vector: (world - collider.Center));

    // One core sample of a capsule's proof: its fraction along the core, the half-length of the piece it centres, as a
    // fraction, and its reading.
    private record struct CoreSample(FixedQ4816 T, FixedQ4816 Half, FixedQ4816 Value);

    // The solve over a core (a point when the axis is zero) of the radius, in the collider's frame. The box bounds first
    // try the whole core, then pieces no longer than the radius, each spanning its own computed end points from the core's
    // lower end to its upper end, so together they cover it. The converged surface solve runs from the piece the bounds
    // cannot prove that reads least at its centre, and from a second dip along the core when the first finds no surface
    // within the reach. A proved piece reads as far. Its piece tables are read only where written, so they skip the zero
    // fill.
    [SkipLocalsInit]
    private static FixedFieldContact SolveCore(ref Sampler sampler, FixedVector3 lower, FixedVector3 axis, FixedQ4816 radius, FixedQ4816 skin) {
        var reach = (radius + skin);
        var upper = (lower + axis);
        var length = axis.Length;

        if (sampler.Clears(from: lower, reach: reach, to: upper)) {
            return default;
        }

        var count = 1;

        if (length > FixedQ4816.Zero) {
            count = (int)((radius > FixedQ4816.Zero)
                ? Math.Clamp(value: ((length.Value + (radius.Value - 1L)) / radius.Value), min: 1L, max: MaximumCorePieces)
                : MaximumCorePieces);
        }

        Span<CoreSample> pieces = stackalloc CoreSample[MaximumCorePieces];
        Span<bool> proved = stackalloc bool[MaximumCorePieces];
        Span<CoreSample> open = stackalloc CoreSample[MaximumCorePieces];
        var openCount = 0;
        var countFixed = FixedQ4816.FromInteger(value: count);
        var pieceHalf = (FixedQ4816.One / (countFixed + countFixed));
        var from = lower;

        for (var piece = 0; (piece < count); piece++) {
            var to = (((piece + 1) == count) ? upper : (lower + (axis * (FixedQ4816.FromInteger(value: (piece + 1L)) / countFixed))));
            var t = (FixedQ4816.FromInteger(value: ((2L * piece) + 1L)) * pieceHalf);

            proved[piece] = sampler.Clears(from: from, reach: reach, to: to);

            if (proved[piece]) {
                pieces[piece] = new CoreSample(Half: pieceHalf, T: t, Value: FixedQ4816.MaxValue);
            } else {
                if (!sampler.TryValue(local: ((from + to) / Two), value: out var value)) {
                    return Unproved;
                }

                pieces[piece] = new CoreSample(Half: pieceHalf, T: t, Value: value);
                open[openCount++] = pieces[piece];
            }

            from = to;
        }

        if (openCount == 0) {
            return default;
        }

        var seed = Least(samples: open[..openCount]);

        // A seed piece at an end of the core: the end itself may read less.
        if ((length > FixedQ4816.Zero) && (seed.Value > FixedQ4816.Zero)) {
            var end = (((seed.T - seed.Half) <= FixedQ4816.Zero)
                ? FixedQ4816.Zero
                : (((seed.T + seed.Half) >= FixedQ4816.One) ? FixedQ4816.One : FixedQ4816.MinValue));

            if ((end != FixedQ4816.MinValue) && sampler.TryValue(local: (lower + (axis * end)), value: out var endValue) && (endValue < seed.Value)) {
                seed = new CoreSample(Half: FixedQ4816.Zero, T: end, Value: endValue);
            }
        }

        if (seed.Value <= FixedQ4816.Zero) {
            return Embedded(sampler: ref sampler, axis: axis, lower: lower, reach: reach, samples: pieces[..count], deepest: seed);
        }

        var contact = Pair(sampler: ref sampler, axis: axis, lower: lower, reach: reach, samples: pieces[..count], seed: seed);

        if (contact.Outcome != FixedFieldContactOutcome.Unproved) {
            return contact;
        }

        // Another place a surface may come within the reach: an open piece more than a radius along the core from the
        // seed that reads less than both its neighbours, a second dip the seed's solve does not reach. The least of them
        // is solved from too.
        var other = seed;
        var found = false;

        for (var piece = 0; (piece < count); piece++) {
            var candidate = pieces[piece];

            if (
                !proved[piece] &&
                ((length * FixedQ4816.Abs(value: (candidate.T - seed.T))) > radius) &&
                ((piece == 0) || (candidate.Value < pieces[piece - 1].Value)) &&
                (((piece + 1) == count) || (candidate.Value < pieces[piece + 1].Value)) &&
                (!found || (candidate.Value < other.Value))
            ) {
                (other, found) = (candidate, true);
            }
        }

        return (found
            ? Pair(sampler: ref sampler, axis: axis, lower: lower, reach: reach, samples: pieces[..count], seed: other)
            : Unproved);
    }
    // The core sample that reads least, the first along the core among equals.
    private static CoreSample Least(scoped ReadOnlySpan<CoreSample> samples) {
        var least = samples[0];

        foreach (var sample in samples) {
            if ((sample.Value < least.Value) || ((sample.Value == least.Value) && (sample.T < least.T))) {
                least = sample;
            }
        }

        return least;
    }
    // The converged surface solve from a core sample outside the solid: alternate the surface point nearest the core
    // point with the core point nearest that surface point. A contact when the least distance reached is within the
    // reach; an embedded core when the alternation lands inside the solid; unproved otherwise.
    private static FixedFieldContact Pair(ref Sampler sampler, FixedVector3 lower, FixedVector3 axis, FixedQ4816 reach, scoped ReadOnlySpan<CoreSample> samples, CoreSample seed) {
        var limit = ((reach + reach) + Tolerance);
        var value = seed.Value;
        var point = (lower + (axis * seed.T));
        var best = FixedQ4816.MaxValue;
        var bestPoint = point;
        var bestSurface = point;
        var squared = FixedVector3.Dot(left: axis, right: axis);

        for (var round = 0; (round < PairRounds); round++) {
            if (!sampler.TryProject(distance: out var distance, limit: limit, local: point, surface: out var surface, value: value)) {
                break;
            }

            if (distance < best) {
                (best, bestPoint, bestSurface) = (distance, point, surface);
            }

            if (squared == FixedQ4816.Zero) {
                break;
            }

            var next = FixedQ4816.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Zero, value: (FixedVector3.Dot(left: (surface - lower), right: axis) / squared));
            var nextPoint = (lower + (axis * next));

            if (((nextPoint - point).Length <= Tolerance) || !sampler.TryValue(local: nextPoint, value: out var nextValue)) {
                break;
            }

            if (nextValue <= FixedQ4816.Zero) {
                return Embedded(sampler: ref sampler, axis: axis, lower: lower, reach: reach, samples: samples,
                    deepest: new CoreSample(Half: FixedQ4816.Zero, T: next, Value: nextValue));
            }

            (value, point) = (nextValue, nextPoint);
        }

        // A surface within a bracket's width past the reach is the one a body rests its skin outside: touched, not pushed.
        if (best >= (reach + sampler.Width)) {
            return Unproved;
        }

        if (!TryUnit(length: out _, unit: out var away, vector: (bestPoint - bestSurface)) &&
            !sampler.TryDirection(direction: out away, length: out _, local: bestSurface)) {
            return Unproved;
        }

        return new FixedFieldContact(Distance: best, Normal: away, Outcome: FixedFieldContactOutcome.Contact,
            Penetration: FixedQ4816.Max(x: FixedQ4816.Zero, y: (reach - best)));
    }
    // An embedded core leaves along the field's direction where the core crosses the surface, or else at the deepest
    // core reading that measures a direction, by the depth of its deepest reading along it plus its reach.
    private static FixedFieldContact Embedded(ref Sampler sampler, FixedVector3 lower, FixedVector3 axis, FixedQ4816 reach, scoped ReadOnlySpan<CoreSample> samples, CoreSample deepest) {
        var inside = (lower + (axis * deepest.T));
        var normal = FixedVector3.Zero;
        var measured = false;
        var outsideT = FixedQ4816.MinValue;

        foreach (var sample in samples) {
            if ((sample.Value > FixedQ4816.Zero) &&
                ((outsideT == FixedQ4816.MinValue) || (FixedQ4816.Abs(value: (sample.T - deepest.T)) < FixedQ4816.Abs(value: (outsideT - deepest.T))))) {
                outsideT = sample.T;
            }
        }

        if (outsideT != FixedQ4816.MinValue) {
            // Halve the core between its inside and outside readings to the crossing, and read the surface's direction there.
            var (inT, outT) = (deepest.T, outsideT);

            for (var halving = 0; (halving < 12); halving++) {
                var middleT = ((inT + outT) / Two);

                if (!sampler.TryValue(local: (lower + (axis * middleT)), value: out var middleValue)) {
                    break;
                }

                (inT, outT) = ((middleValue <= FixedQ4816.Zero) ? (middleT, outT) : (inT, middleT));
            }

            measured = sampler.TryDirection(direction: out normal, length: out _, local: (lower + (axis * outT)));
        }

        var depth = FixedQ4816.Zero;

        if (measured) {
            measured = sampler.TryExitAlong(depth: out depth, guess: FixedQ4816.Max(x: Tolerance, y: (-deepest.Value * sampler.Field.BoundScale)),
                local: inside, normal: normal, value: deepest.Value);
        }

        if (!measured) {
            measured = sampler.TryExit(depth: out depth, local: inside, normal: out normal, value: deepest.Value);
        }

        if (!measured) {
            foreach (var sample in samples) {
                if ((sample.Value <= FixedQ4816.Zero) && (sample.T != deepest.T) &&
                    sampler.TryExit(depth: out depth, local: (lower + (axis * sample.T)), normal: out normal, value: sample.Value)) {
                    measured = true;
                    break;
                }
            }
        }

        return (measured
            ? new FixedFieldContact(Distance: -depth, Normal: normal, Outcome: FixedFieldContactOutcome.Embedded, Penetration: (depth + reach))
            : Stuck);
    }
    // A box or ellipsoid: its centre's surface solve, with its support along the measured normal.
    private static FixedFieldContact SolveVolume(ref Sampler sampler, in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 skin) {
        var worldCenter = (position + orientation.Rotate(vector: volume.Center));
        var center = ToLocal(collider: in collider, world: worldCenter);
        var outer = (volume.HalfExtents.Length + skin);

        if (sampler.Clears(from: center, reach: outer, to: center)) {
            return default;
        }

        if (!sampler.TryValue(local: center, value: out var value)) {
            return Unproved;
        }

        FixedVector3 local;
        FixedQ4816 distance;

        if (value <= FixedQ4816.Zero) {
            if (!sampler.TryExit(depth: out var depth, local: center, normal: out local, value: value)) {
                return Stuck;
            }

            distance = -depth;
        } else if (!sampler.TryProject(distance: out distance, limit: ((outer + outer) + Tolerance), local: center, surface: out var surface, value: value) ||
            (!TryUnit(length: out _, unit: out local, vector: (center - surface)) && !sampler.TryDirection(direction: out local, length: out _, local: surface))) {
            return Unproved;
        }

        var normal = collider.Rotation.Rotate(vector: local);
        var support = (FixedVector3.Dot(left: worldCenter, right: normal) - FixedColliderBounds.MinimumProjection(
            normal: normal, orientation: in orientation, position: position, volume: in volume));
        var penetration = ((support + skin) - distance);

        // A volume resting within a bracket's width past its skin touches the surface it measured; one farther is unproved.
        return ((penetration > -sampler.Width)
            ? new FixedFieldContact(Distance: distance, Normal: normal,
                Outcome: ((distance < FixedQ4816.Zero) ? FixedFieldContactOutcome.Embedded : FixedFieldContactOutcome.Contact),
                Penetration: FixedQ4816.Max(x: FixedQ4816.Zero, y: penetration))
            : Unproved);
    }

    private static FixedFieldContact Unproved => new(Distance: FixedQ4816.Zero, Normal: FixedVector3.Zero, Outcome: FixedFieldContactOutcome.Unproved, Penetration: FixedQ4816.Zero);
    // An embedded core the field measures no way out of.
    private static FixedFieldContact Stuck => new(Distance: FixedQ4816.Zero, Normal: FixedVector3.Zero, Outcome: FixedFieldContactOutcome.Embedded, Penetration: FixedQ4816.Zero);

    /// <summary>Solves a body volume against a field collider's surface: whether the volume lies within the contact skin
    /// of it, and the push that stands it the skin outside.</summary>
    /// <param name="collider">The field collider.</param>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local volume.</param>
    /// <param name="skin">The contact skin, in metres.</param>
    /// <returns>The solve's answer, its normal in world space.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="collider"/> carries no field.</exception>
    public static FixedFieldContact Solve(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 skin) {
        var sampler = new Sampler(field: Field(collider: in collider), width: Tolerance);
        FixedFieldContact contact;

        if (volume.Kind is FixedBodyColliderKind.Box or FixedBodyColliderKind.Ellipsoid) {
            contact = SolveVolume(collider: in collider, orientation: in orientation, position: position, sampler: ref sampler, skin: skin, volume: in volume);
        } else {
            var (start, end) = FixedDynamicBodyContacts.Segment(orientation: orientation, position: position, volume: in volume);
            var lower = ToLocal(collider: in collider, world: start);

            contact = SolveCore(axis: (ToLocal(collider: in collider, world: end) - lower), lower: lower, radius: volume.Radius, sampler: ref sampler, skin: skin);
            contact = (contact with { Normal = collider.Rotation.Rotate(vector: contact.Normal) });
        }

        FixedStaticContactWork.Process.CountField(bounds: sampler.Bounds, samples: sampler.Samples, unproved: contact.Uncertain);
        return contact;
    }
    /// <summary>Returns the push that keeps a body volume the contact skin outside a field collider's surface: the
    /// pushes of <see cref="Solve"/>.</summary>
    /// <param name="collider">The field collider.</param>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local volume.</param>
    /// <param name="skin">The contact skin, in metres.</param>
    /// <param name="push">The push along the measured outward normal, when one is needed.</param>
    /// <param name="uncertain">Whether the solve proved neither clearance nor its contact (<see cref="FixedFieldContact.Uncertain"/>).</param>
    /// <returns><see langword="true"/> when the solve proves a contact or measures the way out of an embedded volume.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="collider"/> carries no field.</exception>
    public static bool TryPush(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 skin, out FixedContactPush push, out bool uncertain) {
        var contact = Solve(collider: in collider, orientation: in orientation, position: position, skin: skin, volume: in volume);

        push = (contact.Pushes ? new FixedContactPush(Normal: contact.Normal, Penetration: contact.Penetration) : default);
        uncertain = contact.Uncertain;
        return contact.Pushes;
    }
    /// <summary>Measures a field collider's surface from a world point by the surface solve: the distance to the
    /// surface point it converges on outside the solid, or the negated depth to the way out inside it, and the outward
    /// normal there.</summary>
    /// <param name="collider">The field collider.</param>
    /// <param name="point">The world-space point.</param>
    /// <param name="distance">The signed distance, in metres, negative inside; never below the true distance outside.</param>
    /// <param name="normal">The unit outward normal, in world space.</param>
    /// <returns><see langword="false"/> when the field does not answer at the point, measures no direction there, or
    /// brackets no surface within 64 m.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="collider"/> carries no field.</exception>
    public static bool TryMeasure(in FixedStaticCollider collider, FixedVector3 point, out FixedQ4816 distance, out FixedVector3 normal) {
        var sampler = new Sampler(field: Field(collider: in collider), width: MeasureTolerance);
        var measured = Measure(distance: out distance, local: ToLocal(collider: in collider, world: point), normal: out var local,
            sampler: ref sampler, surface: out _);

        normal = (measured ? collider.Rotation.Rotate(vector: local) : FixedVector3.Zero);
        FixedStaticContactWork.Process.CountField(bounds: sampler.Bounds, samples: sampler.Samples, unproved: !measured);
        return measured;
    }
    /// <summary>Reads a field collider's field at a world point without a surface solve: the field's value scaled by
    /// <see cref="FixedColliderField.BoundScale"/>, whose sign is exact and whose magnitude is a lower bound on the
    /// Euclidean distance to the surface up to the field's fixed-point rounding, inside the solid as outside it. Where
    /// <see cref="TryMeasure"/> follows one direction to a surface point (an upper bound, which inside a union or at a
    /// crease of touching solids can run through other solids), this reading is the one a claim that a point lies off
    /// the surface can rest on. It counts no contact work.</summary>
    /// <param name="collider">The field collider.</param>
    /// <param name="point">The world-space point.</param>
    /// <param name="distance">The signed reading, in metres, negative inside, when the method returns <see langword="true"/>.</param>
    /// <param name="material">The material id the field reads at the point, when the method returns <see langword="true"/>.</param>
    /// <param name="normal">The field's unit outward direction there, in world space; zero where the field measures none.</param>
    /// <returns><see langword="false"/> when the field does not answer at the point.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="collider"/> carries no field.</exception>
    public static bool TryRead(in FixedStaticCollider collider, FixedVector3 point, out FixedQ4816 distance, out int material, out FixedVector3 normal) {
        var sampler = new Sampler(field: Field(collider: in collider), width: MeasureTolerance);
        var local = ToLocal(collider: in collider, world: point);

        normal = FixedVector3.Zero;
        if (!sampler.Field.Field.TryDistance(distance: out var value, material: out material, position: FixedPosition.FromLocal(local: local))) {
            distance = FixedQ4816.Zero;
            return false;
        }

        distance = (value * sampler.Field.BoundScale);
        if (sampler.TryDirection(direction: out var direction, length: out _, local: local)) {
            normal = collider.Rotation.Rotate(vector: direction);
        }
        return true;
    }
    /// <summary>Projects a world point onto a field collider's surface by the surface solve.</summary>
    /// <param name="collider">The field collider.</param>
    /// <param name="probe">The world-space point to project.</param>
    /// <param name="point">The surface point, in world space, when the method returns <see langword="true"/>.</param>
    /// <param name="normal">The unit outward normal there, in world space, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when the solve reaches no surface: the field does not answer, measures no
    /// direction at the probe, or brackets no surface within 64 m.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="collider"/> carries no field.</exception>
    public static bool TryNearest(in FixedStaticCollider collider, FixedVector3 probe, out FixedVector3 point, out FixedVector3 normal) {
        var sampler = new Sampler(field: Field(collider: in collider), width: MeasureTolerance);
        var measured = Measure(distance: out _, local: ToLocal(collider: in collider, world: probe), normal: out var local,
            sampler: ref sampler, surface: out var surface);

        point = (measured ? (collider.Center + collider.Rotation.Rotate(vector: surface)) : probe);
        normal = (measured ? collider.Rotation.Rotate(vector: local) : FixedVector3.Zero);
        FixedStaticContactWork.Process.CountField(bounds: sampler.Bounds, samples: sampler.Samples, unproved: !measured);
        return measured;
    }
    // A point's signed measured distance, outward normal and surface point, in the collider's frame.
    private static bool Measure(ref Sampler sampler, FixedVector3 local, out FixedQ4816 distance, out FixedVector3 normal, out FixedVector3 surface) {
        distance = FixedQ4816.Zero;
        normal = FixedVector3.Zero;
        surface = local;

        if (!sampler.TryValue(local: local, value: out var value)) {
            return false;
        }

        if (value <= FixedQ4816.Zero) {
            if (!sampler.TryExit(depth: out var depth, local: local, normal: out normal, value: value)) {
                return false;
            }

            (distance, surface) = (-depth, (local + (normal * depth)));
            return true;
        }

        if (!sampler.TryProject(distance: out distance, limit: OpenReach, local: local, surface: out surface, value: value)) {
            return false;
        }

        return (TryUnit(length: out _, unit: out normal, vector: (local - surface)) || sampler.TryDirection(direction: out normal, length: out _, local: surface));
    }
}
