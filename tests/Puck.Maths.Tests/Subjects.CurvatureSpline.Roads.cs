namespace Puck.Maths.Tests;

/// <summary>Subject closures for the road shapes a curvature spline must compile: straights at any heading, arcs that
/// end into straights with a curvature jump, and joining a compiled curve from a position.</summary>
internal static partial class Subjects {
    private const double RoadHalfX = 12.0;
    private const double RoadHalfZ = 8.0;
    private const double RoadRadius = 4.0;

    // The knots of a closed rounded rectangle about the origin, driven with positive curvature (turning toward +Z),
    // each corner three knots 45° apart about its centre. With jump, the corners are circular: the corner's end knots
    // declare the arc's curvature on the arc side and zero on the straight side. Without, they are transitions: zero
    // at both corner ends and 1/r at the middle, every knot continuous. Knots 3c..3c+2 are corner c, so segment s is a
    // straight exactly when s % 3 == 2.
    private static CurvatureSplineKnot[] CurvatureSplineRoundedRectangle(bool jump) {
        (double X, double Z)[] centres = [
            ((RoadHalfX - RoadRadius), (RoadRadius - RoadHalfZ)),
            ((RoadHalfX - RoadRadius), (RoadHalfZ - RoadRadius)),
            ((RoadRadius - RoadHalfX), (RoadHalfZ - RoadRadius)),
            ((RoadRadius - RoadHalfX), (RoadRadius - RoadHalfZ)),
        ];
        var curvature = (1.0 / RoadRadius);
        var knots = new CurvatureSplineKnot[12];

        for (var corner = 0; (corner < 4); ++corner) {
            for (var step = 0; (step < 3); ++step) {
                var angle = ((-Math.PI / 2.0) + (corner * (Math.PI / 2.0)) + (step * (Math.PI / 4.0)));
                var (leaving, arriving) = step switch {
                    0 => (jump ? (curvature, 0.0) : (0.0, (double?)null)),
                    1 => (curvature, null),
                    _ => (jump ? (0.0, curvature) : (0.0, null)),
                };

                knots[((corner * 3) + step)] = new CurvatureSplineKnot(
                    Curvature: CurvatureSplineD(value: leaving),
                    Elevation: FixedQ4816.Zero,
                    IncomingCurvature: ((arriving is { } value) ? CurvatureSplineD(value: value) : null),
                    TangentYaw: CurvatureSplineD(value: Math.IEEERemainder(x: (angle + (Math.PI / 2.0)), y: (2.0 * Math.PI))),
                    X: CurvatureSplineD(value: (centres[corner].X + (RoadRadius * Math.Cos(d: angle)))),
                    Z: CurvatureSplineD(value: (centres[corner].Z + (RoadRadius * Math.Sin(a: angle))))
                );
            }
        }

        return knots;
    }
    private static long CurvatureSplineMidStation(CurvatureSplineSegment segment) =>
        (segment.StationRaw + (segment.LengthRaw / 2));

    /// <summary>A zero-curvature segment whose tangents point along its chord within one yaw raw compiles to the
    /// exact line at any heading, and the tolerance is one raw and no wider.</summary>
    public static string? CurvatureSplineStraightWithinHeadingRounding() {
        string? ExpectStraight(string label, double x, double z, FixedQ4816 startYaw, FixedQ4816 endYaw) {
            CompiledCurvatureSpline compiled;

            try {
                compiled = CurvatureSpline.Compile(
                    closed: false,
                    knots: [
                        new CurvatureSplineKnot(X: FixedQ4816.Zero, Z: FixedQ4816.Zero, Elevation: FixedQ4816.Zero, TangentYaw: startYaw, Curvature: FixedQ4816.Zero),
                        new CurvatureSplineKnot(X: CurvatureSplineD(value: x), Z: CurvatureSplineD(value: z), Elevation: FixedQ4816.Zero, TangentYaw: endYaw, Curvature: FixedQ4816.Zero),
                    ]
                );
            } catch (CurvatureSplineException exception) {
                return $"{label}: the straight refused ({exception.Message})";
            }

            var segment = compiled.GetSegment(index: 0);
            var chordX = (double)CurvatureSplineD(value: x);
            var chordZ = (double)CurvatureSplineD(value: z);
            var third = (long)Math.Round(a: ((Math.Sqrt(d: ((chordX * chordX) + (chordZ * chordZ))) / 3.0) * 4294967296.0));

            if ((Math.Abs(value: (segment.Tangent0LengthRaw - third)) > 1L) || (Math.Abs(value: (segment.Tangent1LengthRaw - third)) > 1L)) {
                return $"{label}: tangent lengths {segment.Tangent0LengthRaw} and {segment.Tangent1LengthRaw}, a third of the chord is {third}";
            }
            for (var station = 0; (station <= 16); ++station) {
                var arcRaw = ((long)((((Int128)compiled.TotalLengthRaw) * station) / 16));

                if (compiled.EvaluateRaw(arcRaw: arcRaw).Curvature != FixedQ4816.Zero) {
                    return $"{label}: curvature at station {station}/16 is not exactly zero";
                }
            }

            return null;
        }

        var diagonalX = (double)CurvatureSplineD(value: 7.3);
        var diagonalZ = (double)CurvatureSplineD(value: 4.1);
        var bearing = CurvatureSplineD(value: Math.Atan2(y: diagonalZ, x: diagonalX));
        var failure =
            ExpectStraight(label: "heading 0", x: 10, z: 0, startYaw: FixedQ4816.Zero, endYaw: FixedQ4816.Zero) ??
            ExpectStraight(label: "heading pi/2", x: 0, z: 10, startYaw: CurvatureSplineD(value: (Math.PI / 2.0)), endYaw: CurvatureSplineD(value: (Math.PI / 2.0))) ??
            ExpectStraight(label: "heading pi", x: -10, z: 0, startYaw: CurvatureSplineD(value: Math.PI), endYaw: CurvatureSplineD(value: Math.PI)) ??
            ExpectStraight(label: "heading -pi/2", x: 0, z: -10, startYaw: CurvatureSplineD(value: (-Math.PI / 2.0)), endYaw: CurvatureSplineD(value: (-Math.PI / 2.0))) ??
            ExpectStraight(label: "diagonal at its knots' rounded bearing", x: 7.3, z: 4.1, startYaw: bearing, endYaw: bearing) ??
            ExpectStraight(label: "heading spelled pi then -pi", x: -10, z: 0, startYaw: CurvatureSplineD(value: Math.PI), endYaw: CurvatureSplineD(value: -Math.PI)) ??
            ExpectStraight(label: "yaw one raw off the chord", x: 10, z: 0, startYaw: FixedQ4816.FromRawBits(value: 1L), endYaw: FixedQ4816.FromRawBits(value: 1L));

        if (failure is not null) { return failure; }

        try {
            var twoRaws = FixedQ4816.FromRawBits(value: 2L);

            _ = CurvatureSpline.Compile(
                closed: false,
                knots: [
                    new CurvatureSplineKnot(X: FixedQ4816.Zero, Z: FixedQ4816.Zero, Elevation: FixedQ4816.Zero, TangentYaw: twoRaws, Curvature: FixedQ4816.Zero),
                    new CurvatureSplineKnot(X: CurvatureSplineD(value: 10), Z: FixedQ4816.Zero, Elevation: FixedQ4816.Zero, TangentYaw: twoRaws, Curvature: FixedQ4816.Zero),
                ]
            );
        } catch (CurvatureSplineException exception) {
            return ((exception.Refusal == CurvatureSplineRefusal.TangentCurvatureInconsistent)
                ? null
                : $"a yaw two raws off the chord refused {exception.Refusal}, not TangentCurvatureInconsistent");
        }

        return "a yaw two raws off the chord compiled as a straight; the tolerance is wider than one raw";
    }
    /// <summary>A rounded rectangle with circular corners compiles with its curvature jumping at each corner's ends,
    /// and the same rectangle with transition corners compiles too.</summary>
    public static string? CurvatureSplineCurvatureJump() {
        var authored = CurvatureSplineD(value: (1.0 / RoadRadius)).Value;
        var band = (authored >> 6);
        CompiledCurvatureSpline circular, transition;

        try {
            circular = CurvatureSpline.Compile(closed: true, knots: CurvatureSplineRoundedRectangle(jump: true));
            transition = CurvatureSpline.Compile(closed: true, knots: CurvatureSplineRoundedRectangle(jump: false));
        } catch (CurvatureSplineException exception) {
            return $"a rounded rectangle refused: {exception.Message}";
        }

        for (var index = 0; (index < circular.SegmentCount); ++index) {
            var segment = circular.GetSegment(index: index);
            var middle = circular.EvaluateRaw(arcRaw: CurvatureSplineMidStation(segment: segment)).Curvature.Value;

            if ((index % 3) != 2) {
                if (Math.Abs(value: (middle - authored)) > band) {
                    return $"circular corner segment {index}: curvature raw {middle} at its middle, authored {authored}, band {band}";
                }

                continue;
            }
            if (middle != 0L) {
                return $"circular straight segment {index}: curvature raw {middle} at its middle, not zero";
            }

            var arcSide = circular.EvaluateRaw(arcRaw: (segment.StationRaw - 1L)).Curvature.Value;
            var straightStart = circular.EvaluateRaw(arcRaw: segment.StationRaw).Curvature.Value;
            var nextStation = (segment.StationRaw + segment.LengthRaw);
            var straightEnd = circular.EvaluateRaw(arcRaw: (nextStation - 1L)).Curvature.Value;
            var nextArc = circular.EvaluateRaw(arcRaw: nextStation).Curvature.Value;

            if ((Math.Abs(value: (arcSide - authored)) > band) || (straightStart != 0L) || (straightEnd != 0L) || (Math.Abs(value: (nextArc - authored)) > band)) {
                return $"straight segment {index}: across its start knot {arcSide} then {straightStart}, across its end knot {straightEnd} then {nextArc}; the arc side should read {authored} within {band} and the straight side zero";
            }
        }
        for (var index = 2; (index < transition.SegmentCount); index += 3) {
            var middle = transition.EvaluateRaw(arcRaw: CurvatureSplineMidStation(segment: transition.GetSegment(index: index))).Curvature.Value;

            if (middle != 0L) {
                return $"transition straight segment {index}: curvature raw {middle} at its middle, not zero";
            }
        }

        return null;
    }
    /// <summary>A position on or beside a compiled curve finds its own station again, and positions past an open
    /// curve's ends find its ends.</summary>
    public static string? CurvatureSplineNearestStation() {
        const long Tolerance = (1L << 24); // 2^-8 units at the Q32 station scale.
        var loop = CurvatureSpline.Compile(closed: true, knots: CurvatureSplineRoundedRectangle(jump: true));
        var half = CurvatureSplineD(value: 0.5);

        for (var index = 0; (index < 64); ++index) {
            var station = ((long)((((Int128)loop.TotalLengthRaw) * index) / 64));
            var sample = loop.EvaluateRaw(arcRaw: station);
            var normal = new FixedVector3(X: -sample.Tangent.Z, Y: FixedQ4816.Zero, Z: sample.Tangent.X);

            foreach (var offset in (ReadOnlySpan<FixedQ4816>)[FixedQ4816.Zero, half, -half]) {
                var found = loop.NearestStationRaw(position: (sample.Position + (normal * offset)));
                var difference = Math.Abs(value: (found - station));

                difference = Math.Min(val1: difference, val2: (loop.TotalLengthRaw - difference));
                if (difference > Tolerance) {
                    return $"station {index}/64 offset {offset}: found {found}, expected {station} within {Tolerance}";
                }
            }
        }

        var straight = CurvatureSpline.Compile(
            closed: false,
            knots: [
                new CurvatureSplineKnot(X: FixedQ4816.Zero, Z: FixedQ4816.Zero, Elevation: FixedQ4816.Zero, TangentYaw: FixedQ4816.Zero, Curvature: FixedQ4816.Zero),
                new CurvatureSplineKnot(X: CurvatureSplineD(value: 10), Z: FixedQ4816.Zero, Elevation: FixedQ4816.Zero, TangentYaw: FixedQ4816.Zero, Curvature: FixedQ4816.Zero),
            ]
        );
        var before = straight.NearestStationRaw(position: new FixedVector3(X: CurvatureSplineD(value: -3), Y: FixedQ4816.Zero, Z: FixedQ4816.One));
        var beyond = straight.NearestStationRaw(position: new FixedVector3(X: CurvatureSplineD(value: 14), Y: FixedQ4816.Zero, Z: -FixedQ4816.One));

        return ((before != 0L) || (beyond != straight.TotalLengthRaw))
            ? $"past the open straight's ends: found {before} and {beyond}, expected 0 and {straight.TotalLengthRaw}"
            : null;
    }
}
