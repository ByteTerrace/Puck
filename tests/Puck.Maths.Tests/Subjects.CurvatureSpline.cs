namespace Puck.Maths.Tests;

/// <summary>Subject closures binding <see cref="CurvatureSpline"/> and its compiled form to the law suite. Every
/// claim here works from an OWN hand-constructed basis (geometric configurations engineered to exercise a specific
/// branch or bound) rather than a swept <c>Domain</c>: the admissibility filter rejects almost every randomly drawn
/// knot quadruple, so a random sweep would spend nearly all its draws on refusals rather than compiled curves.</summary>
internal static partial class Subjects {
    private static FixedQ4816 CurvatureSplineD(double value) =>
        FixedQ4816.FromDouble(value: value);

    private static CurvatureSplineKnot CurvatureSplineKnotAt(double x, double z, double elevation, double tangentYaw, double curvature) => new(
        Curvature: CurvatureSplineD(value: curvature),
        Elevation: CurvatureSplineD(value: elevation),
        TangentYaw: CurvatureSplineD(value: tangentYaw),
        X: CurvatureSplineD(value: x),
        Z: CurvatureSplineD(value: z)
    );
    // A knot on a circle of the given radius, evenly spaced by turnRadians — a circle's own curvature (signed
    // 1/radius) and tangent direction (perpendicular to the radius) are exact by construction, the
    // Puck.SdfVm.Tests.SdfCurvePathTests.CircleKnot precedent, in double.
    private static CurvatureSplineKnot CurvatureSplineCircleKnotAt(double radius, double elevation, int knotIndex, double turnRadians, double signedCurvature) {
        var angle = (knotIndex * turnRadians);

        return CurvatureSplineKnotAt(
            x: (radius * Math.Cos(d: angle)),
            z: (radius * Math.Sin(a: angle)),
            elevation: elevation,
            tangentYaw: (angle + (Math.PI / 2.0)),
            curvature: signedCurvature
        );
    }

    // A representative, hand-verified admissible battery: a straight segment (both branches' w=0/s=0 canonical
    // completion), a symmetric quarter turn, the constructed multi-root configuration (§ deterministic-multi-root-pick
    // shares this same geometry), and a gentle asymmetric S.
    private static readonly (string Label, CurvatureSplineKnot Start, CurvatureSplineKnot End)[] EndpointCurvatureCases = [
        ("straight", CurvatureSplineKnotAt(0, 0, 0, 0, 0), CurvatureSplineKnotAt(4, 0, 0, 0, 0)),
        ("quarter-turn", CurvatureSplineKnotAt(0, 0, 0, 0, 0.5), CurvatureSplineKnotAt(2, 2, 0, Math.PI / 2, 0.5)),
        ("multi-root", CurvatureSplineKnotAt(0, 0, 0, -1.4, 1.0), CurvatureSplineKnotAt(4, 0, 0, 0.4, 0.25)),
        ("gentle-s", CurvatureSplineKnotAt(0, 0, 0, 0.2, 0.4), CurvatureSplineKnotAt(5, 1, 0, 0.1, -0.15)),
    ];

    /// <summary>Every compiled endpoint curvature agrees with its authored knot, checked against BOTH an
    /// independent <see cref="double"/> reconstruction and an independent <see cref="System.Numerics.BigInteger"/>
    /// exact reconstruction of the cubic-Bézier definition — neither of which forms the subject's own s0/s1/w system
    /// or quartic.</summary>
    public static string? CurvatureSplineEndpointCurvatureOracle() {
        const double envelope = 1e-4; // measured max observed error ~6e-6 across the battery below; frozen with margin.

        foreach (var (label, start, end) in EndpointCurvatureCases) {
            var compiled = CurvatureSpline.Compile(knots: [start, end], closed: false);
            var segment = compiled.GetSegment(index: 0);
            var authoredK0 = (((double)start.Curvature.Value) / 65536.0);
            var authoredK1 = (((double)end.Curvature.Value) / 65536.0);

            var (doubleK0, doubleK1) = Oracles.CurvatureSplineEndpointCurvatureDouble(segment: segment);

            if (Math.Abs(value: (doubleK0 - authoredK0)) > envelope) {
                return $"{label}: double oracle κ0={doubleK0} disagrees with authored {authoredK0}";
            }
            if (Math.Abs(value: (doubleK1 - authoredK1)) > envelope) {
                return $"{label}: double oracle κ1={doubleK1} disagrees with authored {authoredK1}";
            }

            var (exactK0, exactK1) = Oracles.CurvatureSplineEndpointCurvatureExact(segment: segment);

            if (Math.Abs(value: (exactK0 - authoredK0)) > envelope) {
                return $"{label}: exact BigInteger oracle κ0={exactK0} disagrees with authored {authoredK0}";
            }
            if (Math.Abs(value: (exactK1 - authoredK1)) > envelope) {
                return $"{label}: exact BigInteger oracle κ1={exactK1} disagrees with authored {authoredK1}";
            }
        }

        return null;
    }

    /// <summary>The curvature <see cref="CompiledCurvatureSpline.Evaluate"/> reads back: on a twelve-knot circle of
    /// radius eight it stays within 1/64 of the authored 1/8 at sixty-four stations round the loop and lands within
    /// two raw of the authored value at every knot station; on a straight segment it is exactly zero everywhere. The
    /// circle band is the cubic's own approximation of the circle between knots, not the evaluator's rounding.</summary>
    public static string? CurvatureSplineEvaluateCurvature() {
        const double radius = 8.0;
        const int knotCount = 12;
        var turn = ((2.0 * Math.PI) / knotCount);
        var knots = new CurvatureSplineKnot[knotCount];

        for (var i = 0; (i < knotCount); ++i) {
            knots[i] = CurvatureSplineCircleKnotAt(radius: radius, elevation: 0, knotIndex: i, turnRadians: turn, signedCurvature: (1.0 / radius));
        }

        var circle = CurvatureSpline.Compile(knots: knots, closed: true);
        var authored = knots[0].Curvature.Value;
        var band = (authored >> 6);

        for (var station = 0; (station < 64); ++station) {
            var arcRaw = ((long)((((Int128)circle.TotalLengthRaw) * station) / 64));
            var sample = circle.EvaluateRaw(arcRaw: arcRaw);

            if (Math.Abs(value: (sample.Curvature.Value - authored)) > band) {
                return $"circle station {station}/64: curvature raw {sample.Curvature.Value}, authored {authored}, band {band}";
            }
        }

        for (var segment = 0; (segment < circle.SegmentCount); ++segment) {
            var atKnot = circle.EvaluateRaw(arcRaw: circle.GetSegment(index: segment).StationRaw);

            if (Math.Abs(value: (atKnot.Curvature.Value - authored)) > 2L) {
                return $"circle knot {segment}: curvature raw {atKnot.Curvature.Value} at the knot station, authored {authored}";
            }
        }

        var line = CurvatureSpline.Compile(knots: [CurvatureSplineKnotAt(0, 0, 0, 0, 0), CurvatureSplineKnotAt(4, 0, 0, 0, 0)], closed: false);

        for (var station = 0; (station <= 16); ++station) {
            var arcRaw = ((long)((((Int128)line.TotalLengthRaw) * station) / 16));

            if (line.EvaluateRaw(arcRaw: arcRaw).Curvature != FixedQ4816.Zero) {
                return $"straight segment station {station}/16: curvature is not exactly zero";
            }
        }

        return null;
    }

    /// <summary>At every interior joint of a multi-knot curve, the curvature one Q32 raw before the joint station
    /// (the left segment's own evaluation, essentially at its t=1) and exactly at the joint station (the right
    /// segment's own evaluation at its t=0) agree after Q16 rounding — both read through the subject's own public
    /// <see cref="CompiledCurvatureSpline.EvaluateRaw"/>, at the true Q32 station scale, never re-derived.</summary>
    public static string? CurvatureSplineG2Joint() {
        CurvatureSplineKnot[] knots = [
            CurvatureSplineKnotAt(0, 0, 0, 0, 0.3),
            CurvatureSplineKnotAt(3, 1, 1, 0.4, -0.2),
            CurvatureSplineKnotAt(6, 0, 2, -0.3, 0.1),
        ];
        var compiled = CurvatureSpline.Compile(knots: knots, closed: false);

        for (var joint = 1; (joint < compiled.SegmentCount); ++joint) {
            var stationRaw = compiled.GetSegment(index: joint).StationRaw;
            var left = compiled.EvaluateRaw(arcRaw: (stationRaw - 1L));
            var right = compiled.EvaluateRaw(arcRaw: stationRaw);
            var diff = Math.Abs(value: (left.Curvature.Value - right.Curvature.Value));

            if (diff > 2L) {
                return $"joint {joint} at station raw {stationRaw}: left curvature {left.Curvature} vs right curvature {right.Curvature} (raw diff {diff})";
            }
        }

        return null;
    }

    /// <summary>Every compiled segment's arc table is strictly increasing (exact, on raws), knot stations are
    /// strictly increasing with the last agreeing with <see cref="CompiledCurvatureSpline.TotalLength"/>, and the
    /// compiled total length agrees with an independent double chord-subdivision flattening oracle within a
    /// measured-then-frozen relative tolerance.</summary>
    public static string? CurvatureSplineArcLengthTable() {
        const double relativeTolerance = 1e-6; // measured relative error ~1.8e-10 on the battery below.
        CurvatureSplineKnot[] openKnots = [
            CurvatureSplineKnotAt(0, 0, 0, 0.2, 0.4),
            CurvatureSplineKnotAt(5, 1, 0, 0.1, -0.15),
            CurvatureSplineKnotAt(9, -1, 0, -0.5, 0.2),
        ];
        var compiled = CurvatureSpline.Compile(knots: openKnots, closed: false);
        var runningStation = 0L;

        for (var segmentIndex = 0; (segmentIndex < compiled.SegmentCount); ++segmentIndex) {
            var segment = compiled.GetSegment(index: segmentIndex);

            if (segment.StationRaw != runningStation) {
                return $"segment {segmentIndex}: station {segment.StationRaw} does not chain from the running total {runningStation}";
            }

            for (var i = 1; (i < segment.ArcTable.Length); ++i) {
                if (segment.ArcTable[i] <= segment.ArcTable[(i - 1)]) {
                    return $"segment {segmentIndex}: arc table entry {i} ({segment.ArcTable[i]}) does not strictly exceed entry {(i - 1)} ({segment.ArcTable[(i - 1)]})";
                }
            }
            if (segment.ArcTable[^1] != segment.LengthRaw) {
                return $"segment {segmentIndex}: arc table's last entry {segment.ArcTable[^1]} does not equal LengthRaw {segment.LengthRaw}";
            }

            var oracleLength = Oracles.CurvatureSplineArcLengthByChordSubdivision(segment: segment, subdivisions: 20000);
            var compiledLength = (segment.LengthRaw / 4294967296.0);
            var relativeError = (Math.Abs(value: (oracleLength - compiledLength)) / oracleLength);

            if (relativeError > relativeTolerance) {
                return $"segment {segmentIndex}: chord-subdivision oracle length {oracleLength} vs compiled {compiledLength} (relative error {relativeError})";
            }

            runningStation = unchecked(runningStation + segment.LengthRaw);
        }

        for (var knotIndex = 1; (knotIndex <= compiled.SegmentCount); ++knotIndex) {
            if (compiled.KnotStation(index: knotIndex).Value <= compiled.KnotStation(index: (knotIndex - 1)).Value) {
                return $"knot station {knotIndex} does not strictly exceed knot station {(knotIndex - 1)}";
            }
        }
        if (compiled.KnotStation(index: compiled.SegmentCount) != compiled.TotalLength) {
            return $"the last knot station {compiled.KnotStation(index: compiled.SegmentCount)} does not equal TotalLength {compiled.TotalLength}";
        }

        return null;
    }

    /// <summary><see cref="CompiledCurvatureSpline.Evaluate"/> is total over its whole arc-length domain — never
    /// throws, every component finite — at both the arc-length extremes and well past them in either direction, for
    /// both an open and a closed curve, and agrees closely across a one-Q32-raw step at every interior knot station.</summary>
    public static string? CurvatureSplineEvaluateContinuityAndTotality() {
        CurvatureSplineKnot[] openKnots = [
            CurvatureSplineKnotAt(0, 0, 0, 0, 0.3),
            CurvatureSplineKnotAt(3, 1, 1, 0.4, -0.2),
            CurvatureSplineKnotAt(6, 0, 2, -0.3, 0.1),
        ];
        CurvatureSplineKnot[] closedKnots = [
            CurvatureSplineKnotAt(0, 0, 0, 0, 0.4),
            CurvatureSplineKnotAt(3, 3, 0, Math.PI / 2, 0.4),
            CurvatureSplineKnotAt(0, 6, 0, Math.PI, 0.4),
            CurvatureSplineKnotAt(-3, 3, 0, -Math.PI / 2, 0.4),
        ];

        foreach (var (label, compiled) in new[] {
            ("open", CurvatureSpline.Compile(knots: openKnots, closed: false)),
            ("closed", CurvatureSpline.Compile(knots: closedKnots, closed: true)),
        }) {
            if (compiled.Closed != (label == "closed")) {
                return $"{label} curve: Closed reported {compiled.Closed}.";
            }

            var totalRaw = compiled.TotalLength.Value;

            foreach (var probeRaw in new[] { -totalRaw, -(totalRaw / 4), 0L, (totalRaw / 2), totalRaw, (2 * totalRaw) }) {
                CurvatureSplineSample sample;

                try {
                    sample = compiled.Evaluate(arcLength: FixedQ4816.FromRawBits(value: probeRaw));
                } catch (Exception exception) {
                    return $"{label} curve: Evaluate({probeRaw}) threw {exception.GetType().Name}: {exception.Message}";
                }

                if (
                    (sample.Position.X.Value == long.MinValue) || (sample.Position.Y.Value == long.MinValue) || (sample.Position.Z.Value == long.MinValue) ||
                    (sample.Tangent.X.Value == long.MinValue) || (sample.Tangent.Z.Value == long.MinValue) ||
                    (sample.Grade.Value == long.MinValue) || (sample.Curvature.Value == long.MinValue)
                ) {
                    return $"{label} curve: Evaluate({probeRaw}) reported a MinValue component.";
                }
            }

            for (var knotIndex = 1; (knotIndex < compiled.SegmentCount); ++knotIndex) {
                var stationRaw = compiled.GetSegment(index: knotIndex).StationRaw;
                var before = compiled.EvaluateRaw(arcRaw: (stationRaw - 1L));
                var at = compiled.EvaluateRaw(arcRaw: stationRaw);
                var positionDiff = (
                    Math.Abs(value: (before.Position.X.Value - at.Position.X.Value)) +
                    Math.Abs(value: (before.Position.Y.Value - at.Position.Y.Value)) +
                    Math.Abs(value: (before.Position.Z.Value - at.Position.Z.Value))
                );

                if (positionDiff > 4L) {
                    return $"{label} curve: position jumps by {positionDiff} raw across a one-Q32-raw step at knot {knotIndex}'s station.";
                }
            }
        }

        return null;
    }

    /// <summary>Compiling the identical authored knots three times from scratch produces bit-identical compiled
    /// output — every control point, derivative point, tangent length, station and arc-table entry — proving the
    /// deterministic branch pick is a pure function of the authored knots (no iteration-order or ambient-state
    /// dependence).</summary>
    public static string? CurvatureSplineDeterministicRecompile() {
        CurvatureSplineKnot[] knots = [
            CurvatureSplineKnotAt(0, 0, 0, -1.4, 1.0),
            CurvatureSplineKnotAt(4, 0, 0, 0.4, 0.25),
        ];

        var first = CurvatureSpline.Compile(knots: knots, closed: false);
        var second = CurvatureSpline.Compile(knots: knots, closed: false);
        var third = CurvatureSpline.Compile(knots: knots, closed: false);

        for (var i = 0; (i < first.SegmentCount); ++i) {
            var a = first.GetSegment(index: i);
            var b = second.GetSegment(index: i);
            var c = third.GetSegment(index: i);

            if (!SegmentsBitIdentical(left: a, right: b) || !SegmentsBitIdentical(left: b, right: c)) {
                return $"segment {i} was not bit-identical across three from-scratch compiles of the same knots.";
            }
        }
        if ((first.TotalLengthRaw != second.TotalLengthRaw) || (second.TotalLengthRaw != third.TotalLengthRaw)) {
            return "TotalLengthRaw was not bit-identical across three from-scratch compiles of the same knots.";
        }

        return null;
    }

    private static bool SegmentsBitIdentical(CurvatureSplineSegment left, CurvatureSplineSegment right) =>
        (
            (left.P0X == right.P0X) && (left.P0Z == right.P0Z) &&
            (left.P1X == right.P1X) && (left.P1Z == right.P1Z) &&
            (left.P2X == right.P2X) && (left.P2Z == right.P2Z) &&
            (left.P3X == right.P3X) && (left.P3Z == right.P3Z) &&
            (left.D0X == right.D0X) && (left.D0Z == right.D0Z) &&
            (left.D1X == right.D1X) && (left.D1Z == right.D1Z) &&
            (left.D2X == right.D2X) && (left.D2Z == right.D2Z) &&
            (left.E0X == right.E0X) && (left.E0Z == right.E0Z) &&
            (left.E1X == right.E1X) && (left.E1Z == right.E1Z) &&
            (left.Tangent0LengthRaw == right.Tangent0LengthRaw) && (left.Tangent1LengthRaw == right.Tangent1LengthRaw) &&
            (left.StationRaw == right.StationRaw) && (left.LengthRaw == right.LengthRaw) &&
            (left.Y0Raw == right.Y0Raw) && (left.Y1Raw == right.Y1Raw) && (left.GradeRaw == right.GradeRaw) &&
            left.ArcTable.AsSpan().SequenceEqual(other: right.ArcTable)
        );

    /// <summary>A constructed segment whose tangent-length quartic has (certified by an independently written
    /// <see cref="double"/> root finder) at least two admissible roots: the subject picks the one minimizing
    /// <c>l0² + l1²</c>, not merely the first the isolation order happens to visit.</summary>
    public static string? CurvatureSplineDeterministicMultiRootPick() {
        var start = CurvatureSplineKnotAt(0, 0, 0, -1.4, 1.0);
        var end = CurvatureSplineKnotAt(4, 0, 0, 0.4, 0.25);
        var chordX = ((double)(end.X.Value - start.X.Value) / 65536.0);
        var chordZ = ((double)(end.Z.Value - start.Z.Value) / 65536.0);
        var t0X = Math.Cos(-1.4); var t0Z = Math.Sin(-1.4);
        var t1X = Math.Cos(0.4); var t1Z = Math.Sin(0.4);
        var s0 = ((t0X * chordZ) - (t0Z * chordX));
        var s1 = ((t1X * chordZ) - (t1Z * chordX));
        var w = ((t0X * t1Z) - (t0Z * t1X));
        var chordLength = Math.Sqrt(d: ((chordX * chordX) + (chordZ * chordZ)));

        var admissible = Oracles.CurvatureSplineAdmissibleTangentLengths(s0: s0, s1: s1, w: w, kappa0: 1.0, kappa1: 0.25, chordLength: chordLength);

        if (admissible.Count < 2) {
            return $"the constructed configuration's own certification found only {admissible.Count} admissible root(s); it no longer exercises multiple roots.";
        }

        var bestByOracle = admissible.MinBy(keySelector: pair => ((pair.L0 * pair.L0) + (pair.L1 * pair.L1)));
        var compiled = CurvatureSpline.Compile(knots: [start, end], closed: false);
        var segment = compiled.GetSegment(index: 0);
        var subjectL0 = (segment.Tangent0LengthRaw / 4294967296.0);
        var subjectL1 = (segment.Tangent1LengthRaw / 4294967296.0);

        if ((Math.Abs(value: (subjectL0 - bestByOracle.L0)) > 1e-3) || (Math.Abs(value: (subjectL1 - bestByOracle.L1)) > 1e-3)) {
            return $"the subject picked (l0={subjectL0}, l1={subjectL1}) but the oracle's minimum-F admissible root is (l0={bestByOracle.L0}, l1={bestByOracle.L1}) among {admissible.Count} candidates.";
        }

        return null;
    }

    /// <summary>Every named refusal in <see cref="CurvatureSplineRefusal"/> is reachable, and reached by name — a
    /// wrong-reason refusal fails the case.</summary>
    public static string? CurvatureSplineRefusalLadder() {
        string? Expect(CurvatureSplineKnot[] knots, bool closed, CurvatureSplineRefusal expected, string label) {
            try {
                _ = CurvatureSpline.Compile(knots: knots, closed: closed);
            } catch (CurvatureSplineException exception) {
                if (exception.Refusal != expected) {
                    return $"{label}: expected {expected} but got {exception.Refusal} ({exception.Message})";
                }

                var wantsWholeCurveIndex = (expected == CurvatureSplineRefusal.TooFewKnots);

                return (((exception.SegmentIndex < 0) == wantsWholeCurveIndex)
                    ? null
                    : $"{label}: {expected} carried segment index {exception.SegmentIndex}, which does not match a {(wantsWholeCurveIndex ? "whole-curve" : "per-segment")} refusal.");
            }

            return $"{label}: expected {expected} but the curve compiled.";
        }

        // Each geometry below was confirmed against the live implementation (not hand-derived) to land on exactly the
        // named refusal, INCLUDING the exact-parallel-tangent case: TangentYaw is Q16-rounded before compile, so a
        // pair authored at yaw 0 and yaw π is only EXACTLY opposite when the rounded raws land there — 0 and π
        // (Q16-rounded) do, which is why "coordinate past MaxCoordinate" below reuses that same exact-zero-yaw
        // pairing for its own w = 0 branch rather than an arbitrary one. CarrierOverflow has no case here: every
        // knot pair inside KnotOutOfRange's own bound compiles or refuses by an earlier, more specific name first —
        // the derived §1.6 bounds make the Q32 carrier's own overflow unreachable through legal authored knots,
        // which is the intended safety margin, not a gap in this ladder.
        var detail =
            Expect(knots: [CurvatureSplineKnotAt(0, 0, 0, 0, 0)], closed: false, expected: CurvatureSplineRefusal.TooFewKnots, label: "open, one knot")
            ?? Expect(knots: [CurvatureSplineKnotAt(0, 0, 0, 0, 0), CurvatureSplineKnotAt(4, 0, 0, 0, 0)], closed: true, expected: CurvatureSplineRefusal.TooFewKnots, label: "closed, two knots")
            ?? Expect(knots: [CurvatureSplineKnotAt(0, 0, 0, 0, 0), CurvatureSplineKnotAt(0, 0, 0, 0, 0)], closed: false, expected: CurvatureSplineRefusal.ZeroLengthChord, label: "coincident knots")
            ?? Expect(knots: [CurvatureSplineKnotAt(0, 0, 0, 0, 8), CurvatureSplineKnotAt((1.0 / 16.0), 0, 0, 0, 8)], closed: false, expected: CurvatureSplineRefusal.TangentCurvatureInconsistent, label: "parallel tangents along the chord, w = 0 and s0 = 0")
            ?? Expect(knots: [CurvatureSplineKnotAt(0, 0, 0, 0, 0.2), CurvatureSplineKnotAt(4, 0, 0, Math.PI, 0.2)], closed: false, expected: CurvatureSplineRefusal.CurvatureUnreachable, label: "near-opposite tangents, general branch admits no admissible root")
            ?? Expect(knots: [CurvatureSplineKnotAt(0, 0, 0, 0, 0), CurvatureSplineKnotAt(2_000_000, 0, 0, 0, 0)], closed: false, expected: CurvatureSplineRefusal.KnotOutOfRange, label: "coordinate past MaxCoordinate")
            ?? Expect(knots: [CurvatureSplineKnotAt(0, 0, 0, -2.7407, 1.1065), CurvatureSplineKnotAt(1.0357, 0, 0, -1.1520, -4.9124)], closed: false, expected: CurvatureSplineRefusal.InteriorCusp, label: "admissible tangent lengths whose speed dips below the floor mid-segment");

        return detail;
    }

    /// <summary>Knots placed at exactly <see cref="CurvatureSpline.MaxCoordinate"/> with admissible geometry compile,
    /// and <see cref="CompiledCurvatureSpline.Evaluate"/> stays finite across the whole arc; one raw past the cap
    /// refuses <see cref="CurvatureSplineRefusal.KnotOutOfRange"/>.</summary>
    public static string? CurvatureSplineCarrierExtremes() {
        var max = ((double)CurvatureSpline.MaxCoordinate.Value / 65536.0);
        CurvatureSplineKnot[] knots = [
            CurvatureSplineKnotAt(-max, 0, 0, 0, 0),
            CurvatureSplineKnotAt(max, 0, 0, 0, 0),
        ];

        CompiledCurvatureSpline compiled;

        try {
            compiled = CurvatureSpline.Compile(knots: knots, closed: false);
        } catch (Exception exception) {
            return $"knots at ±MaxCoordinate with straight, zero-curvature geometry did not compile: {exception.GetType().Name}: {exception.Message}";
        }

        for (var i = 0; (i <= 8); ++i) {
            var arc = FixedQ4816.FromRawBits(value: ((compiled.TotalLength.Value * i) / 8));
            var sample = compiled.Evaluate(arcLength: arc);

            if ((sample.Position.X.Value == long.MinValue) || (sample.Position.Z.Value == long.MinValue)) {
                return $"Evaluate at fraction {i}/8 of a ±MaxCoordinate curve reported a MinValue component.";
            }
        }

        var overCap = CurvatureSplineKnotAt((max + 1), 0, 0, 0, 0);

        try {
            _ = CurvatureSpline.Compile(knots: [knots[0], overCap], closed: false);
        } catch (CurvatureSplineException exception) {
            return ((exception.Refusal == CurvatureSplineRefusal.KnotOutOfRange)
                ? null
                : $"one raw past MaxCoordinate: expected KnotOutOfRange but got {exception.Refusal}");
        }

        return "one raw past MaxCoordinate: expected KnotOutOfRange but the curve compiled.";
    }

    private static bool CurvatureSplineSamplesBitIdentical(CurvatureSplineSample left, CurvatureSplineSample right) =>
        (
            (left.Position.X.Value == right.Position.X.Value) && (left.Position.Y.Value == right.Position.Y.Value) && (left.Position.Z.Value == right.Position.Z.Value) &&
            (left.Tangent.X.Value == right.Tangent.X.Value) && (left.Tangent.Z.Value == right.Tangent.Z.Value) &&
            (left.Grade.Value == right.Grade.Value) && (left.Curvature.Value == right.Curvature.Value)
        );

    // Narrows a Q32 raw to Q16 through the SAME shared exact rounding kernel CompiledCurvatureSpline itself narrows
    // through (FixedPointRounding.TryRoundRational, ties to even) — legitimate here because the claim under test is
    // about which SEGMENT/T region a raw station resolves into (the wrap/clamp seam), not the rounding kernel itself,
    // which curvature-spline.endpoint-curvature-oracle and curvature-spline.arc-length-table already pin
    // independently.
    private static long CurvatureSplineNarrowQ32ToQ16(long raw32) {
        _ = FixedPointRounding.TryRoundRational(numerator: raw32, denominator: (1L << 16), fractionBitCount: 0, result: out var narrowed);

        return narrowed;
    }

    /// <summary><see cref="CompiledCurvatureSpline.EvaluateRaw"/> wraps (closed) or clamps (open) EXACTLY at the true
    /// Q32 station boundaries, on curves engineered so <see cref="CompiledCurvatureSpline.TotalLengthRaw"/>'s low 16
    /// bits are nonzero — the regime in which the retired Q16-only evaluation entry silently missed these same
    /// boundaries by up to 65,535 Q32 raws (narrowing <c>TotalLengthRaw</c> to Q16 before evaluating does not
    /// reproduce raw <c>TotalLengthRaw</c> itself).</summary>
    public static string? CurvatureSplineEvaluateRawStationBoundaries() {
        CurvatureSplineKnot[] openKnots = [
            CurvatureSplineKnotAt(0, 0, 0, 0.2, 0.4),
            CurvatureSplineKnotAt(5, 1, 0, 0.1, -0.15),
            CurvatureSplineKnotAt(9, -1, 0, -0.5, 0.2),
        ];
        CurvatureSplineKnot[] closedKnots = [
            CurvatureSplineKnotAt(0, 0, 0, 0, 0.4),
            CurvatureSplineKnotAt(3, 3, 0, Math.PI / 2, 0.4),
            CurvatureSplineKnotAt(0, 6, 0, Math.PI, 0.4),
            CurvatureSplineKnotAt(-3, 3, 0, -Math.PI / 2, 0.4),
        ];

        foreach (var (label, knots, closed) in new (string, CurvatureSplineKnot[], bool)[] {
            ("open", openKnots, false),
            ("closed", closedKnots, true),
        }) {
            var compiled = CurvatureSpline.Compile(knots: knots, closed: closed);
            var totalRaw = compiled.TotalLengthRaw;

            if ((totalRaw & 0xFFFFL) == 0L) {
                return $"{label} curve: TotalLengthRaw {totalRaw} has zero low-16 bits by construction — re-engineer its knots so this battery still exercises the Q16/Q32 seam rather than passing vacuously.";
            }

            var firstSegment = compiled.GetSegment(index: 0);
            var lastSegment = compiled.GetSegment(index: (compiled.SegmentCount - 1));

            var atZero = compiled.EvaluateRaw(arcRaw: 0L);
            var expectedStartX = CurvatureSplineNarrowQ32ToQ16(raw32: firstSegment.P0X);
            var expectedStartZ = CurvatureSplineNarrowQ32ToQ16(raw32: firstSegment.P0Z);

            if ((atZero.Position.X.Value != expectedStartX) || (atZero.Position.Z.Value != expectedStartZ)) {
                return $"{label} curve: EvaluateRaw(0) reported ({atZero.Position.X}, {atZero.Position.Z}) but segment 0's own P0 narrows to ({FixedQ4816.FromRawBits(value: expectedStartX)}, {FixedQ4816.FromRawBits(value: expectedStartZ)}).";
            }

            var atL = compiled.EvaluateRaw(arcRaw: totalRaw);
            var atLPlusOne = compiled.EvaluateRaw(arcRaw: (totalRaw + 1L));
            var atLMinusOne = compiled.EvaluateRaw(arcRaw: (totalRaw - 1L));

            if (closed) {
                if (!CurvatureSplineSamplesBitIdentical(left: atL, right: atZero)) {
                    return $"{label} curve: EvaluateRaw(TotalLengthRaw) did not reproduce EvaluateRaw(0) bit-for-bit — the modulus wrap missed the exact raw boundary.";
                }

                var atOne = compiled.EvaluateRaw(arcRaw: 1L);

                if (!CurvatureSplineSamplesBitIdentical(left: atLPlusOne, right: atOne)) {
                    return $"{label} curve: EvaluateRaw(TotalLengthRaw + 1) did not reproduce EvaluateRaw(1) bit-for-bit.";
                }

                // The ±1-raw checks above pin the exact boundary, but a raw one Q32 unit past a shared seam knot is
                // indistinguishable at Q16 resolution from the seam itself REGARDLESS of whether the wrap actually
                // ran (both land essentially on the shared corner point) — so they cannot by themselves catch a
                // wrap that clamped instead of wrapping. This offset is large enough (a quarter of the total length,
                // capped) to land in a DIFFERENT segment than the seam, where a clamp-instead-of-wrap bug would
                // report the curve's own end rather than the true wrapped point.
                var farOffset = Math.Min(val1: (totalRaw / 4), val2: (1L << 40));
                var atFarPastEnd = compiled.EvaluateRaw(arcRaw: (totalRaw + farOffset));
                var atFarFromStart = compiled.EvaluateRaw(arcRaw: farOffset);

                if (!CurvatureSplineSamplesBitIdentical(left: atFarPastEnd, right: atFarFromStart)) {
                    return $"{label} curve: EvaluateRaw(TotalLengthRaw + {farOffset}) did not reproduce EvaluateRaw({farOffset}) bit-for-bit — the modulus wrap did not actually wrap.";
                }

                // EvaluateRaw(TotalLengthRaw - 1) may legally share atZero's Q16-narrowed position (one Q32 raw is
                // far below a Q16 grid step) — that is not itself evidence of a premature wrap, which the bit-exact
                // checks above (EvaluateRaw(TotalLengthRaw) == EvaluateRaw(0), EvaluateRaw(TotalLengthRaw + 1) ==
                // EvaluateRaw(1)) already certify happens at the true raw boundary, not one Q16 unit early. Only
                // totality is owed here.
                if ((atLMinusOne.Position.X.Value == long.MinValue) || (atLMinusOne.Position.Z.Value == long.MinValue)) {
                    return $"{label} curve: EvaluateRaw(TotalLengthRaw - 1) reported a MinValue component.";
                }
            } else {
                var expectedEndX = CurvatureSplineNarrowQ32ToQ16(raw32: lastSegment.P3X);
                var expectedEndZ = CurvatureSplineNarrowQ32ToQ16(raw32: lastSegment.P3Z);

                if ((atL.Position.X.Value != expectedEndX) || (atL.Position.Z.Value != expectedEndZ)) {
                    return $"{label} curve: EvaluateRaw(TotalLengthRaw) reported ({atL.Position.X}, {atL.Position.Z}) but the last segment's own P3 narrows to ({FixedQ4816.FromRawBits(value: expectedEndX)}, {FixedQ4816.FromRawBits(value: expectedEndZ)}).";
                }
                if (!CurvatureSplineSamplesBitIdentical(left: atLPlusOne, right: atL)) {
                    return $"{label} curve: EvaluateRaw(TotalLengthRaw + 1) did not stay clamped to EvaluateRaw(TotalLengthRaw) bit-for-bit.";
                }
                // EvaluateRaw(TotalLengthRaw - 1) may share atL's Q16-narrowed position (one Q32 raw is far below a
                // Q16 grid step), so only totality — never throwing, never a MinValue-sentinel component — is owed.
                if ((atLMinusOne.Position.X.Value == long.MinValue) || (atLMinusOne.Position.Z.Value == long.MinValue)) {
                    return $"{label} curve: EvaluateRaw(TotalLengthRaw - 1) reported a MinValue component.";
                }
            }
        }

        return null;
    }

    /// <summary>Position vs. requested station, on a well-conditioned small curve AND an adversarial large-scale one
    /// (four knots on a circle at 0.9·<see cref="CurvatureSpline.MaxCoordinate"/>'s own radius, so both the compiled
    /// coordinates and the arc table's Simpson/inverse-lookup arithmetic run near the extreme end of the authoring
    /// range): the subject's own <see cref="CompiledCurvatureSpline.EvaluateRaw"/> position agrees with
    /// <see cref="Oracles.CurvatureSplinePositionAtStation"/> — a fine chord-walking inverse lookup sharing no code
    /// with the subject's Simpson quadrature plus arc-table inversion — at every eighth of each curve's own
    /// length.</summary>
    public static string? CurvatureSplineArcStationOracle() {
        const double relativeTolerance = 1e-4; // margin over the chord walk's own discretization at 100000 steps.
        CurvatureSplineKnot[] smallKnots = [
            CurvatureSplineKnotAt(0, 0, 0, 0.2, 0.4),
            CurvatureSplineKnotAt(5, 1, 0, 0.1, -0.15),
            CurvatureSplineKnotAt(9, -1, 0, -0.5, 0.2),
        ];
        var maxCoordinate = ((double)CurvatureSpline.MaxCoordinate.Value / 65536.0);
        var bigRadius = (maxCoordinate * 0.9);
        const double bigTurn = (Math.PI / 6.0);
        CurvatureSplineKnot[] largeScaleKnots = [
            CurvatureSplineCircleKnotAt(radius: bigRadius, elevation: 0, knotIndex: 0, turnRadians: bigTurn, signedCurvature: (1.0 / bigRadius)),
            CurvatureSplineCircleKnotAt(radius: bigRadius, elevation: 0, knotIndex: 1, turnRadians: bigTurn, signedCurvature: (1.0 / bigRadius)),
            CurvatureSplineCircleKnotAt(radius: bigRadius, elevation: 0, knotIndex: 2, turnRadians: bigTurn, signedCurvature: (1.0 / bigRadius)),
            CurvatureSplineCircleKnotAt(radius: bigRadius, elevation: 0, knotIndex: 3, turnRadians: bigTurn, signedCurvature: (1.0 / bigRadius)),
        ];

        foreach (var (label, knots) in new (string, CurvatureSplineKnot[])[] {
            ("small, well-conditioned", smallKnots),
            ("large-scale, conditioned", largeScaleKnots),
        }) {
            var compiled = CurvatureSpline.Compile(knots: knots, closed: false);

            foreach (var fraction in new[] { 0.0, 0.125, 0.25, 0.5, 0.75, 0.875, 1.0 }) {
                var stationRaw = ((long)(compiled.TotalLengthRaw * fraction));
                var subjectSample = compiled.EvaluateRaw(arcRaw: stationRaw);
                var subjectX = ((double)subjectSample.Position.X);
                var subjectZ = ((double)subjectSample.Position.Z);
                var segmentIndex = 0;

                for (var i = (compiled.SegmentCount - 1); (i >= 0); --i) {
                    if (stationRaw >= compiled.GetSegment(index: i).StationRaw) { segmentIndex = i; break; }
                }

                var segment = compiled.GetSegment(index: segmentIndex);
                var withinSegment = ((stationRaw - segment.StationRaw) / 4294967296.0);
                var (oracleX, oracleZ) = Oracles.CurvatureSplinePositionAtStation(segment: segment, targetArcLength: withinSegment, subdivisions: 100000);
                var scale = Math.Max(val1: 1.0, val2: Math.Max(val1: Math.Abs(value: subjectX), val2: Math.Abs(value: subjectZ)));
                var error = (Math.Sqrt(d: (((subjectX - oracleX) * (subjectX - oracleX)) + ((subjectZ - oracleZ) * (subjectZ - oracleZ)))) / scale);

                if (error > relativeTolerance) {
                    return $"{label} curve, fraction {fraction}: subject=({subjectX}, {subjectZ}) oracle=({oracleX}, {oracleZ}) relative error {error}";
                }
            }
        }

        return null;
    }

    /// <summary>Declaration-first coverage for every degenerate branch of the tangent-length system's branch table
    /// (<see cref="CurvatureSplineExactMath"/>'s own remarks): <c>w≠0</c> with both curvatures zero, <c>w≠0</c> with
    /// exactly one curvature zero on either side, and <c>w=0</c>'s zero-curvature-with-nonzero-s refusal on either
    /// side (reached with the OTHER side both admitting and refusing, so the branch order itself is exercised).
    /// Each admitted geometry's tangent lengths are checked against the branch's own closed form, computed
    /// independently in <see cref="double"/> from the same <c>s0</c>/<c>s1</c>/<c>w</c> the subject solves —
    /// never read back from the compiled raws.</summary>
    public static string? CurvatureSplineDegenerateBranches() {
        const double envelope = 1e-3;

        string? ExpectAdmit(string label, CurvatureSplineKnot start, CurvatureSplineKnot end, double expectedL0, double expectedL1) {
            var compiled = CurvatureSpline.Compile(knots: [start, end], closed: false);
            var segment = compiled.GetSegment(index: 0);
            var l0 = (segment.Tangent0LengthRaw / 4294967296.0);
            var l1 = (segment.Tangent1LengthRaw / 4294967296.0);

            return (((Math.Abs(value: (l0 - expectedL0)) > envelope) || (Math.Abs(value: (l1 - expectedL1)) > envelope))
                ? $"{label}: subject picked (l0={l0}, l1={l1}) but the closed form predicts (l0={expectedL0}, l1={expectedL1})"
                : null);
        }
        string? ExpectRefuse(string label, CurvatureSplineKnot start, CurvatureSplineKnot end, CurvatureSplineRefusal expected) {
            try {
                _ = CurvatureSpline.Compile(knots: [start, end], closed: false);
            } catch (CurvatureSplineException exception) {
                return ((exception.Refusal == expected) ? null : $"{label}: expected {expected} but got {exception.Refusal} ({exception.Message})");
            }

            return $"{label}: expected {expected} but the curve compiled.";
        }

        // Every geometry below shares T0 = yaw 0 (1, 0) and chord (3, 4) or (4, 3), so s0/s1/w are hand-verifiable:
        // the w != 0 battery uses T1 = yaw pi/2 (0, 1), w = 1, s0 = 4, s1 = -3; the w = 0 battery uses T1 = T0 (the
        // SAME rounded Q16 yaw as T0 — exactly parallel regardless of transcendental rounding, unlike a yaw and
        // yaw+pi pair, whose cos/sin round independently and so are not provably exactly antiparallel), giving
        // s1 = s0 = 3 rather than the opposite-signed pair an antiparallel construction would give.
        var detail =
            // w != 0, both curvatures zero: l0 = -s1/w = 3, l1 = s0/w = 4.
            ExpectAdmit(label: "w!=0, both kappa=0", start: CurvatureSplineKnotAt(0, 0, 0, 0, 0), end: CurvatureSplineKnotAt(3, 4, 0, Math.PI / 2, 0), expectedL0: 3.0, expectedL1: 4.0)
            // w != 0, only kappa0 = 0: eq0 fixes l1 = s0/w = 4, eq1 fixes l0 = (-s1 - 1.5*kappa1*l1^2)/w = 0.6.
            ?? ExpectAdmit(label: "w!=0, only kappa0=0", start: CurvatureSplineKnotAt(0, 0, 0, 0, 0), end: CurvatureSplineKnotAt(3, 4, 0, Math.PI / 2, 0.1), expectedL0: 0.6, expectedL1: 4.0)
            // w != 0, only kappa1 = 0: eq1 fixes l0 = -s1/w = 3, eq0 fixes l1 = (s0 - 1.5*kappa0*l0^2)/w = 1.3.
            ?? ExpectAdmit(label: "w!=0, only kappa1=0", start: CurvatureSplineKnotAt(0, 0, 0, 0, 0.2), end: CurvatureSplineKnotAt(3, 4, 0, Math.PI / 2, 0), expectedL0: 3.0, expectedL1: 1.3)
            // w = 0, both kappa != 0, correct-sign admit: tangent0 = sqrt((2/3)*(s0/kappa0)) = sqrt(10), tangent1 =
            // sqrt(-(2/3)*(s1/kappa1)) = sqrt(40/3) (T1 = T0, s1 = s0 = 3).
            ?? ExpectAdmit(label: "w=0, both kappa!=0 admit", start: CurvatureSplineKnotAt(0, 0, 0, 0, 0.2), end: CurvatureSplineKnotAt(4, 3, 0, 0, -0.15), expectedL0: Math.Sqrt(d: 10.0), expectedL1: Math.Sqrt(d: (40.0 / 3.0)))
            // w = 0, kappa0 = 0 with s0 != 0 (T1 = T0, s0 = 3): no tangent along T0 reaches zero curvature off the
            // chord direction — refuses before kappa1 (irrelevant here) is even read.
            ?? ExpectRefuse(label: "w=0, kappa0=0, s0!=0", start: CurvatureSplineKnotAt(0, 0, 0, 0, 0), end: CurvatureSplineKnotAt(4, 3, 0, 0, 0), expected: CurvatureSplineRefusal.TangentCurvatureInconsistent)
            // w = 0, kappa1 = 0 with s1 != 0 (T1 = T0, s1 = 3), reached only after kappa0 = 0.2's own admit succeeds
            // (s0*kappa0 = 0.6 > 0) — exercises the SECOND half of the w = 0 branch, not merely the first.
            ?? ExpectRefuse(label: "w=0, kappa1=0, s1!=0", start: CurvatureSplineKnotAt(0, 0, 0, 0, 0.2), end: CurvatureSplineKnotAt(4, 3, 0, 0, 0), expected: CurvatureSplineRefusal.TangentCurvatureInconsistent);

        return detail;
    }
}
