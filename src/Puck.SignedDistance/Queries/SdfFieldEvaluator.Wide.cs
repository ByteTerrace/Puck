using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE WIDE READER: the stream read in double precision at several points at once, one point a lane of a hardware
// vector. It is the third lowering of the shared formulas (SdfFormula's wide form) and reads distances alone, for
// presentation: its answers are doubles and floats, which no fixed-point consumer can take, and nothing decides a hit,
// a contact or a material on them.
//
// It walks a region's tape (SdfFieldEvaluator.Regions.cs) from end to end for every lane, with none of the point
// walk's skips: a skip leaves an answer as it is, so a walk without one reads the same field. A tape is valid at every
// point of its region's padded box, so the lanes of one walk are points of one such box. Double arithmetic rounds
// where fixed point does not, so a wide answer stands within a few raws of the fixed one and is never compared to it
// for equality; a fold's seam, where a raw decides which copy a point reads, can fall a raw apart between the two.
//
// The carriers name every operation as the fixed carriers do (FixedQ4816, FixedVector2, FixedVector3,
// FixedQuaternion), which is what lets the wide lowering be the fixed lowering's text. What the formulas call that is
// no formula (a shape with a hand-written reader, the lattice noises, the ellipse's root) is read a lane at a time
// through the fixed-point reader, so the wide reader adds no second statement of any of them.
public sealed partial class SdfFieldEvaluator {
    /// <summary>Gets the kind counting wide walks, each one tape read at up to a vector's lanes of points.</summary>
    public static WorkKind WideWalks { get; } = new(name: "sdf.wide.walks", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the instructions the wide walks visited.</summary>
    public static WorkKind WideInstructions { get; } = new(name: "sdf.wide.instructions", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the normals the wide reader could not answer, which the fixed-point gradient did.</summary>
    public static WorkKind WideRefusals { get; } = new(name: "sdf.wide.refusals", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the process-wide work of every evaluator's wide reader. A monotonic total; a reader takes a window
    /// by reading twice.</summary>
    public static WorkCounterSet WideWork { get; } = new(name: "sdf.wide", kinds: [WideWalks, WideInstructions, WideRefusals]);

    // The lanes of the walk in progress that hold a point; a lane past them holds zeros, and a reader that works a
    // lane at a time skips it.
    [ThreadStatic]
    private static int WideLanes;

    /// <summary>Reads the unit surface normal at a point: the field's central-difference gradient, normalized.</summary>
    /// <param name="position">The world-space point, on or near a surface.</param>
    /// <param name="normal">The unit normal, in single precision.</param>
    /// <returns><see langword="false"/> where the field has no gradient: the program declares no shape, the point
    /// leaves the frame, or the differences are all zero.</returns>
    /// <remarks>Presentation only: the normal is a float and is not the bits <see cref="TryFieldGradient(FixedPosition, out FixedVector3)"/>
    /// answers. A regional evaluator reads the six taps in double precision through its wide reader, one walk over the
    /// tape of the point's region for as many taps as a hardware vector holds; any other evaluator, and a point whose
    /// region is not recorded or whose tape holds an op the wide reader lacks, reads the fixed-point gradient.</remarks>
    public bool TrySurfaceNormal(FixedPosition position, out Vector3 normal) {
        normal = Vector3.Zero;

        if (!TryWorldPoint(position: position, worldPosition: out var centre)) {
            return false;
        }

        if ((m_regions is not null) && TryRegionTape(point: centre, tape: out var tape) && TryWideGradient(centre: centre, gradient: out var wide, tape: tape)) {
            normal = Vector3.Normalize(value: wide);

            if (VectorFunctions.IsFinite(vector: normal)) {
                return true;
            }
        }

        WideWork.Count(kind: WideRefusals);

        if (!TryFieldGradient(gradient: out var gradient, position: position)) {
            normal = Vector3.Zero;

            return false;
        }

        normal = Vector3.Normalize(value: gradient.ToVector3());

        if (VectorFunctions.IsFinite(vector: normal)) {
            return true;
        }

        normal = Vector3.Zero;

        return false;
    }

    // The central differences at a point over one tape, a vector's lanes of taps a walk. The taps stand a gradient step
    // from the point, inside the pad every region's tape is recorded over.
    private bool TryWideGradient(FixedVector3 centre, ReadOnlySpan<int> tape, out Vector3 gradient) {
        const int Taps = 6;

        var step = ((double)GradientEpsilon);
        var frame = (((double)m_frameRaw) / 65536.0);
        var (x, y, z) = (((double)centre.X), ((double)centre.Y), ((double)centre.Z));
        Span<double> answers = stackalloc double[Taps];
        Span<double> px = stackalloc double[Vector<double>.Count];
        Span<double> py = stackalloc double[Vector<double>.Count];
        Span<double> pz = stackalloc double[Vector<double>.Count];

        gradient = Vector3.Zero;

        if ((GradientEpsilon.Value > RegionPad) || ((Math.Abs(value: x) + step) >= frame) || ((Math.Abs(value: y) + step) >= frame) || ((Math.Abs(value: z) + step) >= frame)) {
            return false;
        }

        for (var first = 0; (first < Taps); first += Vector<double>.Count) {
            var lanes = Math.Min(val1: Vector<double>.Count, val2: (Taps - first));

            px.Clear();
            py.Clear();
            pz.Clear();

            for (var lane = 0; (lane < lanes); lane++) {
                var tap = (first + lane);
                var offset = (((tap & 1) == 0) ? step : -step);

                px[lane] = (x + (((tap >> 1) == 0) ? offset : 0.0));
                py[lane] = (y + (((tap >> 1) == 1) ? offset : 0.0));
                pz[lane] = (z + (((tap >> 1) == 2) ? offset : 0.0));
            }

            WideLanes = lanes;

            if (!TryWalkWide(distance: out var distance, tape: tape, world: new Wide3(X: new Wide(lanes: new Vector<double>(values: px)), Y: new Wide(lanes: new Vector<double>(values: py)), Z: new Wide(lanes: new Vector<double>(values: pz))))) {
                return false;
            }

            for (var lane = 0; (lane < lanes); lane++) {
                answers[(first + lane)] = distance.Lanes[lane];
            }
        }

        gradient = new Vector3(x: ((float)(answers[0] - answers[1])), y: ((float)(answers[2] - answers[3])), z: ((float)(answers[4] - answers[5])));

        return true;
    }

    // Walks a tape at every lane's point and answers each lane's distance, or refuses a tape that holds an op the wide
    // reader has no lowering for (a warp, a render-only op). One accumulator a lane across the tape's ranges, as the
    // point walk holds; no skip, no root frame and no material.
    private bool TryWalkWide(ReadOnlySpan<int> tape, Wide3 world, out Wide distance) {
        var instructions = m_instructions;
        var far = ((Wide)((double)FarDistance));
        var result = far;
        var local = world;
        var distanceScale = Wide.One;
        Span<Wide> scopes = stackalloc Wide[SdfProgramBuilder.MaxFieldScopeDepth];
        var scopeDepth = 0;
        var visited = 0;

        distance = far;

        for (var range = 0; (range < tape.Length); range += 2) {
            for (var index = tape[range]; (index < tape[(range + 1)]); index++) {
                ref readonly var compiled = ref instructions[index];
                var instruction = new WideOperands(compiled: in compiled);

                ++visited;

                switch (compiled.PointAction) {
                    case SdfPointAction.ResetPoint: {
                            local = world;
                            distanceScale = Wide.One;
                            break;
                        }
                    case SdfPointAction.Translate: {
                            local = TranslatePoint(instruction: in instruction, p: local);
                            break;
                        }
                    case SdfPointAction.Rotate: {
                            local = RotatePoint(instruction: in instruction, p: local);
                            break;
                        }
                    case SdfPointAction.Scale: {
                            local = ScalePoint(instruction: in instruction, p: local);
                            distanceScale = ScaleDistance(distanceScale: distanceScale, instruction: in instruction);
                            break;
                        }
                    case SdfPointAction.Fold: {
                            local = MapPoint(instruction: in instruction, p: local);
                            break;
                        }
                    case SdfPointAction.EditField: {
                            result = EditField(distance: result, instruction: in instruction, p: local);
                            break;
                        }
                    case SdfPointAction.WeightedRelief: {
                            result = NoiseDisplaceField(
                                distance: result,
                                instruction: in instruction,
                                p: local,
                                weight: ((compiled.ReliefMasks is { } masks) ? MaskProduct(masks: masks, q: local) : Wide.One)
                            );
                            break;
                        }
                    case SdfPointAction.Identity: {
                            break;
                        }
                    case SdfPointAction.PushField: {
                            scopes[scopeDepth++] = result;
                            result = far;
                            break;
                        }
                    case SdfPointAction.PopField: {
                            result = BlendShape(
                                blend: compiled.Blend,
                                candidate: instruction.ScaleScope(candidate: result),
                                current: scopes[--scopeDepth],
                                operand: PopOperand(instruction: in instruction),
                                smoothRadius: instruction.Data1X
                            );
                            break;
                        }
                    case SdfPointAction.ShapeBlend: {
                            if (compiled.Detail) {
                                break;
                            }

                            result = BlendShape(
                                blend: compiled.Blend,
                                candidate: instruction.ScaleScope(candidate: (EvaluateShape(instruction: in instruction, p: local) * distanceScale)),
                                current: result,
                                operand: Wide.Zero,
                                smoothRadius: instruction.Data1X
                            );
                            break;
                        }
                    default: {
                            return false;
                        }
                }
            }
        }

        WideWork.Count(kind: WideWalks);
        WideWork.Add(amount: visited, kind: WideInstructions);
        distance = result;

        return true;
    }
    // The product of a masked relief's weights at each lane's point: per mask, the least distance of its regions, read
    // against the band's shared formula. The point walk's early exits change no value, so the wide walk takes none.
    private static Wide MaskProduct(ReliefMaskFixed[] masks, Wide3 q) {
        var weight = Wide.One;

        foreach (var mask in masks) {
            var distance = ((Wide)double.MaxValue);

            foreach (var region in mask.Regions) {
                var primitive = region.Primitive;
                var u = (q - new Wide3(X: ((double)region.Origin.X), Y: ((double)region.Origin.Y), Z: ((double)region.Origin.Z)));

                if (!region.Identity) {
                    u = new WideRotation(W: ((double)region.Rotation.W), X: ((double)region.Rotation.X), Y: ((double)region.Rotation.Y), Z: ((double)region.Rotation.Z)).RotateInverse(vector: u);
                }

                distance = Wide.Min(x: distance, y: EvaluateShape(instruction: new WideOperands(compiled: in primitive), p: u));
            }

            weight *= SdfReliefBandWeight(distance: distance, end: ((double)mask.End), inside: ((double)mask.Inside), outside: ((double)mask.Outside), start: ((double)mask.Start));
        }

        return weight;
    }

    // === What the formulas call that is no formula, a lane at a time through the fixed-point reader ====================

    private static FixedQ4816 LaneToFixed(double value) =>
        (double.IsFinite(d: value) ? FixedQ4816.FromDouble(value: Math.Clamp(value: value, max: 1.0e12, min: -1.0e12)) : FixedQ4816.Zero);
    private static FixedVector3 LaneToFixed(Wide3 value, int lane) =>
        new(X: LaneToFixed(value: value.X.Lanes[lane]), Y: LaneToFixed(value: value.Y.Lanes[lane]), Z: LaneToFixed(value: value.Z.Lanes[lane]));
    private static Wide EvaluateShapeEscape(in WideOperands instruction, Wide3 p) {
        Span<double> lanes = stackalloc double[Vector<double>.Count];

        lanes.Clear();

        for (var lane = 0; (lane < WideLanes); lane++) {
            lanes[lane] = ((double)EvaluateShapeEscape(instruction: in instruction.Compiled, p: LaneToFixed(lane: lane, value: p)));
        }

        return new Wide(lanes: new Vector<double>(values: lanes));
    }
    private static Wide SdfEllipse2D(Wide2 p, Wide semiX, Wide semiY) {
        Span<double> lanes = stackalloc double[Vector<double>.Count];

        lanes.Clear();

        for (var lane = 0; (lane < WideLanes); lane++) {
            lanes[lane] = ((double)SdfEllipse2D(new FixedVector2(X: LaneToFixed(value: p.X.Lanes[lane]), Y: LaneToFixed(value: p.Y.Lanes[lane])), LaneToFixed(value: semiX.Lanes[lane]), LaneToFixed(value: semiY.Lanes[lane])));
        }

        return new Wide(lanes: new Vector<double>(values: lanes));
    }
    private static Wide SampleCells(Wide3 point, uint seed, SdfCellMode mode, Wide randomness) {
        Span<double> lanes = stackalloc double[Vector<double>.Count];

        lanes.Clear();

        for (var lane = 0; (lane < WideLanes); lane++) {
            lanes[lane] = ((double)SampleCells(LaneToFixed(lane: lane, value: point), seed, mode, LaneToFixed(value: randomness.Lanes[lane])));
        }

        return new Wide(lanes: new Vector<double>(values: lanes));
    }
    private static Wide WideFbm3(Wide3 q, uint seed, uint octaves, Wide gain, Wide lacunarity) {
        Span<double> lanes = stackalloc double[Vector<double>.Count];

        lanes.Clear();

        for (var lane = 0; (lane < WideLanes); lane++) {
            lanes[lane] = ((double)Pcg3dLatticeNoise.Fbm3(gain: LaneToFixed(value: gain.Lanes[lane]), lacunarity: LaneToFixed(value: lacunarity.Lanes[lane]), octaves: ((int)octaves), q: LaneToFixed(lane: lane, value: q), seed: seed));
        }

        return new Wide(lanes: new Vector<double>(values: lanes));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Wide SuperellipsoidScaledRatio(Wide magnitude, Wide radius, Wide minRadius) => ((magnitude * minRadius) / radius);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Wide3 Vector(in WideOperands instruction) => new(X: instruction.Data0X, Y: instruction.Data0Y, Z: instruction.Data0Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Wide3 Vector1(in WideOperands instruction) => new(X: instruction.Data1X, Y: instruction.Data1Y, Z: instruction.Data1Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static WideRotation Rotation(in WideOperands instruction) => new(W: instruction.Data0W, X: instruction.Data0X, Y: instruction.Data0Y, Z: instruction.Data0Z);

    // === The wide carriers ==============================================================================================

    // An instruction's lanes as the wide formulas read them: each fixed-point lane as the double it is exactly.
    private readonly ref struct WideOperands(in CompiledInstruction compiled) {
        public readonly ref readonly CompiledInstruction Compiled = ref compiled;

        public SdfOp Op => Compiled.Op;
        public uint Shape => Compiled.Shape;
        public uint Blend => Compiled.Blend;
        public double Data0X => ((double)Compiled.Data0X);
        public double Data0Y => ((double)Compiled.Data0Y);
        public double Data0Z => ((double)Compiled.Data0Z);
        public double Data0W => ((double)Compiled.Data0W);
        public double Data1X => ((double)Compiled.Data1X);
        public double Data1Y => ((double)Compiled.Data1Y);
        public double Data1Z => ((double)Compiled.Data1Z);
        public double Data1W => ((double)Compiled.Data1W);

        // A scope candidate times the instruction's compiled scale, where it carries one.
        public Wide ScaleScope(Wide candidate) => ((Compiled.ScopeScale > 0L) ? (candidate * (Compiled.ScopeScale / ScopeScaleOne)) : candidate);
    }
    // One double a lane.
    private readonly struct Wide(Vector<double> lanes) {
        public readonly Vector<double> Lanes = lanes;

        public static Wide Zero => new(lanes: Vector<double>.Zero);
        public static Wide One => new(lanes: Vector<double>.One);

        public static Wide FromRawBits(long value) => new(lanes: new Vector<double>(value: (value / 65536.0)));
        public static implicit operator Wide(double value) => new(lanes: new Vector<double>(value: value));
        public static Wide operator +(Wide left, Wide right) => new(lanes: (left.Lanes + right.Lanes));
        public static Wide operator -(Wide left, Wide right) => new(lanes: (left.Lanes - right.Lanes));
        public static Wide operator -(Wide value) => new(lanes: -value.Lanes);
        public static Wide operator *(Wide left, Wide right) => new(lanes: (left.Lanes * right.Lanes));
        public static Wide operator /(Wide left, Wide right) => new(lanes: (left.Lanes / right.Lanes));
        public static WideMask operator <(Wide left, Wide right) => new(bits: System.Numerics.Vector.LessThan(left: left.Lanes, right: right.Lanes));
        public static WideMask operator >(Wide left, Wide right) => new(bits: System.Numerics.Vector.GreaterThan(left: left.Lanes, right: right.Lanes));
        public static WideMask operator <=(Wide left, Wide right) => new(bits: System.Numerics.Vector.LessThanOrEqual(left: left.Lanes, right: right.Lanes));
        public static WideMask operator >=(Wide left, Wide right) => new(bits: System.Numerics.Vector.GreaterThanOrEqual(left: left.Lanes, right: right.Lanes));
        public static WideMask operator ==(Wide left, Wide right) => new(bits: System.Numerics.Vector.Equals(left: left.Lanes, right: right.Lanes));
        public static WideMask operator !=(Wide left, Wide right) => new(bits: ~System.Numerics.Vector.Equals(left: left.Lanes, right: right.Lanes));
        public static Wide Abs(Wide value) => new(lanes: System.Numerics.Vector.Abs(value.Lanes));
        public static Wide Min(Wide x, Wide y) => new(lanes: System.Numerics.Vector.Min(left: x.Lanes, right: y.Lanes));
        public static Wide Max(Wide x, Wide y) => new(lanes: System.Numerics.Vector.Max(left: x.Lanes, right: y.Lanes));
        public static Wide Clamp(Wide value, Wide minimum, Wide maximum) => Min(x: Max(x: value, y: minimum), y: maximum);
        public static Wide Lerp(Wide from, Wide to, Wide amount) => (from + ((to - from) * amount));
        public static Wide Round(Wide value) => new(lanes: System.Numerics.Vector.Round(value.Lanes));
        public static Wide Floor(Wide value) => new(lanes: System.Numerics.Vector.Floor(value.Lanes));
        public static Wide Sqrt(Wide value) => new(lanes: System.Numerics.Vector.SquareRoot(value.Lanes));
        public static Wide Smoothstep(Wide edge0, Wide edge1, Wide value) {
            var t = Clamp(maximum: One, minimum: Zero, value: ((value - edge0) / (edge1 - edge0)));

            return ((t * t) * (((Wide)3.0) - (((Wide)2.0) * t)));
        }
        // x to the y for x at or above zero: zero where x is zero, as the fixed-point power answers.
        public static Wide Pow(Wide x, Wide y) => new(lanes: System.Numerics.Vector.Exp((y.Lanes * System.Numerics.Vector.Log(x.Lanes))));
        public static Wide Select(WideMask mask, Wide whenSet, Wide whenClear) => new(lanes: System.Numerics.Vector.ConditionalSelect(condition: mask.Bits, left: whenSet.Lanes, right: whenClear.Lanes));

        public override bool Equals(object? obj) => throw new NotSupportedException();
        public override int GetHashCode() => throw new NotSupportedException();
    }
    // The lanes a condition holds.
    private readonly struct WideMask(Vector<long> bits) {
        public readonly Vector<long> Bits = bits;

        public static WideMask None => new(bits: Vector<long>.Zero);

        public bool Any => !System.Numerics.Vector.EqualsAll(left: Bits, right: Vector<long>.Zero);
        public bool All => System.Numerics.Vector.EqualsAll(left: Bits, right: Vector<long>.AllBitsSet);

        public static WideMask operator &(WideMask left, WideMask right) => new(bits: (left.Bits & right.Bits));
        public static WideMask operator |(WideMask left, WideMask right) => new(bits: (left.Bits | right.Bits));

        // The lanes this holds that another does not.
        public WideMask Without(WideMask taken) => new(bits: System.Numerics.Vector.AndNot(left: Bits, right: taken.Bits));
    }
    private readonly struct Wide2(Wide X, Wide Y) {
        public readonly Wide X = X;
        public readonly Wide Y = Y;

        public Wide Length => Wide.Sqrt(value: Dot(left: this, right: this));

        public static Wide2 operator +(Wide2 left, Wide2 right) => new(X: (left.X + right.X), Y: (left.Y + right.Y));
        public static Wide2 operator -(Wide2 left, Wide2 right) => new(X: (left.X - right.X), Y: (left.Y - right.Y));
        public static Wide2 operator -(Wide2 value) => new(X: -value.X, Y: -value.Y);
        public static Wide2 operator *(Wide2 vector, Wide scalar) => new(X: (vector.X * scalar), Y: (vector.Y * scalar));
        public static Wide Dot(Wide2 left, Wide2 right) => ((left.X * right.X) + (left.Y * right.Y));
        // The point's place along a segment from the origin, held to the segment; a zero segment is its origin.
        public static Wide SegmentParameter(Wide2 point, Wide2 segment) {
            var length = Dot(left: segment, right: segment);

            return Wide.Select(mask: (length > Wide.Zero), whenClear: Wide.Zero, whenSet: Wide.Clamp(maximum: Wide.One, minimum: Wide.Zero, value: (Dot(left: point, right: segment) / length)));
        }
    }
    private readonly struct Wide3(Wide X, Wide Y, Wide Z) {
        public readonly Wide X = X;
        public readonly Wide Y = Y;
        public readonly Wide Z = Z;

        public Wide Length => Wide.Sqrt(value: Dot(left: this, right: this));

        public static Wide3 operator +(Wide3 left, Wide3 right) => new(X: (left.X + right.X), Y: (left.Y + right.Y), Z: (left.Z + right.Z));
        public static Wide3 operator -(Wide3 left, Wide3 right) => new(X: (left.X - right.X), Y: (left.Y - right.Y), Z: (left.Z - right.Z));
        public static Wide3 operator -(Wide3 value) => new(X: -value.X, Y: -value.Y, Z: -value.Z);
        public static Wide3 operator *(Wide3 vector, Wide scalar) => new(X: (vector.X * scalar), Y: (vector.Y * scalar), Z: (vector.Z * scalar));
        public static Wide3 Abs(Wide3 value) => new(X: Wide.Abs(value: value.X), Y: Wide.Abs(value: value.Y), Z: Wide.Abs(value: value.Z));
        public static Wide3 Round(Wide3 value) => new(X: Wide.Round(value: value.X), Y: Wide.Round(value: value.Y), Z: Wide.Round(value: value.Z));
        public static Wide3 Clamp(Wide3 value, Wide3 minimum, Wide3 maximum) => new(
            X: Wide.Clamp(maximum: maximum.X, minimum: minimum.X, value: value.X),
            Y: Wide.Clamp(maximum: maximum.Y, minimum: minimum.Y, value: value.Y),
            Z: Wide.Clamp(maximum: maximum.Z, minimum: minimum.Z, value: value.Z)
        );
        public static Wide3 Multiply(Wide3 left, Wide3 right) => new(X: (left.X * right.X), Y: (left.Y * right.Y), Z: (left.Z * right.Z));
        public static Wide3 Divide(Wide3 left, Wide3 right) => new(X: (left.X / right.X), Y: (left.Y / right.Y), Z: (left.Z / right.Z));
        public static Wide Dot(Wide3 left, Wide3 right) => (((left.X * right.X) + (left.Y * right.Y)) + (left.Z * right.Z));
        public static Wide SegmentParameter(Wide3 point, Wide3 segment) {
            var length = Dot(left: segment, right: segment);

            return Wide.Select(mask: (length > Wide.Zero), whenClear: Wide.Zero, whenSet: Wide.Clamp(maximum: Wide.One, minimum: Wide.Zero, value: (Dot(left: point, right: segment) / length)));
        }
    }
    // A unit quaternion every lane shares.
    private readonly struct WideRotation(double X, double Y, double Z, double W) {
        // The vector turned back through the rotation: v + 2w(u x v) + 2 u x (u x v), with u the conjugate's axis part.
        public Wide3 RotateInverse(Wide3 vector) {
            var (ux, uy, uz) = (((Wide)(-X)), ((Wide)(-Y)), ((Wide)(-Z)));
            var tx = (((Wide)2.0) * ((uy * vector.Z) - (uz * vector.Y)));
            var ty = (((Wide)2.0) * ((uz * vector.X) - (ux * vector.Z)));
            var tz = (((Wide)2.0) * ((ux * vector.Y) - (uy * vector.X)));

            return new Wide3(
                X: ((vector.X + (tx * W)) + ((uy * tz) - (uz * ty))),
                Y: ((vector.Y + (ty * W)) + ((uz * tx) - (ux * tz))),
                Z: ((vector.Z + (tz * W)) + ((ux * ty) - (uy * tx)))
            );
        }
    }
}
