using System.Numerics;

using Puck.Maths;

namespace Puck.SdfVm.Views;

/// <summary>
/// The presentation-side twin of <see cref="CompiledCurvatureSpline"/>: the same curvature-continuous cubic-Bézier
/// construction, sampled at the render/frame seam rather than in fixed point. Carries no solver of its own — a
/// <see cref="CompiledCurvatureSpline"/>'s already-solved Q32 raws convert exactly ONCE, at construction, so this
/// twin cannot diverge from <see cref="Puck.Maths.CurvatureSpline"/> on the tangent-length branch pick or the
/// compile-time refusal ladder; only <see cref="Sample"/>'s own arithmetic can drift from
/// <see cref="CompiledCurvatureSpline.EvaluateRaw"/>'s fixed-point one. Every intermediate — the converted control
/// points, the arc-length table, the wrap/clamp/lookup/de-Casteljau chain — is carried in <see cref="double"/>;
/// <see cref="float"/> appears only at the two public seams: <see cref="TotalLength"/> and <see cref="Sample"/>'s
/// returned position/yaw. A legal curve can accumulate arc well past <c>2^24</c> units, where a <c>float</c> ULP
/// already exceeds a legal short segment — carrying the accumulation itself in <c>float</c> would collapse distinct
/// Q32 stations onto the same value and could pick the wrong segment; <c>double</c>'s 53-bit mantissa keeps station
/// identity intact across the whole authoring range this twin's fixed authority admits. Kept in sync with
/// <see cref="CompiledCurvatureSpline.EvaluateRaw"/>'s formulas by hand — the <see cref="SecondOrderFollower3"/>/
/// <see cref="Puck.Maths.SecondOrderDynamics"/> pairing precedent, exactly. Presentation only: never feeds
/// simulation state, never persisted, never hashed.
/// </summary>
public sealed class SdfCurvePath {
    // Every raw a CompiledCurvatureSpline carries is Q(CurvatureSpline.CoefficientFractionBitCount); this converts
    // one to double, once, at construction.
    private static readonly double RawScale = Math.ScaleB(n: CurvatureSpline.CoefficientFractionBitCount, x: 1.0);

    private readonly Segment[] m_segments;

    // One compiled segment's raws, converted to double once: the planar Bézier control points, the derivative
    // control points Sample's tangent reads directly (already the 3×/6× de Casteljau-ready form
    // CompiledCurvatureSpline.GetSegment carries — see CurvatureSplineSegment's own remarks), the segment's arc
    // station/length, its linear elevation grade, and its cumulative arc-length table.
    private readonly record struct Segment(
        double P0X, double P0Z, double P1X, double P1Z, double P2X, double P2Z, double P3X, double P3Z,
        double D0X, double D0Z, double D1X, double D1Z, double D2X, double D2Z,
        double Station, double Length, double Y0, double Grade, double[] ArcTable
    );

    /// <summary>Converts a compiled fixed-point spline to its presentation twin.</summary>
    /// <param name="compiled">The compiled spline to convert.</param>
    /// <exception cref="ArgumentNullException"><paramref name="compiled"/> is <see langword="null"/>.</exception>
    public SdfCurvePath(CompiledCurvatureSpline compiled) {
        ArgumentNullException.ThrowIfNull(argument: compiled);

        Closed = compiled.Closed;

        var totalLength = ToDouble(raw: compiled.TotalLengthRaw);

        TotalLength = ((float)totalLength);
        m_totalLength = totalLength;

        var segmentCount = compiled.SegmentCount;
        var segments = new Segment[segmentCount];

        for (var index = 0; (index < segmentCount); index++) {
            var segment = compiled.GetSegment(index: index);
            var arcTable = segment.ArcTable;
            var table = new double[arcTable.Length];

            for (var entry = 0; (entry < arcTable.Length); entry++) {
                table[entry] = ToDouble(raw: arcTable[entry]);
            }

            segments[index] = new Segment(
                ArcTable: table,
                D0X: ToDouble(raw: segment.D0X),
                D0Z: ToDouble(raw: segment.D0Z),
                D1X: ToDouble(raw: segment.D1X),
                D1Z: ToDouble(raw: segment.D1Z),
                D2X: ToDouble(raw: segment.D2X),
                D2Z: ToDouble(raw: segment.D2Z),
                Grade: ToDouble(raw: segment.GradeRaw),
                Length: ToDouble(raw: segment.LengthRaw),
                P0X: ToDouble(raw: segment.P0X),
                P0Z: ToDouble(raw: segment.P0Z),
                P1X: ToDouble(raw: segment.P1X),
                P1Z: ToDouble(raw: segment.P1Z),
                P2X: ToDouble(raw: segment.P2X),
                P2Z: ToDouble(raw: segment.P2Z),
                P3X: ToDouble(raw: segment.P3X),
                P3Z: ToDouble(raw: segment.P3Z),
                Station: ToDouble(raw: segment.StationRaw),
                Y0: ToDouble(raw: segment.Y0Raw)
            );
        }

        m_segments = segments;
    }

    // The double-precision total, retained alongside the public float-narrowed TotalLength so Sample's own
    // wrap/clamp arithmetic never re-narrows through the float seam.
    private readonly double m_totalLength;

    /// <summary>Gets a value indicating whether the last knot connects back to the first — <see cref="Sample"/> wraps
    /// modulo <see cref="TotalLength"/> when set, clamps to <c>[0, TotalLength]</c> otherwise.</summary>
    public bool Closed { get; }
    /// <summary>Gets the total arc length of the curve.</summary>
    public float TotalLength { get; }

    /// <summary>Samples the curve at an arc length.</summary>
    /// <param name="arcLength">The arc length to sample — wrapped (closed) or clamped (open) first; any finite input
    /// is accepted.</param>
    /// <returns>The sampled world position, and the tangent direction's yaw under the engine's own facing convention
    /// (<c>facing(yaw) = (-sin(yaw), -cos(yaw))</c> in world X/Z — the same convention
    /// <c>Puck.World.Server.WorldBody</c>'s <c>SnapYawToPlanarIntent</c> reads a commanded direction's yaw from):
    /// <c>yaw = atan2(-tangentX, -tangentZ)</c>, so a subject built from it via
    /// <c>Quaternion.CreateFromYawPitchRoll(yaw, 0, 0)</c> faces exactly along the sampled tangent.</returns>
    public (Vector3 Position, float TangentYaw) Sample(float arcLength) {
        var segments = m_segments;
        var totalLength = m_totalLength;
        var local = (Closed
            ? ((totalLength > 0.0) ? (arcLength - (totalLength * Math.Floor(d: (arcLength / totalLength)))) : 0.0)
            : arcLength);

        local = Math.Clamp(max: totalLength, min: 0.0, value: local);

        var segmentIndex = 0;

        for (var index = (segments.Length - 1); (index >= 0); --index) {
            if (local >= segments[index].Station) {
                segmentIndex = index;

                break;
            }
        }

        var segment = segments[segmentIndex];
        var withinSegment = (local - segment.Station);
        var t = InvertArcTable(
            table: segment.ArcTable,
            withinSegment: withinSegment
        );
        var positionX = DeCasteljau(p0: segment.P0X, p1: segment.P1X, p2: segment.P2X, p3: segment.P3X, t: t);
        var positionZ = DeCasteljau(p0: segment.P0Z, p1: segment.P1Z, p2: segment.P2Z, p3: segment.P3Z, t: t);
        var tangentX = QuadraticAt(a0: segment.D0X, a1: segment.D1X, a2: segment.D2X, t: t);
        var tangentZ = QuadraticAt(a0: segment.D0Z, a1: segment.D1Z, a2: segment.D2Z, t: t);
        var y = (segment.Y0 + (segment.Grade * withinSegment));

        return (
            Position: new Vector3(x: ((float)positionX), y: ((float)y), z: ((float)positionZ)),
            TangentYaw: ((float)Math.Atan2(x: -tangentZ, y: -tangentX))
        );
    }

    private static double ToDouble(long raw) => (raw / RawScale);
    // Binary-searches the cumulative table (its own panel count, adaptively derived per segment, not fixed) for the
    // bracket containing withinSegment, then linearly interpolates the fraction within it — the
    // CompiledCurvatureSpline.InvertArcTable precedent, in double.
    private static double InvertArcTable(double[] table, double withinSegment) {
        var lo = 0;
        var hi = (table.Length - 1);

        while (lo < hi) {
            var mid = (((lo + hi) + 1) / 2);

            if (table[mid] <= withinSegment) { lo = mid; } else { hi = (mid - 1); }
        }

        if (lo >= (table.Length - 1)) {
            return 1.0;
        }

        var bracketLo = table[lo];
        var bracketHi = table[(lo + 1)];
        var span = (bracketHi - bracketLo);
        var fraction = ((span > 0.0) ? Math.Clamp(max: 1.0, min: 0.0, value: ((withinSegment - bracketLo) / span)) : 0.0);

        return ((lo + fraction) / (table.Length - 1));
    }
    private static double DeCasteljau(double p0, double p1, double p2, double p3, double t) {
        var q0 = double.Lerp(amount: t, value1: p0, value2: p1);
        var q1 = double.Lerp(amount: t, value1: p1, value2: p2);
        var q2 = double.Lerp(amount: t, value1: p2, value2: p3);
        var r0 = double.Lerp(amount: t, value1: q0, value2: q1);
        var r1 = double.Lerp(amount: t, value1: q1, value2: q2);

        return double.Lerp(amount: t, value1: r0, value2: r1);
    }
    private static double QuadraticAt(double a0, double a1, double a2, double t) {
        var q0 = double.Lerp(amount: t, value1: a0, value2: a1);
        var q1 = double.Lerp(amount: t, value1: a1, value2: a2);

        return double.Lerp(amount: t, value1: q0, value2: q1);
    }
}
