using System.Diagnostics;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE INTERVAL INTERPRETER: a third reading of the same compiled stream TryDistance walks, over a box of points instead
// of one. Every point step is a faithful rounding of an exact expression (one raw or less from it, on the Q16 grid), and
// each step here encloses that exact expression over the box with outward rounding, so the box's interval holds every
// TryDistance answer inside it: by induction over the stream, each point value lies in its interval, and a faithful
// rounding of a value inside a grid-ended interval stays inside it. Where the point code branches, the interval takes
// every branch the box can reach and joins them. Each rule mirrors its TryDistance or EvaluateShape step; a step changed
// there is a rule changed here, and SdfFieldBoundsLawTests holds every op, shape and blend's point answers inside these
// bounds. Two rules enclose rather than mirror: Sweep encloses whatever parameter its point search picks, and a
// Superellipsoid of any exponent but two raises through the interval power. A shape with no rule answers the unbounded
// interval, never an approximation, so it can only shrink the frame.
public sealed partial class SdfFieldEvaluator {
    private static readonly FixedQ4816 SuperellipsoidSphereExponent = FixedQ4816.FromInteger(value: 2L);
    // The sweep's parameter runs over the whole curve: every closest parameter the point search can pick lies in it.
    private static readonly FixedInterval SweepParameter = new(lower: FixedQ4816.Zero, upper: FixedQ4816.One);
    // A unit vector's component, and a cross product of two unit vectors' component, each rounded: inside [−2, 2].
    private static readonly FixedInterval SweepFrameComponent = new(lower: FixedQ4816.FromInteger(value: -2L), upper: FixedQ4816.FromInteger(value: 2L));
    // A sine or cosine: SinCos answers inside [−1, 1] for every angle.
    private static readonly FixedInterval UnitRange = new(lower: -FixedQ4816.One, upper: FixedQ4816.One);

    // The raws by which a fixed-point rotation can miss its exact linear image (see RotateBoundsByInverseQuaternion).
    private const long LinearRotationSlackRaws = 4L;

    // The frame's half-width in raws (see Frame), found once at construction.
    private readonly long m_frameRaw;

    /// <summary>Encloses every distance <see cref="TryDistance"/> answers at a point of the box
    /// <c>[<paramref name="lower"/>, <paramref name="upper"/>]</c>, corners included.</summary>
    /// <param name="lower">The box's least corner.</param>
    /// <param name="upper">The box's greatest corner, at or above <paramref name="lower"/> on every axis.</param>
    /// <param name="distance">The enclosing interval on success; zero on failure.</param>
    /// <returns><see langword="false"/> when the program has no shape or a corner leaves the evaluator's frame, as
    /// <see cref="TryDistance"/> refuses; <see langword="true"/> otherwise.</returns>
    /// <exception cref="ArgumentException"><paramref name="lower"/> exceeds <paramref name="upper"/> on an
    /// axis.</exception>
    /// <remarks>The interval is certified, not estimated: its lower endpoint is at or below the least point answer in the
    /// box and its upper at or above the greatest. It is a conservative hull, so it widens with the box, and a rotation
    /// or a fold that reuses a coordinate widens it further; a smaller box answers tighter.</remarks>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) =>
        TryDistanceBounds(
            distance: out distance,
            instructionsWalked: out _,
            lower: lower,
            upper: upper
        );
    /// <summary>Encloses every distance <see cref="TryDistance"/> answers at a point of the box, and counts the work: the
    /// instructions the walk visited, after the instance cull skipped every instance the box cannot reach.</summary>
    /// <param name="lower">The box's least corner.</param>
    /// <param name="upper">The box's greatest corner, at or above <paramref name="lower"/> on every axis.</param>
    /// <param name="distance">The enclosing interval on success; zero on failure.</param>
    /// <param name="instructionsWalked">The instructions the walk visited; zero on failure.</param>
    /// <returns><see langword="false"/> when the program has no shape or a corner leaves the evaluator's frame.</returns>
    /// <exception cref="ArgumentException"><paramref name="lower"/> exceeds <paramref name="upper"/> on an
    /// axis.</exception>
    /// <remarks>The cull is <see cref="TryDistance"/>'s own (<c>CanCullInstance</c>), asked of the box: a hard-union
    /// instance whose bound sphere lies at or beyond the running interval's upper end from every point of the box can
    /// only offer a candidate no point's running answer would take, so skipping it leaves every point answer inside
    /// the interval. It rests on the same sphere containment the point cull's exactness rests on.</remarks>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance, out int instructionsWalked) =>
        TryDistanceBounds(distance: out distance, instructionsWalked: out instructionsWalked, lower: lower, rotationsExpanded: out _, upper: upper);
    /// <summary>Encloses the box and counts visited instructions and full interval rotation expansions.</summary>
    /// <param name="lower">The box's least corner.</param>
    /// <param name="upper">The box's greatest corner.</param>
    /// <param name="distance">The enclosing interval on success.</param>
    /// <param name="instructionsWalked">The instructions visited after culling.</param>
    /// <param name="rotationsExpanded">The rotations evaluated through the full interval formula.</param>
    /// <returns>Whether the field can enclose this box.</returns>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance, out int instructionsWalked, out int rotationsExpanded) {
        distance = FixedInterval.FromPoint(value: FixedQ4816.Zero);
        instructionsWalked = 0;
        rotationsExpanded = 0;

        if (
            !m_hasShape ||
            !lower.TryDelta(delta: out var lowerPoint, origin: FixedPosition.Zero) ||
            !upper.TryDelta(delta: out var upperPoint, origin: FixedPosition.Zero)
        ) {
            return false;
        }

        if ((lowerPoint.X > upperPoint.X) || (lowerPoint.Y > upperPoint.Y) || (lowerPoint.Z > upperPoint.Z)) {
            throw new ArgumentException(message: $"The box's lower corner {lowerPoint} exceeds its upper corner {upperPoint}.", paramName: nameof(lower));
        }

        // A box reaching outside the frame holds points TryDistance refuses, so the bounds refuse it as well.
        if (!IsInFrame(point: lowerPoint) || !IsInFrame(point: upperPoint)) {
            return false;
        }

        distance = BoundsOver(
            instructionsWalked: out instructionsWalked,
            rotationsExpanded: out rotationsExpanded,
            world: new IntervalVector3(
                X: new FixedInterval(lower: lowerPoint.X, upper: upperPoint.X),
                Y: new FixedInterval(lower: lowerPoint.Y, upper: upperPoint.Y),
                Z: new FixedInterval(lower: lowerPoint.Z, upper: upperPoint.Z)
            )
        );

        return true;
    }

    /// <summary>Gets the evaluator's frame: the half-width, in each world axis, of the cube about the origin inside
    /// which <see cref="TryDistance"/> answers and outside which it refuses. Inside it, no step of the evaluation can
    /// leave the carrier. Negative when no position can be answered.</summary>
    /// <remarks>Found once, at construction, by the bounds interpreter: the frame is the widest power-of-two cube over
    /// which the bounds of the whole program stay bounded. A bounded interval proves that no step of the interval walk
    /// left the carrier, and every point step that reads the position lies inside its step's interval, so none can wrap
    /// inside the cube. Every instruction has an inclusion rule; one without would answer the unbounded interval, which
    /// can only shrink the frame, never widen it.</remarks>
    public FixedQ4816 Frame => FixedQ4816.FromRawBits(value: m_frameRaw);

    // Whether every axis of a point lies inside the frame (the carrier's minimum, whose magnitude has no raw, never does).
    private bool IsInFrame(FixedVector3 point) =>
        (WithinRaw(raw: point.X.Value, limit: m_frameRaw) && WithinRaw(raw: point.Y.Value, limit: m_frameRaw) && WithinRaw(raw: point.Z.Value, limit: m_frameRaw));
    private static bool WithinRaw(long raw, long limit) =>
        ((raw != long.MinValue) && (Math.Abs(value: raw) <= limit));
    // The widest power-of-two cube whose bounds stay bounded, capped so the instance cull's subtraction of each bound's
    // centre stays inside the carrier; minus one raw when not even a one-raw cube is bounded. Every instruction joins the
    // proof: one whose bounds were ever missing would answer the unbounded interval, which shrinks the frame, and never
    // grants one.
    private static long FindFrame(SdfFieldEvaluator evaluator) {
        var centre = 0UL;

        foreach (var bound in evaluator.m_cullBounds) {
            centre = Math.Max(val1: centre, val2: Math.Max(val1: FusedArithmetic.RawMagnitude(value: bound.CenterX.Value), val2: Math.Max(val1: FusedArithmetic.RawMagnitude(value: bound.CenterY.Value), val2: FusedArithmetic.RawMagnitude(value: bound.CenterZ.Value))));
        }

        var ceiling = ((centre >= ((ulong)long.MaxValue)) ? 0L : (long.MaxValue - ((long)centre)));
        var frame = -1L;
        var low = 0;
        var high = 62;

        // Bounded over a cube implies bounded over every cube inside it (the bounds are inclusion isotonic), so the
        // bounded cubes are an initial run of the powers of two.
        while (low <= high) {
            var middle = ((low + high) / 2);
            var radius = Math.Min(val1: (1L << middle), val2: ceiling);
            var side = new FixedInterval(lower: FixedQ4816.FromRawBits(value: -radius), upper: FixedQ4816.FromRawBits(value: radius));

            if (!evaluator.BoundsOver(instructionsWalked: out _, rotationsExpanded: out _, world: new IntervalVector3(X: side, Y: side, Z: side)).IsUnbounded) {
                frame = radius;
                low = (middle + 1);
            } else {
                high = (middle - 1);
            }
        }

        return frame;
    }
    // The bounds over a box whose every point the frame holds, and the instructions the walk visited.
    private FixedInterval BoundsOver(IntervalVector3 world, out int instructionsWalked, out int rotationsExpanded) {
        var local = world;
        var distanceScale = Point(value: FixedQ4816.One);
        var result = FixedInterval.FromPoint(value: FarDistance);
        Span<FixedInterval> scopes = stackalloc FixedInterval[SdfProgramBuilder.MaxFieldScopeDepth];
        var scopeDepth = 0;

        var cullIndex = 0;
        var walked = 0;

        rotationsExpanded = 0;

        for (var index = 0; (index < m_instructions.Length); index++) {
            if (
                (cullIndex < m_cullBounds.Length) &&
                (m_cullBounds[cullIndex].First == index)
            ) {
                var bound = m_cullBounds[cullIndex];

                cullIndex++;

                if (CanCullInstance(bound: bound, box: world, resultUpper: result.Upper)) {
                    index = (bound.End - 1);

                    continue;
                }
            }

            var instruction = m_instructions[index];

            ++walked;

            switch (instruction.Op) {
                case SdfOp.ResetPoint: {
                        local = world;
                        distanceScale = Point(value: FixedQ4816.One);
                        break;
                    }
                case SdfOp.Translate: {
                        local -= Vector(instruction: instruction);
                        break;
                    }
                case SdfOp.Rotate: {
                        if (TryAxialRotationBounds(instruction: instruction, p: local, rotated: out var axial)) {
                            local = axial;
                        } else {
                            rotationsExpanded++;
                            local = RotateBoundsByInverseQuaternion(instruction: instruction, p: local);
                        }
                        break;
                    }
                case SdfOp.Scale: {
                        var scale = Vector(instruction: instruction);

                        local = new(
                            X: (local.X / FixedInterval.FromPoint(value: scale.X)),
                            Y: (local.Y / FixedInterval.FromPoint(value: scale.Y)),
                            Z: (local.Z / FixedInterval.FromPoint(value: scale.Z))
                        );
                        distanceScale *= Point(value: instruction.Data0W);
                        break;
                    }
                case SdfOp.Repeat: {
                        var spacing = Vector(instruction: instruction);
                        var inverseSpacing = Vector1(instruction: instruction);

                        local = new(
                            X: (local.X - (Point(value: spacing.X) * FixedInterval.Round(value: (local.X * Point(value: inverseSpacing.X))))),
                            Y: (local.Y - (Point(value: spacing.Y) * FixedInterval.Round(value: (local.Y * Point(value: inverseSpacing.Y))))),
                            Z: (local.Z - (Point(value: spacing.Z) * FixedInterval.Round(value: (local.Z * Point(value: inverseSpacing.Z)))))
                        );
                        break;
                    }
                case SdfOp.RepeatLimited: {
                        var spacing = Vector(instruction: instruction);
                        var limit = Vector1(instruction: instruction);

                        local = new(
                            X: (local.X - (Point(value: spacing.X) * FixedInterval.Clamp(value: FixedInterval.Round(value: (local.X / Point(value: spacing.X))), minimum: -limit.X, maximum: limit.X))),
                            Y: (local.Y - (Point(value: spacing.Y) * FixedInterval.Clamp(value: FixedInterval.Round(value: (local.Y / Point(value: spacing.Y))), minimum: -limit.Y, maximum: limit.Y))),
                            Z: (local.Z - (Point(value: spacing.Z) * FixedInterval.Clamp(value: FixedInterval.Round(value: (local.Z / Point(value: spacing.Z))), minimum: -limit.Z, maximum: limit.Z)))
                        );
                        break;
                    }
                case SdfOp.SymmetryPlane: {
                        var normal = Vector(instruction: instruction);
                        var t = (Dot(left: local, right: normal) + Point(value: instruction.Data0W));
                        var twiceMin = (FixedInterval.Min(first: t, second: Point(value: FixedQ4816.Zero)) * Point(value: Two));

                        local = new(
                            X: (local.X - (Point(value: normal.X) * twiceMin)),
                            Y: (local.Y - (Point(value: normal.Y) * twiceMin)),
                            Z: (local.Z - (Point(value: normal.Z) * twiceMin))
                        );
                        break;
                    }
                case SdfOp.Elongate: {
                        var extents = Vector(instruction: instruction);

                        local = new(
                            X: ElongateBounds(extent: extents.X, value: local.X),
                            Y: ElongateBounds(extent: extents.Y, value: local.Y),
                            Z: ElongateBounds(extent: extents.Z, value: local.Z)
                        );
                        break;
                    }
                case SdfOp.Onion: {
                        result = (FixedInterval.Abs(value: result) - Point(value: instruction.Data0X));
                        break;
                    }
                case SdfOp.CellDisplace: {
                        var cells = CellBounds(
                            mode: ((SdfCellMode)instruction.Blend),
                            point: (local * instruction.Data0X),
                            randomness: instruction.Data0Z,
                            seed: instruction.Shape
                        );

                        result += (Point(value: instruction.Data0Y) * (cells - Point(value: Half)));
                        break;
                    }
                case SdfOp.Dilate: {
                        result -= Point(value: instruction.Data0X);
                        break;
                    }
                case SdfOp.PushField: {
                        scopes[scopeDepth++] = result;
                        result = FixedInterval.FromPoint(value: FarDistance);
                        break;
                    }
                case SdfOp.PopField: {
                        result = PopFieldBounds(candidate: result, instruction: instruction, saved: scopes[--scopeDepth]);
                        break;
                    }
                case SdfOp.ShapeBlend: {
                        if (instruction.Detail) {
                            break;
                        }

                        var candidate = (ShapeBounds(instruction: instruction, p: local) * distanceScale);

                        result = BlendBounds(blend: instruction.Blend, candidate: candidate, current: result, smoothRadius: instruction.Data1X);
                        break;
                    }
                default: {
                        throw new NotSupportedException(message: $"The bounds interpreter has no inclusion rule for op {instruction.Op}.");
                    }
            }
        }

        instructionsWalked = walked;

        return result;
    }
    // The box form of CanCullInstance: the least distance from any point of the box to the bound's centre is the length
    // of the gap between them, taken by the same exact-square, once-rounded length the point cull takes of a longer
    // vector, so it never exceeds what the point cull would read at a point of the box. The frame keeps each gap's
    // subtraction inside the carrier (FindFrame caps it by the farthest centre).
    private static bool CanCullInstance(CullBound bound, IntervalVector3 box, FixedQ4816 resultUpper) {
        static FixedQ4816 Gap(FixedInterval axis, FixedQ4816 centre) => FixedQ4816.Max(
            x: FixedQ4816.Max(x: (centre - axis.Upper), y: (axis.Lower - centre)),
            y: FixedQ4816.Zero
        );

        var gap = new FixedVector3(
            X: Gap(axis: box.X, centre: bound.CenterX),
            Y: Gap(axis: box.Y, centre: bound.CenterY),
            Z: Gap(axis: box.Z, centre: bound.CenterZ)
        );

        return ((gap.Length - bound.Radius) >= resultUpper);
    }

    /// <summary>Gets the ops the bounds interpreter has an inclusion rule for. Every op the point interpreter accepts
    /// must appear here; the sync law holds the two sets equal.</summary>
    public static IReadOnlyList<SdfOp> BoundedOps { get; } = [
        SdfOp.ResetPoint,
        SdfOp.Translate,
        SdfOp.Rotate,
        SdfOp.Scale,
        SdfOp.Repeat,
        SdfOp.RepeatLimited,
        SdfOp.SymmetryPlane,
        SdfOp.Elongate,
        SdfOp.Onion,
        SdfOp.CellDisplace,
        SdfOp.Dilate,
        SdfOp.PushField,
        SdfOp.PopField,
        SdfOp.ShapeBlend,
    ];
    /// <summary>Gets the shapes the bounds interpreter has an inclusion rule for. Every shape the point interpreter
    /// accepts must appear here; the sync law holds the two sets equal.</summary>
    public static IReadOnlyList<SdfShapeType> BoundedShapes { get; } = [
        SdfShapeType.Box,
        SdfShapeType.ScreenSlab,
        SdfShapeType.Capsule,
        SdfShapeType.Sphere,
        SdfShapeType.Torus,
        SdfShapeType.Cylinder,
        SdfShapeType.Plane,
        SdfShapeType.Vesica,
        SdfShapeType.RoundedRectangle,
        SdfShapeType.Trapezoid,
        SdfShapeType.ChamferedRectangle,
        SdfShapeType.RoundCone,
        SdfShapeType.ConvexPolygon,
        SdfShapeType.Superellipsoid,
        SdfShapeType.Sweep,
    ];

    private static FixedInterval Point(FixedQ4816 value) =>
        FixedInterval.FromPoint(value: value);
    private static FixedInterval Dot(IntervalVector3 left, FixedVector3 right) =>
        (((left.X * Point(value: right.X)) + (left.Y * Point(value: right.Y))) + (left.Z * Point(value: right.Z)));
    // x − clamp(x, −e, e) is x − e above e, x + e below −e and zero between: monotone in x, so its endpoints are exact.
    private static FixedInterval ElongateBounds(FixedInterval value, FixedQ4816 extent) {
        static FixedQ4816 Fold(FixedQ4816 x, FixedQ4816 e) =>
            (x - FixedQ4816.Clamp(maximum: e, minimum: -e, value: x));

        return (value.IsUnbounded
            ? FixedInterval.Entire
            : new(
                lower: Fold(x: value.Lower, e: extent),
                upper: Fold(x: value.Upper, e: extent)
            ));
    }
    // Simplifies the general formula only when its quaternion coefficients are exactly 0 or +/-1. A half turn still
    // intersects the three-use interval with the slackened linear image: returning just the negated interval would
    // tighten the old answer and could move a sweep. The range guard proves every intermediate of the general formula
    // bounded (at most three copies of an endpoint, plus four raws); outside it, including Entire, use that formula
    // so its absorbing overflow and frame discovery remain identical.
    private static bool TryAxialRotationBounds(IntervalVector3 p, CompiledInstruction instruction, out IntervalVector3 rotated) {
        rotated = p;

        if (!FitsAxialRotation(axis: p.X) || !FitsAxialRotation(axis: p.Y) || !FitsAxialRotation(axis: p.Z)) {
            return false;
        }

        var x = instruction.Data0X;
        var y = instruction.Data0Y;
        var z = instruction.Data0Z;
        var w = instruction.Data0W;
        var zero = FixedQ4816.Zero;
        var one = FixedQ4816.One;

        if ((x == zero) && (y == zero) && (z == zero)) {
            return ((w == one) || (w == -one));
        }

        if (w != zero) {
            return false;
        }

        var aroundX = (((x == one) || (x == -one)) && (y == zero) && (z == zero));
        var aroundY = ((x == zero) && ((y == one) || (y == -one)) && (z == zero));
        var aroundZ = ((x == zero) && (y == zero) && ((z == one) || (z == -one)));

        if (!aroundX && !aroundY && !aroundZ) {
            return false;
        }

        rotated = new IntervalVector3(
            X: (aroundX ? p.X : HalfTurnBounds(axis: p.X)),
            Y: (aroundY ? p.Y : HalfTurnBounds(axis: p.Y)),
            Z: (aroundZ ? p.Z : HalfTurnBounds(axis: p.Z))
        );
        return true;
    }
    private static bool FitsAxialRotation(FixedInterval axis) =>
        (!axis.IsUnbounded && (axis.Lower.Value >= -(long.MaxValue / 4L)) && (axis.Upper.Value <= (long.MaxValue / 4L)));
    private static FixedInterval HalfTurnBounds(FixedInterval axis) {
        var negated = -axis;
        var slack = new FixedInterval(lower: FixedQ4816.FromRawBits(value: -LinearRotationSlackRaws), upper: FixedQ4816.FromRawBits(value: LinearRotationSlackRaws));

        return Intersect(first: ((axis + negated) + negated), second: (negated + slack));
    }
    // FixedQuaternion.Rotate's two fused stages over the conjugate, each stage's three-term sum enclosed by the sum of its
    // outward-rounded products: t = u×v + w·v, then d = u×t, then v + 2d.
    private static IntervalVector3 RotateBoundsByInverseQuaternion(IntervalVector3 p, CompiledInstruction instruction) {
        var ux = Point(value: -instruction.Data0X);
        var uy = Point(value: -instruction.Data0Y);
        var uz = Point(value: -instruction.Data0Z);
        var w = Point(value: instruction.Data0W);
        var tx = (((uy * p.Z) - (uz * p.Y)) + (w * p.X));
        var ty = (((uz * p.X) - (ux * p.Z)) + (w * p.Y));
        var tz = (((ux * p.Y) - (uy * p.X)) + (w * p.Z));
        var dx = ((uy * tz) - (uz * ty));
        var dy = ((uz * tx) - (ux * tz));
        var dz = ((ux * ty) - (uy * tx));
        var stepwise = new IntervalVector3(
            X: ((p.X + dx) + dx),
            Y: ((p.Y + dy) + dy),
            Z: ((p.Z + dz) + dz)
        );

        // The step-by-step enclosure reuses each coordinate several times, so a rotation as plain as a half turn
        // triples a box's width. The exact expression is linear in p, p + 2u×(u×p + w·p) = M·p with
        // M = (1 − 2|u|²)·I + 2·u·uᵀ + 2w·[u]×, so its enclosure as M·p reads each coordinate once per axis. The point
        // code rounds each of t and d to the nearest raw, so its answer lies within 0.5 + 2·0.5 raws of the exact d and
        // twice that of the exact result for |u| ≤ 1: three raws, widened to four. That error budget holds only while
        // no fused stage wraps, and the linear form never sees the stages: a stage can leave the carrier while M·p,
        // a rotated point, stays inside it. The step-by-step enclosure does see them, a bounded one proving every
        // stage of t and d representable, so the two meet only then (see Intersect).
        var two = Point(value: Two);
        var usq = (((ux * ux) + (uy * uy)) + (uz * uz));
        var diagonal = (Point(value: FixedQ4816.One) - (two * usq));
        var m00 = (diagonal + (two * (ux * ux)));
        var m11 = (diagonal + (two * (uy * uy)));
        var m22 = (diagonal + (two * (uz * uz)));
        var m01 = (two * ((ux * uy) - (w * uz)));
        var m02 = (two * ((ux * uz) + (w * uy)));
        var m10 = (two * ((uy * ux) + (w * uz)));
        var m12 = (two * ((uy * uz) - (w * ux)));
        var m20 = (two * ((uz * ux) - (w * uy)));
        var m21 = (two * ((uz * uy) + (w * ux)));
        var slack = new FixedInterval(lower: FixedQ4816.FromRawBits(value: -LinearRotationSlackRaws), upper: FixedQ4816.FromRawBits(value: LinearRotationSlackRaws));

        return new(
            X: Intersect(first: stepwise.X, second: ((((m00 * p.X) + (m01 * p.Y)) + (m02 * p.Z)) + slack)),
            Y: Intersect(first: stepwise.Y, second: ((((m10 * p.X) + (m11 * p.Y)) + (m12 * p.Z)) + slack)),
            Z: Intersect(first: stepwise.Z, second: ((((m20 * p.X) + (m21 * p.Y)) + (m22 * p.Z)) + slack))
        );
    }
    // A faithful enclosure meets a tighter one that holds the point answer only where no point step wrapped. An unbounded
    // first stays unbounded: it is the proof that a point step may have wrapped, and the second, which assumes none did,
    // cannot bound what a wrap produced. An unbounded second, or an empty meet no point answer could produce, defers to
    // the first.
    private static FixedInterval Intersect(FixedInterval first, FixedInterval second) {
        if (first.IsUnbounded || second.IsUnbounded) {
            return first;
        }

        var lower = FixedQ4816.Max(x: first.Lower, y: second.Lower);
        var upper = FixedQ4816.Min(x: first.Upper, y: second.Upper);

        return ((lower <= upper)
            ? new FixedInterval(lower: lower, upper: upper)
            : first);
    }
    private static FixedInterval PopFieldBounds(FixedInterval candidate, FixedInterval saved, CompiledInstruction instruction) {
        var isStairs = (instruction.Blend is ((uint)SdfBlendOp.StairsUnion) or ((uint)SdfBlendOp.StairsSubtraction));
        var candidateScale = instruction.Data1Y;

        if (candidateScale > FixedQ4816.Zero) {
            candidate *= Point(value: candidateScale);
        }

        if (instruction.Blend == ((uint)SdfBlendOp.Morph)) {
            var from = Point(value: instruction.Data0Y);
            var to = Point(value: instruction.Data0Z);
            var t = FixedInterval.Clamp(
                value: ((Point(value: FixedQ4816.Zero) - from) / (to - from)),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            );

            return (((Point(value: FixedQ4816.One) - t) * saved) + (t * candidate));
        }

        if (isStairs) {
            var r = instruction.Data1X;
            var n = instruction.Data1Z;

            if ((n >= FixedQ4816.One) && (r > FixedQ4816.Zero)) {
                var s = (Point(value: r) / Point(value: n));
                var twoS = (s * Point(value: Two));
                var isSubtraction = (instruction.Blend == ((uint)SdfBlendOp.StairsSubtraction));
                var u = (isSubtraction
                    ? ((-candidate) - Point(value: r))
                    : (candidate - Point(value: r)));
                var argument = ((u - saved) + s);
                var m = (argument - (twoS * FixedInterval.Floor(value: (argument / twoS))));
                var w = (m - s);
                var stairs = (Point(value: Half) * ((u + saved) + FixedInterval.Abs(value: w)));

                return (isSubtraction
                    ? FixedInterval.Max(first: FixedInterval.Max(first: saved, second: -candidate), second: -stairs)
                    : FixedInterval.Min(first: FixedInterval.Min(first: saved, second: candidate), second: stairs));
            }
        }

        return BlendBounds(blend: instruction.Blend, candidate: candidate, current: saved, smoothRadius: instruction.Data1X);
    }
    // BlendShape over intervals, operation for operation.
    private static FixedInterval BlendBounds(FixedInterval current, FixedInterval candidate, uint blend, FixedQ4816 smoothRadius) {
        var smoothK = FixedQ4816.Max(x: smoothRadius, y: SmoothRadiusMin);
        var chamfer = Point(value: FixedQ4816.Max(x: smoothRadius, y: FixedQ4816.Zero));
        var root = Point(value: SqrtHalf);

        return blend switch {
            ((uint)SdfBlendOp.SmoothUnion) => SmoothUnionBounds(a: current, b: candidate, k: smoothK),
            ((uint)SdfBlendOp.Subtraction) => FixedInterval.Max(first: current, second: -candidate),
            ((uint)SdfBlendOp.Intersection) => FixedInterval.Max(first: current, second: candidate),
            ((uint)SdfBlendOp.Xor) => FixedInterval.Max(first: FixedInterval.Min(first: current, second: candidate), second: -FixedInterval.Max(first: current, second: candidate)),
            ((uint)SdfBlendOp.SmoothIntersection) => -SmoothUnionBounds(a: -current, b: -candidate, k: smoothK),
            ((uint)SdfBlendOp.SmoothSubtraction) => -SmoothUnionBounds(a: candidate, b: -current, k: smoothK),
            ((uint)SdfBlendOp.GrooveUnion) => FixedInterval.Max(first: FixedInterval.Min(first: current, second: candidate), second: (chamfer - FixedInterval.Magnitude(x: current, y: candidate))),
            ((uint)SdfBlendOp.PipeUnion) => FixedInterval.Min(first: FixedInterval.Min(first: current, second: candidate), second: (FixedInterval.Magnitude(x: current, y: candidate) - chamfer)),
            ((uint)SdfBlendOp.GrooveSubtraction) => FixedInterval.Max(first: FixedInterval.Max(first: current, second: -candidate), second: (chamfer - FixedInterval.Magnitude(x: current, y: candidate))),
            ((uint)SdfBlendOp.PipeSubtraction) => FixedInterval.Min(first: FixedInterval.Max(first: current, second: -candidate), second: (FixedInterval.Magnitude(x: current, y: candidate) - chamfer)),
            ((uint)SdfBlendOp.ChamferUnion) => FixedInterval.Min(first: FixedInterval.Min(first: current, second: candidate), second: (((current + candidate) - chamfer) * root)),
            ((uint)SdfBlendOp.ChamferIntersection) => FixedInterval.Max(first: FixedInterval.Max(first: current, second: candidate), second: (((current + candidate) + chamfer) * root)),
            ((uint)SdfBlendOp.ChamferSubtraction) => FixedInterval.Max(first: FixedInterval.Max(first: current, second: -candidate), second: (((current - candidate) + chamfer) * root)),
            _ => FixedInterval.Min(first: current, second: candidate),
        };
    }
    // BlendSmoothUnion over intervals: the h ≤ 0 select joins both arms wherever the box reaches both.
    private static FixedInterval SmoothUnionBounds(FixedInterval a, FixedInterval b, FixedQ4816 k) {
        var h = FixedInterval.Clamp(
            value: (Point(value: Half) + ((Point(value: Half) * (b - a)) / Point(value: k))),
            minimum: FixedQ4816.Zero,
            maximum: FixedQ4816.One
        );
        var oneMinusH = (Point(value: FixedQ4816.One) - h);
        var lerp = (a + ((b - a) * oneMinusH));
        var blended = ((h.Upper <= FixedQ4816.Zero)
            ? b
            : ((h.Lower > FixedQ4816.Zero)
                ? lerp
                : FixedInterval.Union(first: b, second: lerp)));

        return (blended - ((Point(value: k) * h) * oneMinusH));
    }
    // SampleCells over a box: walked exactly when the box stays inside one lattice cell on every axis, otherwise bounded by
    // the reach of the two nearest features (every feature offset lies within 1/2 + |randomness|/2 of its cell's centre, and
    // the second nearest is no farther than the nearer of two adjacent cells' features).
    private static FixedInterval CellBounds(IntervalVector3 point, uint seed, SdfCellMode mode, FixedQ4816 randomness) {
        // The frequency multiplication is a point-evaluation step even though a cell distance is bounded. Its
        // overflow must reach the program result rather than being hidden by the lattice's finite reach.
        if (point.X.IsUnbounded || point.Y.IsUnbounded || point.Z.IsUnbounded) {
            return FixedInterval.Entire;
        }

        var cx = (point.X.Lower.Value >> FixedQ4816.FractionBitCount);
        var cy = (point.Y.Lower.Value >> FixedQ4816.FractionBitCount);
        var cz = (point.Z.Lower.Value >> FixedQ4816.FractionBitCount);

        if (
            (cx != (point.X.Upper.Value >> FixedQ4816.FractionBitCount)) ||
            (cy != (point.Y.Upper.Value >> FixedQ4816.FractionBitCount)) ||
            (cz != (point.Z.Upper.Value >> FixedQ4816.FractionBitCount))
        ) {
            // Each delta component lies within |x| + 1/2 + |randomness|/2 + 1 of zero; the own cell (x = 0) bounds the
            // nearest feature and a face neighbour bounds the second, and F2 − F1 is at most the second.
            var reach = (FixedQ4816.Abs(value: randomness) * Half);
            var near = ((Half + reach) + FixedQ4816.One);
            var far = (near + FixedQ4816.One);
            var span = FixedInterval.Magnitude(
                x: Point(value: far),
                y: Point(value: near),
                z: Point(value: near)
            );

            return new(lower: FixedQ4816.Zero, upper: span.Upper);
        }

        var fraction = (point - new FixedVector3(
            X: FixedQ4816.FromInteger(value: cx),
            Y: FixedQ4816.FromInteger(value: cy),
            Z: FixedQ4816.FromInteger(value: cz)
        ));
        var first = Point(value: FixedQ4816.MaxValue);
        var second = Point(value: FixedQ4816.MaxValue);

        for (var z = -1; (z <= 1); z++) {
            for (var y = -1; (y <= 1); y++) {
                for (var x = -1; (x <= 1); x++) {
                    var h = Pcg3dLatticeNoise.Pcg3d(
                        x: unchecked((uint)(cx + x)) ^ seed,
                        y: unchecked((uint)(cy + y)) ^ (seed ^ 0x9E3779B9u),
                        z: unchecked((uint)(cz + z)) ^ (seed ^ 0x85EBCA77u)
                    );
                    var feature = new IntervalVector3(
                        X: (Point(value: (FixedQ4816.FromInteger(value: x) + Half)) + (Point(value: randomness) * (Point(value: FixedQ4816.FromRawBits(value: (h.X >> 16))) - Point(value: Half)))),
                        Y: (Point(value: (FixedQ4816.FromInteger(value: y) + Half)) + (Point(value: randomness) * (Point(value: FixedQ4816.FromRawBits(value: (h.Y >> 16))) - Point(value: Half)))),
                        Z: (Point(value: (FixedQ4816.FromInteger(value: z) + Half)) + (Point(value: randomness) * (Point(value: FixedQ4816.FromRawBits(value: (h.Z >> 16))) - Point(value: Half))))
                    );
                    var distance = FixedInterval.Magnitude(x: (feature.X - fraction.X), y: (feature.Y - fraction.Y), z: (feature.Z - fraction.Z));

                    // The two smallest of the running set, each an order statistic monotone in every argument.
                    var lowest = FixedInterval.Min(first: first, second: distance);

                    second = FixedInterval.Min(first: second, second: FixedInterval.Max(first: first, second: distance));
                    first = lowest;
                }
            }
        }

        return ((mode == SdfCellMode.F1)
            ? first
            : (second - first));
    }
    private static FixedInterval ShapeBounds(CompiledInstruction instruction, IntervalVector3 p) {
        return ((SdfShapeType)instruction.Shape) switch {
            SdfShapeType.Sphere => SphereBounds(p: p, radius: Point(value: instruction.Data0X)),
            SdfShapeType.Box or SdfShapeType.ScreenSlab => BoxBounds(p: p, halfExtents: Vector(instruction: instruction), cornerRadius: instruction.Data0W),
            SdfShapeType.Torus => TorusBounds(p: p, majorRadius: Point(value: instruction.Data0X), minorRadius: Point(value: instruction.Data0Y)),
            SdfShapeType.Plane => PlaneBounds(p: p, normal: Interval(value: Vector(instruction: instruction)), offset: Point(value: instruction.Data0W)),
            SdfShapeType.RoundCone => RoundConeBounds(p: p, lowerRadius: instruction.Data0X, upperRadius: instruction.Data0Y, height: instruction.Data0Z, b: instruction.Data0W, a: instruction.Data1Y),
            SdfShapeType.Capsule => CapsuleBounds(p: p, endpoint: Vector(instruction: instruction), radius: instruction.Data0W, inverseLengthSquared: instruction.Data1Y),
            SdfShapeType.Cylinder => (Extrude2DBounds(distance2D: (FixedInterval.Magnitude(x: p.X, y: p.Z) - Point(value: instruction.Data0X)), z: p.Y, halfDepth: instruction.Data0Y) - Point(value: instruction.Data1W)),
            SdfShapeType.Vesica => VesicaBounds(p: p, r: instruction.Data0X, d: instruction.Data0Y, b: instruction.Data0Z),
            SdfShapeType.RoundedRectangle => (LiftedBounds(
                p: p,
                instruction: instruction,
                liftAmount: instruction.Data0W,
                lift: instruction.Data1Y,
                capChamfer: instruction.Data1Z
            ) - Point(value: instruction.Data1W)),
            SdfShapeType.Trapezoid => (LiftedBounds(
                p: p,
                instruction: instruction,
                liftAmount: instruction.Data0W,
                lift: instruction.Data1Y,
                capChamfer: instruction.Data1Z
            ) - Point(value: instruction.Data1W)),
            SdfShapeType.ChamferedRectangle => (LiftedBounds(
                p: p,
                instruction: instruction,
                liftAmount: instruction.Data0W,
                lift: instruction.Data1Y,
                capChamfer: instruction.Data0Z
            ) - Point(value: instruction.Data1W)),
            SdfShapeType.Superellipsoid => SuperellipsoidBounds(p: p, radii: Vector(instruction: instruction), inverseRadii: new FixedVector3(X: instruction.Data1Y, Y: instruction.Data1Z, Z: instruction.Data1W), exponent: instruction.Data0W),
            SdfShapeType.Sweep => SweepBounds(p: p, curve: (instruction.SweepCurve ?? throw new UnreachableException(message: "Compile always attaches a SweepCurve to a Sweep instruction.")), twist: instruction.Data0Z, strandOffset: instruction.Data0W),
            SdfShapeType.ConvexPolygon => (LiftedBounds(
                p: p,
                instruction: instruction,
                liftAmount: instruction.Data0W,
                lift: instruction.Data1Y,
                capChamfer: instruction.Data1Z
            ) - Point(value: instruction.Data1W)),
            // A shape with no rule proves nothing: the unbounded interval, which shrinks the frame and never grants it.
            _ => FixedInterval.Entire,
        };
    }
    private static FixedInterval BoxBounds(IntervalVector3 p, FixedVector3 halfExtents, FixedQ4816 cornerRadius) =>
        BoxBounds(p: p, halfExtents: Interval(value: halfExtents), cornerRadius: Point(value: cornerRadius));
    private static FixedInterval BoxBounds(IntervalVector3 p, IntervalVector3 halfExtents, FixedInterval cornerRadius) {
        var absolute = p.Abs();
        var q = new IntervalVector3(X: (absolute.X - (halfExtents.X - cornerRadius)),
            Y: (absolute.Y - (halfExtents.Y - cornerRadius)), Z: (absolute.Z - (halfExtents.Z - cornerRadius)));
        var zero = Point(value: FixedQ4816.Zero);
        var outside = new IntervalVector3(
            X: FixedInterval.Max(first: q.X, second: zero),
            Y: FixedInterval.Max(first: q.Y, second: zero),
            Z: FixedInterval.Max(first: q.Z, second: zero)
        ).Length;
        var inside = FixedInterval.Min(first: FixedInterval.Max(first: q.X, second: FixedInterval.Max(first: q.Y, second: q.Z)), second: zero);

        return ((outside + inside) - cornerRadius);
    }
    private static FixedInterval CapsuleBounds(IntervalVector3 p, FixedVector3 endpoint, FixedQ4816 radius, FixedQ4816 inverseLengthSquared) =>
        CapsuleBounds(p: p, endpoint: Interval(value: endpoint), radius: Point(value: radius), inverseLengthSquared: Point(value: inverseLengthSquared));
    private static FixedInterval CapsuleBounds(IntervalVector3 p, IntervalVector3 endpoint, FixedInterval radius, FixedInterval inverseLengthSquared) {
        var h = FixedInterval.Clamp(
            value: ((((p.X * endpoint.X) + (p.Y * endpoint.Y)) + (p.Z * endpoint.Z)) * inverseLengthSquared),
            minimum: FixedQ4816.Zero,
            maximum: FixedQ4816.One
        );

        return (new IntervalVector3(
            X: (p.X - (endpoint.X * h)),
            Y: (p.Y - (endpoint.Y * h)),
            Z: (p.Z - (endpoint.Z * h))
        ).Length - radius);
    }
    // SdfSuperellipsoid over a box. The point side scales each ratio q_i/m into [0, 1] (each q_i is at most the largest,
    // m), raises it, sums, and roots the sum; the largest axis's ratio is exactly one and so is its power, so the sum is
    // at least one, which keeps the lower bound the gauge's own (m − 1)·min(r). A box reaching the centre (m = 0) joins
    // the centre arm, −min(r).
    private static FixedInterval SuperellipsoidBounds(IntervalVector3 p, FixedVector3 radii, FixedVector3 inverseRadii, FixedQ4816 exponent) {
        if (exponent == SuperellipsoidSphereExponent) {
            return SuperellipsoidSphereBounds(inverseRadii: inverseRadii, p: p, radii: radii);
        }

        var absolute = p.Abs();
        var q = new IntervalVector3(
            X: (absolute.X * Point(value: inverseRadii.X)),
            Y: (absolute.Y * Point(value: inverseRadii.Y)),
            Z: (absolute.Z * Point(value: inverseRadii.Z))
        );
        var m = FixedInterval.Max(first: q.X, second: FixedInterval.Max(first: q.Y, second: q.Z));
        var minimumRadius = Point(value: FixedQ4816.Min(x: radii.X, y: FixedQ4816.Min(x: radii.Y, y: radii.Z)));

        if (m.IsUnbounded) {
            return FixedInterval.Entire;
        }

        FixedInterval? joined = null;

        if (m.Lower <= FixedQ4816.Zero) {
            joined = -minimumRadius;
        }

        if (m.Upper > FixedQ4816.Zero) {
            var power = Point(value: exponent);

            FixedInterval Ratio(FixedInterval component) => ((m.Lower > FixedQ4816.Zero)
                ? FixedInterval.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Zero, value: (component / m))
                : new FixedInterval(lower: FixedQ4816.Zero, upper: FixedQ4816.One));
            var sum = ((FixedInterval.Pow(exponent: power, value: Ratio(component: q.X)) + FixedInterval.Pow(exponent: power, value: Ratio(component: q.Y))) + FixedInterval.Pow(exponent: power, value: Ratio(component: q.Z)));
            var root = FixedInterval.Pow(exponent: (Point(value: FixedQ4816.One) / power), value: FixedInterval.Max(first: sum, second: Point(value: FixedQ4816.One)));

            joined = Join(joined: joined, next: (((m * root) - Point(value: FixedQ4816.One)) * minimumRadius));
        }

        return joined!.Value;
    }
    // SdfSweep over a box, whatever parameter the point search picks: the centreline over the whole parameter range is
    // the box of the Bézier's interval evaluation, the radius the interval of every radius, and the strand offset the
    // interval its rotated frame reaches. The lower bound is the box's least distance to that hull, less the largest
    // radius and the margin. The upper bound is the lesser of the far side of that hull and, because the search starts
    // at the curve's start and only ever moves to a point it measures nearer, the distance to that start. The search's
    // own squared distances are enclosed too, so a box where they would leave the carrier answers unbounded and the
    // frame excludes it.
    private static FixedInterval SweepBounds(IntervalVector3 p, SweepCurveFixed curve, FixedQ4816 twist, FixedQ4816 strandOffset) {
        var t = SweepParameter;
        var centre = BezierBounds(a: curve.A, b: curve.B, c: curve.C, t: t);
        var reach = new IntervalVector3(X: (p.X - centre.X), Y: (p.Y - centre.Y), Z: (p.Z - centre.Z));
        var searchSquared = ((FixedInterval.Square(value: reach.X) + FixedInterval.Square(value: reach.Y)) + FixedInterval.Square(value: reach.Z));

        if (searchSquared.IsUnbounded) {
            return FixedInterval.Entire;
        }

        var taper = (Point(value: curve.RadiusStart) + ((Point(value: curve.RadiusEnd) - Point(value: curve.RadiusStart)) * t));
        var swell = FixedInterval.Max(first: FixedInterval.Sin(angle: (Point(value: SweepPi) * t)), second: Point(value: FixedQ4816.Zero));
        var radius = (taper + (Point(value: curve.Bulge) * FixedInterval.Pow(exponent: Point(value: SweepBulgeExponent), value: swell)));
        // SinCos answers inside [−1, 1] for every angle, so the phase (which never sees the point) needs no enclosure.
        var spread = (((SweepFrameComponent * UnitRange) + (SweepFrameComponent * UnitRange)) * Point(value: strandOffset));
        var offsetCentre = new IntervalVector3(X: (centre.X + spread), Y: (centre.Y + spread), Z: (centre.Z + spread));
        var distance = new IntervalVector3(X: (p.X - offsetCentre.X), Y: (p.Y - offsetCentre.Y), Z: (p.Z - offsetCentre.Z)).Length;
        var margin = (((Point(value: SweepBulgeMarginFactor) * FixedInterval.Abs(value: Point(value: curve.Bulge))) +
            ((Point(value: SweepStrandMarginFactor) * Point(value: strandOffset)) * (Point(value: FixedQ4816.One) + FixedInterval.Abs(value: Point(value: twist))))) +
            (Point(value: SweepTaperMarginFactor) * FixedInterval.Abs(value: (Point(value: curve.RadiusEnd) - Point(value: curve.RadiusStart)))));
        var hull = ((distance - radius) - margin);

        if (hull.IsUnbounded) {
            return FixedInterval.Entire;
        }

        // From the start: the search's first best is the start a itself and it only ever moves to a centreline point whose
        // squared distance rounds strictly lower. Each rounding is half a raw, so the pick's exact squared distance is at
        // most the start's plus one raw; its root, the strand's reach and half a raw of the final root bound the answer.
        var startSquared = ((FixedInterval.Square(value: (p.X - Point(value: curve.A.X))) + FixedInterval.Square(value: (p.Y - Point(value: curve.A.Y)))) + FixedInterval.Square(value: (p.Z - Point(value: curve.A.Z))));
        var fromStart = ((((FixedInterval.Sqrt(value: (startSquared + Point(value: FixedQ4816.Epsilon))) + new IntervalVector3(X: spread, Y: spread, Z: spread).Length) + Point(value: FixedQ4816.Epsilon)) - radius) - margin);

        return (fromStart.IsUnbounded
            ? hull
            : new FixedInterval(lower: hull.Lower, upper: FixedQ4816.Max(x: hull.Lower, y: FixedQ4816.Min(x: hull.Upper, y: fromStart.Upper))));
    }
    // SdfBezierPoint over a parameter interval: the two Lerps and the Lerp between them, each a single rounding of
    // from + (to − from)·t on the point side, enclosed here as written.
    private static IntervalVector3 BezierBounds(FixedVector3 a, FixedVector3 b, FixedVector3 c, FixedInterval t) {
        static FixedInterval Lerp(FixedInterval from, FixedInterval to, FixedInterval amount) =>
            (from + ((to - from) * amount));

        return new(
            X: Lerp(amount: t, from: Lerp(amount: t, from: Point(value: a.X), to: Point(value: b.X)), to: Lerp(amount: t, from: Point(value: b.X), to: Point(value: c.X))),
            Y: Lerp(amount: t, from: Lerp(amount: t, from: Point(value: a.Y), to: Point(value: b.Y)), to: Lerp(amount: t, from: Point(value: b.Y), to: Point(value: c.Y))),
            Z: Lerp(amount: t, from: Lerp(amount: t, from: Point(value: a.Z), to: Point(value: b.Z)), to: Lerp(amount: t, from: Point(value: b.Z), to: Point(value: c.Z)))
        );
    }
    private static FixedInterval SuperellipsoidSphereBounds(IntervalVector3 p, FixedVector3 radii, FixedVector3 inverseRadii) =>
        SuperellipsoidSphereBounds(p: p, radii: Interval(value: radii), inverseRadii: Interval(value: inverseRadii));
    private static FixedInterval SuperellipsoidSphereBounds(IntervalVector3 p, IntervalVector3 radii, IntervalVector3 inverseRadii) =>
        SuperellipsoidNormBounds(p: p, radii: radii, inverseRadii: inverseRadii, lowerFactor: Point(value: FixedQ4816.One));
    private static FixedInterval SuperellipsoidNormBounds(IntervalVector3 p, IntervalVector3 radii, IntervalVector3 inverseRadii,
        FixedInterval lowerFactor) {
        var absolute = p.Abs();
        var q = new IntervalVector3(
            X: (absolute.X * inverseRadii.X),
            Y: (absolute.Y * inverseRadii.Y),
            Z: (absolute.Z * inverseRadii.Z)
        );
        var minimumRadius = FixedInterval.Min(first: radii.X, second: FixedInterval.Min(first: radii.Y, second: radii.Z));

        var norm = q.Length;

        return FixedInterval.Union(first: (((lowerFactor * norm) - Point(value: FixedQ4816.One)) * minimumRadius),
            second: ((norm - Point(value: FixedQ4816.One)) * minimumRadius));
    }
    private static FixedInterval RoundConeBounds(IntervalVector3 p, FixedQ4816 lowerRadius, FixedQ4816 upperRadius, FixedQ4816 height, FixedQ4816 b, FixedQ4816 a) {
        var qx = FixedInterval.Magnitude(x: p.X, y: p.Z);
        var qy = p.Y;
        var k = ((qx * Point(value: -b)) + (qy * Point(value: a)));
        var top = (a * height);
        FixedInterval? joined = null;

        // Joining branch results cannot certify a predicate whose point arithmetic may have wrapped.
        if (k.IsUnbounded) {
            return FixedInterval.Entire;
        }

        if (k.Lower < FixedQ4816.Zero) {
            joined = Join(joined: joined, next: (FixedInterval.Magnitude(x: qx, y: qy) - Point(value: lowerRadius)));
        }

        if (k.Upper > top) {
            joined = Join(joined: joined, next: (FixedInterval.Magnitude(x: qx, y: (qy - Point(value: height))) - Point(value: upperRadius)));
        }

        if ((k.Upper >= FixedQ4816.Zero) && (k.Lower <= top)) {
            joined = Join(joined: joined, next: (((qx * Point(value: a)) + (qy * Point(value: b))) - Point(value: lowerRadius)));
        }

        return joined!.Value;
    }
    private static FixedInterval VesicaBounds(IntervalVector3 p, FixedQ4816 r, FixedQ4816 d, FixedQ4816 b) {
        var qx = FixedInterval.Magnitude(x: p.X, y: p.Z);
        var qy = FixedInterval.Abs(value: p.Y);
        var test = (((qy - Point(value: b)) * Point(value: d)) - (qx * Point(value: b)));
        var cap = FixedInterval.Magnitude(x: qx, y: (qy - Point(value: b)));
        var body = (FixedInterval.Magnitude(x: (qx + Point(value: d)), y: qy) - Point(value: r));

        if (test.IsUnbounded) {
            return FixedInterval.Entire;
        }

        // The point test compares two separately rounded products; their difference's interval decides it wherever it
        // keeps one sign.
        return ((test.Lower > FixedQ4816.Zero)
            ? cap
            : ((test.Upper <= FixedQ4816.Zero)
                ? body
                : FixedInterval.Union(first: cap, second: body)));
    }
    // ProjectLiftPoint and ApplyLift: an extruded profile (lift above one half) or a revolved one.
    // The profile is chosen by a switch on the instruction's shape rather than a delegate, so a bounds walk, which a
    // moving body's certified sweep runs every tick, allocates nothing.
    private static FixedInterval LiftedBounds(IntervalVector3 p, CompiledInstruction instruction, FixedQ4816 liftAmount, FixedQ4816 lift, FixedQ4816 capChamfer) {
        if (lift > Half) {
            return ExtrudeChamfer2DBounds(distance2D: ProfileBounds(instruction: instruction, p: new(X: p.X, Y: p.Y)), z: p.Z, halfDepth: liftAmount, c: capChamfer);
        }

        return ProfileBounds(instruction: instruction, p: new(X: (FixedInterval.Magnitude(x: p.X, y: p.Z) - Point(value: liftAmount)), Y: p.Y));
    }
    private static FixedInterval ProfileBounds(CompiledInstruction instruction, IntervalVector2 p) => ((SdfShapeType)instruction.Shape) switch {
        SdfShapeType.RoundedRectangle => RoundedRectangle2DBounds(p: p, halfWidth: instruction.Data0X, halfHeight: instruction.Data0Y, cornerRadius: instruction.Data0Z),
        SdfShapeType.Trapezoid => Trapezoid2DBounds(p: p, r1: instruction.Data0X, r2: instruction.Data0Y, halfHeight: instruction.Data0Z),
        SdfShapeType.ChamferedRectangle => ChamferBox2DBounds(p: p, halfWidth: instruction.Data0X, halfHeight: instruction.Data0Y, chamfer: instruction.Data0Z),
        SdfShapeType.ConvexPolygon => ConvexPolygon2DBounds(p: p, vertices: (instruction.ConvexPolygonVertices ?? [])),
        _ => throw new NotSupportedException(message: $"The bounds interpreter has no profile for shape {((SdfShapeType)instruction.Shape)}."),
    };
    private static FixedInterval Extrude2DBounds(FixedInterval distance2D, FixedInterval z, FixedQ4816 halfDepth) =>
        Extrude2DBounds(distance2D: distance2D, z: z, halfDepth: Point(value: halfDepth));
    private static FixedInterval Extrude2DBounds(FixedInterval distance2D, FixedInterval z, FixedInterval halfDepth) {
        var wy = (FixedInterval.Abs(value: z) - halfDepth);
        var zero = Point(value: FixedQ4816.Zero);
        var inside = FixedInterval.Min(first: FixedInterval.Max(first: distance2D, second: wy), second: zero);
        var outside = FixedInterval.Magnitude(x: FixedInterval.Max(first: distance2D, second: zero), y: FixedInterval.Max(first: wy, second: zero));

        return (inside + outside);
    }
    private static FixedInterval ExtrudeChamfer2DBounds(FixedInterval distance2D, FixedInterval z, FixedQ4816 halfDepth, FixedQ4816 c) =>
        ExtrudeChamfer2DBounds(distance2D: distance2D, z: z, halfDepth: Point(value: halfDepth), c: Point(value: c), sqrtHalf: Point(value: SqrtHalf));
    private static FixedInterval ExtrudeChamfer2DBounds(FixedInterval distance2D, FixedInterval z, FixedInterval halfDepth, FixedInterval c, FixedInterval sqrtHalf) {
        var wy = (FixedInterval.Abs(value: z) - halfDepth);
        var plain = Extrude2DBounds(distance2D: distance2D, halfDepth: halfDepth, z: z);
        var bevel = (((distance2D + wy) + c) * sqrtHalf);

        return FixedInterval.Max(first: plain, second: bevel);
    }
    private static FixedInterval RoundedRectangle2DBounds(IntervalVector2 p, FixedQ4816 halfWidth, FixedQ4816 halfHeight, FixedQ4816 cornerRadius) =>
        RoundedRectangle2DBounds(p: p, halfWidth: Point(value: halfWidth), halfHeight: Point(value: halfHeight), cornerRadius: Point(value: cornerRadius));
    private static FixedInterval RoundedRectangle2DBounds(IntervalVector2 p, FixedInterval halfWidth, FixedInterval halfHeight, FixedInterval cornerRadius) {
        var qx = ((FixedInterval.Abs(value: p.X) - halfWidth) + cornerRadius);
        var qy = ((FixedInterval.Abs(value: p.Y) - halfHeight) + cornerRadius);
        var zero = Point(value: FixedQ4816.Zero);
        var outside = FixedInterval.Magnitude(x: FixedInterval.Max(first: qx, second: zero), y: FixedInterval.Max(first: qy, second: zero));
        var inside = FixedInterval.Min(first: FixedInterval.Max(first: qx, second: qy), second: zero);

        return ((inside + outside) - cornerRadius);
    }
    private static FixedInterval ChamferBox2DBounds(IntervalVector2 p, FixedQ4816 halfWidth, FixedQ4816 halfHeight, FixedQ4816 chamfer) =>
        ChamferBox2DBounds(p: p, halfWidth: Point(value: halfWidth), halfHeight: Point(value: halfHeight), chamfer: Point(value: chamfer), sqrtHalf: Point(value: SqrtHalf));
    private static FixedInterval ChamferBox2DBounds(IntervalVector2 p, FixedInterval halfWidth, FixedInterval halfHeight, FixedInterval chamfer, FixedInterval sqrtHalf) {
        var qx = (FixedInterval.Abs(value: p.X) - halfWidth);
        var qy = (FixedInterval.Abs(value: p.Y) - halfHeight);
        var zero = Point(value: FixedQ4816.Zero);
        var box = (FixedInterval.Min(first: FixedInterval.Max(first: qx, second: qy), second: zero) + FixedInterval.Magnitude(x: FixedInterval.Max(first: qx, second: zero), y: FixedInterval.Max(first: qy, second: zero)));
        var bevel = (((qx + qy) + chamfer) * sqrtHalf);

        return FixedInterval.Max(first: box, second: bevel);
    }
    private static FixedInterval Trapezoid2DBounds(IntervalVector2 p, FixedQ4816 r1, FixedQ4816 r2, FixedQ4816 halfHeight) {
        var k2 = new FixedVector2(X: (r2 - r1), Y: (Two * halfHeight));

        return Trapezoid2DBounds(p: p, r1: Point(value: r1), r2: Point(value: r2), halfHeight: Point(value: halfHeight),
            slant: new IntervalVector2(X: Point(value: k2.X), Y: Point(value: k2.Y)),
            slantLengthSquared: Point(value: FixedVector2.Dot(left: k2, right: k2)));
    }
    private static FixedInterval Trapezoid2DBounds(IntervalVector2 p, FixedInterval r1, FixedInterval r2, FixedInterval halfHeight,
        IntervalVector2 slant, FixedInterval slantLengthSquared) {
        var px = FixedInterval.Abs(value: p.X);
        var zero = Point(value: FixedQ4816.Zero);
        // p.x − min(p.x, r) is max(p.x − r, 0) exactly; r is r1 below the axis and r2 at or above it.
        var r = ((p.Y.Upper < FixedQ4816.Zero)
            ? r1
            : ((p.Y.Lower >= FixedQ4816.Zero)
                ? r2
                : FixedInterval.Union(first: r1, second: r2)));
        var cax = FixedInterval.Max(first: (px - r), second: zero);
        var cay = (FixedInterval.Abs(value: p.Y) - halfHeight);
        var projection = ((slantLengthSquared == zero)
            ? zero
            : FixedInterval.Clamp(
                value: ((((r2 - px) * slant.X) + ((halfHeight - p.Y) * slant.Y)) / slantLengthSquared),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            ));
        var cbx = ((px - r2) + (slant.X * projection));
        var cby = ((p.Y - halfHeight) + (slant.Y * projection));
        var magnitude = FixedInterval.Sqrt(value: FixedInterval.Min(
            first: (FixedInterval.Square(value: cax) + FixedInterval.Square(value: cay)),
            second: (FixedInterval.Square(value: cbx) + FixedInterval.Square(value: cby))
        ));
        var surelyInside = ((cbx.Upper < FixedQ4816.Zero) && (cay.Upper < FixedQ4816.Zero));
        var surelyOutside = ((cbx.Lower >= FixedQ4816.Zero) || (cay.Lower >= FixedQ4816.Zero));

        return (surelyInside
            ? -magnitude
            : (surelyOutside
                ? magnitude
                : FixedInterval.Union(first: -magnitude, second: magnitude)));
    }
    // The exact polygon field over a box: the running minimum squared distance enclosed edge by edge, and the crossing
    // parity followed while every edge's three tests keep one answer over the box; once one does not, the sign is
    // either, and the bound is the hull of both.
    private static FixedInterval ConvexPolygon2DBounds(IntervalVector2 p, FixedVector2[] vertices) {
        var count = vertices.Length;
        var firstX = (p.X - Point(value: vertices[0].X));
        var firstY = (p.Y - Point(value: vertices[0].Y));
        var d = (FixedInterval.Square(value: firstX) + FixedInterval.Square(value: firstY));
        var negate = false;
        var parityKnown = true;
        var previous = vertices[(count - 1)];
        var zero = FixedQ4816.Zero;

        for (var i = 0; (i < count); i++) {
            var vertex = vertices[i];
            var e = (previous - vertex);
            var wx = (p.X - Point(value: vertex.X));
            var wy = (p.Y - Point(value: vertex.Y));
            var eDotE = FixedVector2.Dot(left: e, right: e);
            var t = FixedInterval.Clamp(
                value: (((wx * Point(value: e.X)) + (wy * Point(value: e.Y))) / Point(value: eDotE)),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            );
            var bx = (wx - (Point(value: e.X) * t));
            var by = (wy - (Point(value: e.Y) * t));

            d = FixedInterval.Min(first: d, second: (FixedInterval.Square(value: bx) + FixedInterval.Square(value: by)));

            var c1 = Decide(always: (p.Y.Lower >= vertex.Y), never: (p.Y.Upper < vertex.Y));
            var c2 = Decide(always: (p.Y.Upper < previous.Y), never: (p.Y.Lower >= previous.Y));
            var cross = ((Point(value: e.X) * wy) - (Point(value: e.Y) * wx));
            var c3 = Decide(always: (cross.Lower > zero), never: (cross.Upper <= zero));

            if ((c1 is null) || (c2 is null) || (c3 is null)) {
                parityKnown = false;
            } else if ((c1.Value && c2.Value && c3.Value) || (!c1.Value && !c2.Value && !c3.Value)) {
                negate = !negate;
            }

            previous = vertex;
        }

        var distance = FixedInterval.Sqrt(value: d);

        return (!parityKnown
            ? FixedInterval.Union(first: -distance, second: distance)
            : (negate
                ? -distance
                : distance));
    }
    private static bool? Decide(bool always, bool never) =>
        (always
            ? true
            : (never
                ? false
                : null));
    private static FixedInterval Join(FixedInterval? joined, FixedInterval next) =>
        ((joined is { } value)
            ? FixedInterval.Union(first: value, second: next)
            : next);

    // A box of points: one interval per axis.
    private readonly record struct IntervalVector3(FixedInterval X, FixedInterval Y, FixedInterval Z) {
        public FixedInterval Length => FixedInterval.Magnitude(x: X, y: Y, z: Z);

        public IntervalVector3 Abs() => new(
            X: FixedInterval.Abs(value: X),
            Y: FixedInterval.Abs(value: Y),
            Z: FixedInterval.Abs(value: Z)
        );

        public static IntervalVector3 operator -(IntervalVector3 left, FixedVector3 right) => new(
            X: (left.X - FixedInterval.FromPoint(value: right.X)),
            Y: (left.Y - FixedInterval.FromPoint(value: right.Y)),
            Z: (left.Z - FixedInterval.FromPoint(value: right.Z))
        );
        public static IntervalVector3 operator *(IntervalVector3 left, FixedQ4816 right) => new(
            X: (left.X * FixedInterval.FromPoint(value: right)),
            Y: (left.Y * FixedInterval.FromPoint(value: right)),
            Z: (left.Z * FixedInterval.FromPoint(value: right))
        );
    }
    private readonly record struct IntervalVector2(FixedInterval X, FixedInterval Y);
}
