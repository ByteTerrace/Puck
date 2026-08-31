using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Shared-nothing reference derivations for <see cref="CurvatureSpline"/>. Every kernel here reconstructs
/// the cubic-Bézier DEFINITION itself, from the compiled Q32 control points — never the subject's own s0/s1/w system,
/// quartic, or Sturm chain.</summary>
internal static partial class Oracles {
    private static double CurvatureSplineRaw32(long raw) =>
        (raw / 4294967296.0);

    /// <summary>Reconstructs a compiled segment's endpoint curvatures in <see cref="double"/>, by direct polynomial
    /// differentiation of the cubic-Bézier definition (<c>B'(0) = 3(P1-P0)</c>, <c>B''(0) = 6(P0-2P1+P2)</c>,
    /// <c>κ = cross2(B', B'')/|B'|³</c>) from the compiled control points, sharing no code with
    /// <see cref="CurvatureSplineExactMath"/>.</summary>
    public static (double K0, double K1) CurvatureSplineEndpointCurvatureDouble(CurvatureSplineSegment segment) {
        var p0X = CurvatureSplineRaw32(raw: segment.P0X); var p0Z = CurvatureSplineRaw32(raw: segment.P0Z);
        var p1X = CurvatureSplineRaw32(raw: segment.P1X); var p1Z = CurvatureSplineRaw32(raw: segment.P1Z);
        var p2X = CurvatureSplineRaw32(raw: segment.P2X); var p2Z = CurvatureSplineRaw32(raw: segment.P2Z);
        var p3X = CurvatureSplineRaw32(raw: segment.P3X); var p3Z = CurvatureSplineRaw32(raw: segment.P3Z);

        var d0X = (3 * (p1X - p0X)); var d0Z = (3 * (p1Z - p0Z));
        var e0X = (6 * ((p0X - (2 * p1X)) + p2X)); var e0Z = (6 * ((p0Z - (2 * p1Z)) + p2Z));
        var d1X = (3 * (p3X - p2X)); var d1Z = (3 * (p3Z - p2Z));
        var e1X = (6 * ((p1X - (2 * p2X)) + p3X)); var e1Z = (6 * ((p1Z - (2 * p2Z)) + p3Z));

        var speed0 = Math.Sqrt(d: ((d0X * d0X) + (d0Z * d0Z)));
        var k0 = (((d0X * e0Z) - (d0Z * e0X)) / (speed0 * speed0 * speed0));
        var speed1 = Math.Sqrt(d: ((d1X * d1X) + (d1Z * d1Z)));
        var k1 = (((d1X * e1Z) - (d1Z * e1X)) / (speed1 * speed1 * speed1));

        return (k0, k1);
    }

    /// <summary>Reconstructs a compiled segment's endpoint curvatures exactly, in <see cref="BigInteger"/> — the same
    /// definition as <see cref="CurvatureSplineEndpointCurvatureDouble"/>, without any intermediate rounding, narrowed
    /// to <see cref="double"/> only at the very last step (the returned tuple), which the caller compares within an
    /// envelope rather than bit-exactly.</summary>
    public static (double K0, double K1) CurvatureSplineEndpointCurvatureExact(CurvatureSplineSegment segment) {
        BigInteger p0X = segment.P0X, p0Z = segment.P0Z;
        BigInteger p1X = segment.P1X, p1Z = segment.P1Z;
        BigInteger p2X = segment.P2X, p2Z = segment.P2Z;
        BigInteger p3X = segment.P3X, p3Z = segment.P3Z;

        var d0X = (3 * (p1X - p0X)); var d0Z = (3 * (p1Z - p0Z));
        var e0X = (6 * ((p0X - (2 * p1X)) + p2X)); var e0Z = (6 * ((p0Z - (2 * p1Z)) + p2Z));
        var d1X = (3 * (p3X - p2X)); var d1Z = (3 * (p3Z - p2Z));
        var e1X = (6 * ((p1X - (2 * p2X)) + p3X)); var e1Z = (6 * ((p1Z - (2 * p2Z)) + p3Z));

        var cross0 = ((d0X * e0Z) - (d0Z * e0X));
        var speedSquared0 = ((d0X * d0X) + (d0Z * d0Z));
        var cross1 = ((d1X * e1Z) - (d1Z * e1X));
        var speedSquared1 = ((d1X * d1X) + (d1Z * d1Z));

        // Every operand above is a Q32 raw (real value · 2^32), so cross is Q64 and speedSquared^1.5 is Q96 — the
        // quotient needs the 2^32 = Q96/Q64 scale restored to read back as the real, unscaled curvature.
        const double scale = 4294967296.0; // 2^32

        var k0 = ((((double)cross0) / Math.Pow(x: ((double)speedSquared0), y: 1.5)) * scale);
        var k1 = ((((double)cross1) / Math.Pow(x: ((double)speedSquared1), y: 1.5)) * scale);

        return (k0, k1);
    }

    /// <summary>Sums chord lengths over a fine uniform-<c>t</c> subdivision of a compiled segment's cubic Bézier —
    /// independent of <see cref="CurvatureSplineExactMath"/>'s Simpson arc-length table, which integrates
    /// <c>|B'(t)|</c> rather than summing chords, over the exact pre-rounding control points rather than the
    /// compiled Q32 ones this oracle reads.</summary>
    public static double CurvatureSplineArcLengthByChordSubdivision(CurvatureSplineSegment segment, int subdivisions) {
        var p0X = CurvatureSplineRaw32(raw: segment.P0X); var p0Z = CurvatureSplineRaw32(raw: segment.P0Z);
        var p1X = CurvatureSplineRaw32(raw: segment.P1X); var p1Z = CurvatureSplineRaw32(raw: segment.P1Z);
        var p2X = CurvatureSplineRaw32(raw: segment.P2X); var p2Z = CurvatureSplineRaw32(raw: segment.P2Z);
        var p3X = CurvatureSplineRaw32(raw: segment.P3X); var p3Z = CurvatureSplineRaw32(raw: segment.P3Z);

        var length = 0.0;
        var previousX = p0X;
        var previousZ = p0Z;

        for (var i = 1; (i <= subdivisions); ++i) {
            var t = (((double)i) / subdivisions);
            var u = (1.0 - t);
            var x = ((u * u * u * p0X) + (3 * u * u * t * p1X) + (3 * u * t * t * p2X) + (t * t * t * p3X));
            var z = ((u * u * u * p0Z) + (3 * u * u * t * p1Z) + (3 * u * t * t * p2Z) + (t * t * t * p3Z));

            length += Math.Sqrt(d: (((x - previousX) * (x - previousX)) + ((z - previousZ) * (z - previousZ))));
            previousX = x;
            previousZ = z;
        }

        return length;
    }

    /// <summary>Independently locates the position at a requested within-segment arc length by walking a fine chord
    /// subdivision of the compiled Q32 control points and linearly interpolating the last sub-chord — a different
    /// METHOD from the subject's own composite-Simpson quadrature plus arc-table inversion, used to certify position
    /// vs. requested station rather than merely the total length <see cref="CurvatureSplineArcLengthByChordSubdivision"/>
    /// certifies.</summary>
    public static (double X, double Z) CurvatureSplinePositionAtStation(CurvatureSplineSegment segment, double targetArcLength, int subdivisions) {
        var p0X = CurvatureSplineRaw32(raw: segment.P0X); var p0Z = CurvatureSplineRaw32(raw: segment.P0Z);
        var p1X = CurvatureSplineRaw32(raw: segment.P1X); var p1Z = CurvatureSplineRaw32(raw: segment.P1Z);
        var p2X = CurvatureSplineRaw32(raw: segment.P2X); var p2Z = CurvatureSplineRaw32(raw: segment.P2Z);
        var p3X = CurvatureSplineRaw32(raw: segment.P3X); var p3Z = CurvatureSplineRaw32(raw: segment.P3Z);

        if (targetArcLength <= 0.0) {
            return (p0X, p0Z);
        }

        var cumulative = 0.0;
        var previousX = p0X;
        var previousZ = p0Z;

        for (var i = 1; (i <= subdivisions); ++i) {
            var t = (((double)i) / subdivisions);
            var u = (1.0 - t);
            var x = ((u * u * u * p0X) + (3 * u * u * t * p1X) + (3 * u * t * t * p2X) + (t * t * t * p3X));
            var z = ((u * u * u * p0Z) + (3 * u * u * t * p1Z) + (3 * u * t * t * p2Z) + (t * t * t * p3Z));
            var chord = Math.Sqrt(d: (((x - previousX) * (x - previousX)) + ((z - previousZ) * (z - previousZ))));
            var next = (cumulative + chord);

            if (next >= targetArcLength) {
                var fraction = ((chord > 0.0) ? ((targetArcLength - cumulative) / chord) : 0.0);

                return ((previousX + (fraction * (x - previousX))), (previousZ + (fraction * (z - previousZ))));
            }

            cumulative = next;
            previousX = x;
            previousZ = z;
        }

        return (previousX, previousZ); // the target sits past this subdivision's own (slightly under-measured) total; clamp to the end.
    }

    /// <summary>Finds every real root of the tangent-length quartic <c>(27/8)κ0²κ1·l0⁴ − (9/2)κ0κ1s0·l0² + w³·l0 +
    /// [(3/2)κ1s0² + s1w²] = 0</c> admissible within <c>[MinTangentLength, MaxTangentChordRatio·|C|]</c>, by a coarse
    /// sign-change scan followed by bisection — an independently written <see cref="double"/> root finder sharing no
    /// code with <see cref="CurvatureSplineExactMath"/>'s exact Sturm-sequence isolation, used only to CERTIFY a
    /// constructed multi-root case's root count and each candidate's <c>l0² + l1²</c>.</summary>
    public static List<(double L0, double L1)> CurvatureSplineAdmissibleTangentLengths(double s0, double s1, double w, double kappa0, double kappa1, double chordLength) {
        double Q(double l0) {
            var c0 = ((1.5 * kappa1 * s0 * s0) + (s1 * w * w));
            var c1 = (w * w * w);
            var c2 = -(4.5 * kappa0 * kappa1 * s0);
            var c4 = ((27.0 / 8.0) * kappa0 * kappa0 * kappa1);

            return ((((c4 * l0 * l0) + c2) * l0 * l0) + (c1 * l0) + c0);
        }

        var lo = ((double)CurvatureSpline.MinTangentLength.Value / 65536.0);
        var hi = (CurvatureSpline.MaxTangentChordRatio * chordLength);
        const int steps = 40000;
        var previous = Q(lo);
        var roots = new List<double>();

        for (var i = 1; (i <= steps); ++i) {
            var t = (lo + ((hi - lo) * i / steps));
            var current = Q(t);

            if ((previous == 0.0) || ((previous < 0.0) != (current < 0.0))) {
                var a = (lo + ((hi - lo) * (i - 1) / steps));
                var b = t;

                for (var bisect = 0; (bisect < 80); ++bisect) {
                    var mid = ((a + b) / 2.0);

                    if ((Q(a) < 0.0) == (Q(mid) < 0.0)) { a = mid; } else { b = mid; }
                }

                roots.Add((a + b) / 2.0);
            }

            previous = current;
        }

        var capSquared = (hi * hi);
        var admissible = new List<(double, double)>();

        foreach (var l0 in roots) {
            var l1 = ((s0 - (1.5 * kappa0 * l0 * l0)) / w);

            if ((l0 >= lo) && (l1 >= lo) && ((l0 * l0) <= capSquared) && ((l1 * l1) <= capSquared)) {
                admissible.Add((l0, l1));
            }
        }

        return admissible;
    }
}
