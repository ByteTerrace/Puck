using System.Numerics;

namespace Puck.Maths;

/// <summary>Names why a <see cref="CurvatureSpline.Compile"/> call refused a curve, in the order the compile checks
/// them.</summary>
public enum CurvatureSplineRefusal : byte {
    /// <summary>An open curve declared fewer than two knots, or a closed curve fewer than three.</summary>
    TooFewKnots,
    /// <summary>A knot's coordinate or elevation leaves <see cref="CurvatureSpline.MaxCoordinate"/>, or its
    /// curvature leaves <see cref="CurvatureSpline.MaxCurvature"/>.</summary>
    KnotOutOfRange,
    /// <summary>Two consecutive knots' planar chord is shorter than <see cref="CurvatureSpline.MinChordLength"/>.</summary>
    ZeroLengthChord,
    /// <summary>The tangent-length system's cross product <c>w = cross2(T0, T1)</c> is zero and the remaining
    /// linear equation on the affected side is inconsistent with the authored curvature.</summary>
    TangentCurvatureInconsistent,
    /// <summary>No tangent-length pair within the authoring bounds solves the curvature system.</summary>
    CurvatureUnreachable,
    /// <summary>The segment's speed <c>|B'(t)|</c> dips below <see cref="CurvatureSpline.MinSpeedFloor"/> somewhere
    /// on <c>[0, 1]</c>.</summary>
    InteriorCusp,
    /// <summary>The arc-length table's Richardson-estimated Simpson-quadrature or linear-interpolation error would
    /// not fall under its scaled bound (<see cref="CurvatureSpline.ArcLengthRelativeErrorShift"/>, floored at
    /// <see cref="CurvatureSpline.ArcLengthMinimumErrorBoundRaw"/>) within the panel-doubling budget.</summary>
    ArcLengthErrorUnbounded,
    /// <summary>A compiled raw's exact rational value does not fit the Q32 coefficient carrier.</summary>
    CarrierOverflow,
}

/// <summary>Reports a <see cref="CurvatureSpline.Compile"/>-time refusal.</summary>
public sealed class CurvatureSplineException : ArgumentException {
    internal CurvatureSplineException(CurvatureSplineRefusal refusal, int segmentIndex, string detail)
        : base(message: ((segmentIndex >= 0)
            ? $"Curvature spline refused {refusal} at segment {segmentIndex}: {detail}"
            : $"Curvature spline refused {refusal}: {detail}")) {
        Refusal = refusal;
        SegmentIndex = segmentIndex;
    }

    /// <summary>Gets the refusal category.</summary>
    public CurvatureSplineRefusal Refusal { get; }
    /// <summary>Gets the segment index the refusal names, or <c>-1</c> for a whole-curve refusal
    /// (<see cref="CurvatureSplineRefusal.TooFewKnots"/>).</summary>
    public int SegmentIndex { get; }
}

/// <summary>One authored knot of a curvature-first spline: a planar position and elevation, a tangent direction, and
/// the signed curvature the compiled segments on either side must reach there.</summary>
/// <param name="X">The planar X position.</param>
/// <param name="Z">The planar Z position.</param>
/// <param name="Elevation">The Y lift — outside the planar curvature and arc-length solve; carried through as a
/// linear grade over each segment's arc length.</param>
/// <param name="TangentYaw">The tangent direction, in radians; the unit tangent is <c>(cos, sin)</c> in the XZ
/// plane, the same convention <see cref="FixedQ4816.SinCos"/> uses.</param>
/// <param name="Curvature">The signed planar curvature at this knot, under the <c>cross2(a, b) = a.X·b.Z − a.Z·b.X</c>
/// convention: positive curvature turns from the tangent toward +Z faster than toward −Z.</param>
public readonly record struct CurvatureSplineKnot(
    FixedQ4816 X,
    FixedQ4816 Z,
    FixedQ4816 Elevation,
    FixedQ4816 TangentYaw,
    FixedQ4816 Curvature
);

/// <summary>One evaluated sample of a <see cref="CompiledCurvatureSpline"/>.</summary>
/// <param name="Position">The world position (X, Elevation, Z).</param>
/// <param name="Tangent">The unit planar tangent (Y is always zero).</param>
/// <param name="Grade">The elevation slope <c>dY/ds</c> at the sampled arc length.</param>
/// <param name="Curvature">The signed planar curvature at the sampled arc length, under
/// <see cref="CurvatureSplineKnot.Curvature"/>'s convention.</param>
public readonly record struct CurvatureSplineSample(FixedVector3 Position, FixedVector3 Tangent, FixedQ4816 Grade, FixedQ4816 Curvature);

/// <summary>One compiled cubic-Bézier segment: every raw at <see cref="CurvatureSpline.CoefficientFractionBitCount"/>
/// (Q32), rounded once from the exact solve — the seam a presentation float twin converts from, and the shape the
/// law suite reads back to check continuity and the deterministic branch pick.</summary>
public readonly record struct CurvatureSplineSegment {
    /// <summary>The shared start knot's planar control point, Q32.</summary>
    public required long P0X { get; init; }
    /// <inheritdoc cref="P0X"/>
    public required long P0Z { get; init; }
    /// <summary>The derived first interior control point, Q32.</summary>
    public required long P1X { get; init; }
    /// <inheritdoc cref="P1X"/>
    public required long P1Z { get; init; }
    /// <summary>The derived second interior control point, Q32.</summary>
    public required long P2X { get; init; }
    /// <inheritdoc cref="P2X"/>
    public required long P2Z { get; init; }
    /// <summary>The shared end knot's planar control point, Q32.</summary>
    public required long P3X { get; init; }
    /// <inheritdoc cref="P3X"/>
    public required long P3Z { get; init; }
    /// <summary>The quadratic derivative control point at <c>t = 0</c>, Q32.</summary>
    public required long D0X { get; init; }
    /// <inheritdoc cref="D0X"/>
    public required long D0Z { get; init; }
    /// <summary>The quadratic derivative control point at the midpoint, Q32.</summary>
    public required long D1X { get; init; }
    /// <inheritdoc cref="D1X"/>
    public required long D1Z { get; init; }
    /// <summary>The quadratic derivative control point at <c>t = 1</c>, Q32.</summary>
    public required long D2X { get; init; }
    /// <inheritdoc cref="D2X"/>
    public required long D2Z { get; init; }
    /// <summary>The linear second-derivative control point at <c>t = 0</c>, Q32.</summary>
    public required long E0X { get; init; }
    /// <inheritdoc cref="E0X"/>
    public required long E0Z { get; init; }
    /// <summary>The linear second-derivative control point at <c>t = 1</c>, Q32.</summary>
    public required long E1X { get; init; }
    /// <inheritdoc cref="E1X"/>
    public required long E1Z { get; init; }
    /// <summary>The derived start tangent length <c>l0</c>, Q32.</summary>
    public required long Tangent0LengthRaw { get; init; }
    /// <summary>The derived end tangent length <c>l1</c>, Q32.</summary>
    public required long Tangent1LengthRaw { get; init; }
    /// <summary>The global arc station this segment starts at, Q32.</summary>
    public required long StationRaw { get; init; }
    /// <summary>This segment's own arc length — equal to the last <see cref="ArcTable"/> entry, Q32.</summary>
    public required long LengthRaw { get; init; }
    /// <summary>The start knot's elevation, promoted exactly to Q32.</summary>
    public required long Y0Raw { get; init; }
    /// <summary>The end knot's elevation, promoted exactly to Q32.</summary>
    public required long Y1Raw { get; init; }
    /// <summary>The constant elevation grade <c>dY/ds</c> over this segment (linear in arc length), Q32.</summary>
    public required long GradeRaw { get; init; }
    /// <summary>The cumulative Simpson arc-length table over this segment's parameter <c>t</c> (<c>ArcTable[0] ==
    /// 0</c>, <c>ArcTable[^1] == LengthRaw</c>), each entry Q32 and strictly increasing. The panel count — always a
    /// power of two, starting at 64 — is derived per segment by <see cref="CurvatureSpline.Compile"/>, doubled until
    /// the table's own estimated error falls under its scaled bound
    /// (<see cref="CurvatureSpline.ArcLengthRelativeErrorShift"/>); it is not a fixed 64 for every segment.</summary>
    public required long[] ArcTable { get; init; }
}

/// <summary>
/// The curvature-first spline primitive: authors declare knot positions, tangent directions and signed endpoint
/// curvatures; <see cref="Compile"/> derives the tangent lengths that reproduce them exactly (Steven Wittens'
/// curvature-continuous cubic-Bézier construction, "Making Curvature Front and Center"), at Q32 precision, along with
/// a Simpson arc-length table. <see cref="CompiledCurvatureSpline.Evaluate"/> is the zero-allocation per-tick/per-frame
/// form; <see cref="Compile"/> allocates and runs exact <see cref="System.Numerics.BigInteger"/>/<see cref="Rational"/>
/// arithmetic, never on a per-tick path.
/// </summary>
public static class CurvatureSpline {
    /// <summary>The fraction bit count every compiled raw is carried at (<c>32</c>) — the
    /// <see cref="SecondOrderDynamics.CoefficientFractionBitCount"/> precedent.</summary>
    public const int CoefficientFractionBitCount = 32;
    /// <summary>The largest ratio a derived tangent length may take over the segment's planar chord length.</summary>
    public const long MaxTangentChordRatio = 4L;

    /// <summary>The largest magnitude a knot's X, Z, or elevation coordinate may take. Chosen so a Q32 raw
    /// (<c>2^20 · 2^32 = 2^52</c>) stays well inside the signed 64-bit carrier under de Casteljau's convex-hull
    /// boundedness, including the derivative control points' <c>3×</c> scaling.</summary>
    public static readonly FixedQ4816 MaxCoordinate = FixedQ4816.FromInteger(value: (1 << 20));
    /// <summary>The shortest planar chord a segment may declare between consecutive knots — keeps arc-table
    /// increments far above one Q32 raw unit and bounds the curvature solve's sensitivity to the authored
    /// positions.</summary>
    public static readonly FixedQ4816 MinChordLength = FixedQ4816.FromRawBits(value: 4096L); // 1/16
    /// <summary>The shortest tangent length <see cref="Compile"/> admits. Below it, a Q32 rounding of the derived
    /// control points perturbs the reconstructed endpoint curvature by more than half a Q16 unit, which would make
    /// the compiled joint curvature law's "exact at the compiled scale" claim false.</summary>
    public static readonly FixedQ4816 MinTangentLength = FixedQ4816.FromRawBits(value: 1024L); // 1/64
    /// <summary>The largest signed curvature magnitude a knot may declare — a minimum authored turn radius of
    /// <c>1/8</c> unit, and the bound that keeps the tangent-length solve's sensitivity to curvature comfortable
    /// alongside <see cref="MinTangentLength"/>.</summary>
    public static readonly FixedQ4816 MaxCurvature = FixedQ4816.FromRawBits(value: 524288L); // 8
    /// <summary>The smallest speed <c>|B'(t)|</c> a compiled segment may reach anywhere on <c>[0, 1]</c> — conditions
    /// the arc-length integrand and the runtime tangent normalization away from the origin.</summary>
    public static readonly FixedQ4816 MinSpeedFloor = FixedQ4816.FromRawBits(value: 1024L); // 1/64
    /// <summary>The relative error a segment's <see cref="CurvatureSplineSegment.ArcTable"/>'s Richardson-estimated
    /// Simpson-quadrature or linear-interpolation error must fall under before <see cref="Compile"/> accepts its
    /// panel count, expressed as a right shift applied to the segment's own (estimated) arc length — scale-aware,
    /// since a segment's length ranges from a few units up to <see cref="MaxCoordinate"/>'s own reach, and a fixed
    /// absolute bound tight enough to matter at the small end is unreachable within any sane panel budget at the
    /// large end (Simpson's own error term scales with the integrand's magnitude, not just the panel width).</summary>
    public const int ArcLengthRelativeErrorShift = 20;
    /// <summary>The floor <see cref="ArcLengthRelativeErrorShift"/>'s own scaled bound never drops below, Q32 raw —
    /// one Q16 unit, the coarsest scale any <see cref="FixedQ4816"/>-typed consumer of a compiled raw (a position, a
    /// station) could ever observe, so a very short segment is never asked for an unreachable absolute precision.
    /// <see cref="CurvatureSplineRefusal.ArcLengthErrorUnbounded"/> refuses a segment whose table cannot reach
    /// either bound within 65,536 panels.</summary>
    public const long ArcLengthMinimumErrorBoundRaw = (1L << 16);

    /// <summary>Compiles authored knots into a curvature-continuous spline.</summary>
    /// <param name="knots">The authored knots, in curve order.</param>
    /// <param name="closed">Whether the last knot connects back to the first.</param>
    /// <returns>The compiled spline.</returns>
    /// <exception cref="CurvatureSplineException">The knots do not compile — see <see cref="CurvatureSplineRefusal"/>.</exception>
    public static CompiledCurvatureSpline Compile(ReadOnlySpan<CurvatureSplineKnot> knots, bool closed) {
        var knotCount = knots.Length;

        if ((closed && (knotCount < 3)) || (!closed && (knotCount < 2))) {
            throw new CurvatureSplineException(refusal: CurvatureSplineRefusal.TooFewKnots, segmentIndex: -1, detail: $"a {(closed ? "closed" : "open")} curve needs at least {(closed ? 3 : 2)} knots; {knotCount} were declared.");
        }

        for (var i = 0; (i < knotCount); ++i) {
            ValidateKnotRange(knot: knots[i], knotIndex: i);
        }

        var segmentCount = (closed ? knotCount : (knotCount - 1));
        var segments = new CurvatureSplineSegment[segmentCount];
        var station = 0L;

        for (var segment = 0; (segment < segmentCount); ++segment) {
            var start = knots[segment];
            var end = knots[(closed ? ((segment + 1) % knotCount) : (segment + 1))];
            var compiled = CurvatureSplineExactMath.CompileSegment(start: start, end: end, segmentIndex: segment);

            compiled = (compiled with { StationRaw = station });

            var nextStation = unchecked(station + compiled.LengthRaw);

            if (nextStation < station) {
                throw new CurvatureSplineException(refusal: CurvatureSplineRefusal.CarrierOverflow, segmentIndex: segment, detail: "the cumulative arc-length station overflowed the Q32 raw carrier.");
            }

            station = nextStation;
            segments[segment] = compiled;
        }

        return new CompiledCurvatureSpline(segments: segments, closed: closed, totalLengthRaw: station);
    }

    private static void ValidateKnotRange(CurvatureSplineKnot knot, int knotIndex) {
        if (
            (knot.X.Value > MaxCoordinate.Value) || (knot.X.Value < -MaxCoordinate.Value) ||
            (knot.Z.Value > MaxCoordinate.Value) || (knot.Z.Value < -MaxCoordinate.Value) ||
            (knot.Elevation.Value > MaxCoordinate.Value) || (knot.Elevation.Value < -MaxCoordinate.Value)
        ) {
            throw new CurvatureSplineException(refusal: CurvatureSplineRefusal.KnotOutOfRange, segmentIndex: knotIndex, detail: $"a coordinate leaves ±{MaxCoordinate}.");
        }
        if ((knot.Curvature.Value > MaxCurvature.Value) || (knot.Curvature.Value < -MaxCurvature.Value)) {
            throw new CurvatureSplineException(refusal: CurvatureSplineRefusal.KnotOutOfRange, segmentIndex: knotIndex, detail: $"the curvature leaves ±{MaxCurvature}.");
        }
    }
}

/// <summary>The compiled, curvature-continuous form of an authored curve — zero-allocation, exception-free evaluation
/// over its whole arc-length domain.</summary>
public sealed class CompiledCurvatureSpline {
    private const int NarrowingShift = (CurvatureSpline.CoefficientFractionBitCount - FixedQ4816.FractionBitCount); // Q32 -> Q16

    private readonly CurvatureSplineSegment[] _segments;

    internal CompiledCurvatureSpline(CurvatureSplineSegment[] segments, bool closed, long totalLengthRaw) {
        _segments = segments;
        Closed = closed;
        TotalLengthRaw = totalLengthRaw;
        TotalLength = NarrowToQ16(raw: totalLengthRaw);
    }

    /// <summary>Gets a value indicating whether the last knot connects back to the first.</summary>
    public bool Closed { get; }
    /// <summary>Gets the number of compiled segments (knot count for a closed curve; knot count minus one for an
    /// open one).</summary>
    public int SegmentCount => _segments.Length;
    /// <summary>Gets the total arc length of the curve.</summary>
    public FixedQ4816 TotalLength { get; }
    /// <summary>Gets the total arc length, Q32 raw — the twin-sync and law seam.</summary>
    public long TotalLengthRaw { get; }

    /// <summary>Gets one compiled segment's raw data — the twin-sync and law seam.</summary>
    /// <param name="index">The segment index, from zero to <see cref="SegmentCount"/> minus one.</param>
    public CurvatureSplineSegment GetSegment(int index) => _segments[index];

    /// <summary>Gets the arc station of a knot.</summary>
    /// <param name="index">The knot index. Ranges to <see cref="SegmentCount"/> inclusive: for an open curve that is
    /// the last authored knot; for a closed curve it is the wraparound back to knot zero, at
    /// <see cref="TotalLength"/>.</param>
    public FixedQ4816 KnotStation(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: index, other: SegmentCount);

        return NarrowToQ16(raw: ((index == SegmentCount) ? TotalLengthRaw : _segments[index].StationRaw));
    }

    /// <summary>Evaluates the curve at an arc length expressed at the compiled solve's own Q32 station scale — the
    /// station <see cref="CurvatureSplineSegment.StationRaw"/>/<see cref="TotalLengthRaw"/> and every
    /// <see cref="CurvatureSplineSegment.ArcTable"/> entry already carry. This is the seam a per-tick raw accumulator
    /// (a curve-follow producer's own travelled arc, held at Q32 precisely so a sub-Q16 authored rate still
    /// accumulates instead of rounding to a standstill) must call directly: narrowing such an accumulator to
    /// <see cref="FixedQ4816"/> (Q16) BEFORE evaluating loses up to 65,535 Q32 raws of precision on every call, which
    /// silently shifts where an open curve's endpoint clamp engages and where a closed curve's modulus wrap lands
    /// whenever <see cref="TotalLengthRaw"/> is not itself a multiple of <c>2^16</c> — true of almost every compiled
    /// curve, since the Simpson arc-length integral has no reason to land on a Q16 boundary. <see cref="Evaluate"/>
    /// is the Q16-typed convenience overload for authored/UI-scale callers; it promotes exactly (no rounding) and
    /// delegates here, which owns the one wrap/clamp/lookup implementation both overloads share.</summary>
    /// <param name="arcRaw">The Q32 arc-length raw to sample. A closed curve wraps modulo <see cref="TotalLengthRaw"/>
    /// (the canonical non-negative residue); an open curve clamps to <c>[0, TotalLengthRaw]</c>. Total over every
    /// input — never throws, never returns a non-finite component.</param>
    /// <returns>The sampled position, tangent, elevation grade, and curvature.</returns>
    public CurvatureSplineSample EvaluateRaw(long arcRaw) {
        var local = WrapOrClamp(argument: ((Int128)arcRaw));

        return LookupAndSample(local: local);
    }

    /// <summary>Evaluates the curve at an arc length. Promotes <paramref name="arcLength"/> to the compiled solve's
    /// own Q32 station scale EXACTLY (a left shift; no rounding) and delegates to <see cref="EvaluateRaw"/>, which
    /// owns wrap/clamp/lookup. A caller already holding a Q32 raw — a curve-follow producer's per-tick accumulator —
    /// must call <see cref="EvaluateRaw"/> directly rather than narrow to <see cref="FixedQ4816"/> first; see
    /// <see cref="EvaluateRaw"/>'s own remarks for why that narrowing is lossy at the wrap/clamp boundary.</summary>
    /// <param name="arcLength">The arc length to sample. A closed curve wraps modulo <see cref="TotalLength"/> (the
    /// canonical non-negative residue); an open curve clamps to <c>[0, TotalLength]</c>. Total over every input —
    /// never throws, never returns a non-finite component.</param>
    /// <returns>The sampled position, tangent, elevation grade, and curvature.</returns>
    public CurvatureSplineSample Evaluate(FixedQ4816 arcLength) {
        var argument = (((Int128)arcLength.Value) << NarrowingShift); // exact Q16 -> Q32 promotion, no rounding.
        var bounded = WrapOrClamp(argument: argument); // pre-bounded in Int128 so the narrow to `long` below is safe.

        return EvaluateRaw(arcRaw: bounded);
    }

    // The one wrap ((closed)) / clamp ((open)) implementation both Evaluate and EvaluateRaw stand on, worked in
    // Int128 so a Q16 argument's exact promotion (up to 2^16 wider than a Q32 raw already close to the long carrier's
    // own extremes) cannot silently truncate before it is reduced inside [0, TotalLengthRaw]. The post-reduction
    // value is always safe to narrow: TotalLengthRaw itself is bounded far below long's range by CurvatureSpline's
    // own authoring caps (MaxCoordinate/MaxTangentChordRatio; see their remarks).
    private long WrapOrClamp(Int128 argument) {
        if (Closed) {
            var modulus = ((Int128)TotalLengthRaw);

            if (modulus <= Int128.Zero) { return 0L; }

            var wrapped = (argument % modulus);

            if (wrapped < Int128.Zero) { wrapped += modulus; }

            return ((long)wrapped);
        }

        return ((long)((argument < Int128.Zero) ? Int128.Zero : ((argument > TotalLengthRaw) ? ((Int128)TotalLengthRaw) : argument)));
    }

    private CurvatureSplineSample LookupAndSample(long local) {
        var segmentIndex = 0;

        for (var i = (_segments.Length - 1); (i >= 0); --i) {
            if (local >= _segments[i].StationRaw) { segmentIndex = i; break; }
        }

        var segment = _segments[segmentIndex];
        var withinSegment = (local - segment.StationRaw);
        var t = InvertArcTable(table: segment.ArcTable, withinSegment: withinSegment);

        return SampleAt(segment: segment, tRaw: t, withinSegmentRaw: withinSegment);
    }

    private static long InvertArcTable(long[] table, long withinSegment) {
        var lo = 0;
        var hi = (table.Length - 1);

        while (lo < hi) {
            var mid = ((lo + hi + 1) / 2);

            if (table[mid] <= withinSegment) { lo = mid; } else { hi = (mid - 1); }
        }

        if (lo >= (table.Length - 1)) { return (1L << 32); }

        var bracketLo = table[lo];
        var bracketHi = table[(lo + 1)];
        var span = (bracketHi - bracketLo);

        if (!FusedArithmetic.TryDivideMagnitudeRounded(
            denominatorMagnitude: ((UInt128)span),
            fractionBitCount: 32,
            numeratorMagnitude: ((UInt128)(withinSegment - bracketLo)),
            quotient: out var fraction
        ) || (fraction > (1UL << 32))) {
            fraction = 0UL; // unreachable given the compile-time monotonicity guarantee; a total, finite fallback.
        }

        // The table's panel count (table.Length - 1) is always a power of two — Compile only ever doubles it while
        // refining to its scaled error bound — so dividing the panel-local fraction by it is a shift, never a
        // general division, whatever panel count a given segment settled on.
        var panelShift = BitOperations.Log2(value: ((uint)(table.Length - 1)));
        var numerator = ((((long)lo) << 32) + ((long)fraction));
        var quotientPart = (numerator >> panelShift);
        var mask = ((1L << panelShift) - 1L);
        var remainder = (numerator & mask);
        var half = (1L << (panelShift - 1));

        if ((remainder > half) || ((remainder == half) && ((quotientPart & 1L) != 0L))) { ++quotientPart; }

        return quotientPart;
    }

    private CurvatureSplineSample SampleAt(CurvatureSplineSegment segment, long tRaw, long withinSegmentRaw) {
        var positionX = DeCasteljau(p0: segment.P0X, p1: segment.P1X, p2: segment.P2X, p3: segment.P3X, tRaw: tRaw);
        var positionZ = DeCasteljau(p0: segment.P0Z, p1: segment.P1Z, p2: segment.P2Z, p3: segment.P3Z, tRaw: tRaw);
        var velocityX = QuadraticAt(a0: segment.D0X, a1: segment.D1X, a2: segment.D2X, tRaw: tRaw);
        var velocityZ = QuadraticAt(a0: segment.D0Z, a1: segment.D1Z, a2: segment.D2Z, tRaw: tRaw);
        var accelerationX = LinearAt(a0: segment.E0X, a1: segment.E1X, tRaw: tRaw);
        var accelerationZ = LinearAt(a0: segment.E0Z, a1: segment.E1Z, tRaw: tRaw);

        var tangentX = NarrowToQ16(raw: velocityX);
        var tangentZ = NarrowToQ16(raw: velocityZ);
        var speed = FixedQ4816.Sqrt(value: ((tangentX * tangentX) + (tangentZ * tangentZ)));
        FixedQ4816 unitTangentX, unitTangentZ;

        if (speed.Value == 0L) {
            unitTangentX = FixedQ4816.Zero;
            unitTangentZ = FixedQ4816.Zero;
        } else {
            unitTangentX = (tangentX / speed);
            unitTangentZ = (tangentZ / speed);
        }

        var accelX = NarrowToQ16(raw: accelerationX);
        var accelZ = NarrowToQ16(raw: accelerationZ);
        var cross = ((tangentX * accelZ) - (tangentZ * accelX));
        var speedCubed = (speed * speed * speed);
        var curvature = ((speedCubed.Value == 0L) ? FixedQ4816.Zero : (cross / speedCubed));

        var yRaw = (segment.Y0Raw + FixedQ4816.RoundProduct(product: (((Int128)segment.GradeRaw) * withinSegmentRaw), fractionBitCount: 32));

        return new(
            Curvature: curvature,
            Grade: NarrowToQ16(raw: segment.GradeRaw),
            Position: new(X: NarrowToQ16(raw: positionX), Y: NarrowToQ16(raw: yRaw), Z: NarrowToQ16(raw: positionZ)),
            Tangent: new(X: unitTangentX, Y: FixedQ4816.Zero, Z: unitTangentZ)
        );
    }

    // Cubic de Casteljau at a Q32 fraction `tRaw` (0 = t0, 2^32 = t1) — one rounding per lerp, through the shared
    // FixedQ4816.RoundProduct kernel, so every narrowing in the evaluate path rounds the same way.
    private static long DeCasteljau(long p0, long p1, long p2, long p3, long tRaw) {
        var q0 = Lerp(a: p0, b: p1, tRaw: tRaw);
        var q1 = Lerp(a: p1, b: p2, tRaw: tRaw);
        var q2 = Lerp(a: p2, b: p3, tRaw: tRaw);
        var r0 = Lerp(a: q0, b: q1, tRaw: tRaw);
        var r1 = Lerp(a: q1, b: q2, tRaw: tRaw);

        return Lerp(a: r0, b: r1, tRaw: tRaw);
    }

    private static long QuadraticAt(long a0, long a1, long a2, long tRaw) {
        var q0 = Lerp(a: a0, b: a1, tRaw: tRaw);
        var q1 = Lerp(a: a1, b: a2, tRaw: tRaw);

        return Lerp(a: q0, b: q1, tRaw: tRaw);
    }

    private static long LinearAt(long a0, long a1, long tRaw) =>
        Lerp(a: a0, b: a1, tRaw: tRaw);

    private static long Lerp(long a, long b, long tRaw) =>
        (a + FixedQ4816.RoundProduct(product: (((Int128)(b - a)) * tRaw), fractionBitCount: 32));

    private static FixedQ4816 NarrowToQ16(long raw) =>
        FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(product: ((Int128)raw), fractionBitCount: NarrowingShift));
}
