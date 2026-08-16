using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims over the quaternion, dual-number, vector2 wedge/dot and complex/rigid-transform kernels. Every oracle here
/// is written out by hand in this file — no call reaches <see cref="Oracles"/> — so no claim shares code with the
/// kernel it checks. All comparisons are exact-integer or <see cref="BigInteger"/>-exact; no <see cref="double"/>
/// arithmetic appears anywhere below, including the tolerances, which were picked by cross-checking the ladders and
/// round-trip bounds against the real kernels once (offline, outside this file) rather than guessed.
/// </summary>
internal static class GeometryClaims {
    // ---- shared exact BigInteger primitives, deliberately re-derived here rather than shared with Oracles.cs, so
    // this file owns its own reference chain ----

    /// <summary>One ties-to-even rounding of an exact Q32 raw sum down to Q16, then wrapped to the signed 64-bit
    /// carrier.</summary>
    private static long RoundProductSumOracle(BigInteger sum) {
        var negative = (sum.Sign < 0);
        var magnitude = BigInteger.Abs(value: sum);
        var truncated = BigInteger.DivRem(dividend: magnitude, divisor: (BigInteger.One << FixedQ4816.FractionBitCount), remainder: out var remainder);
        var half = (BigInteger.One << (FixedQ4816.FractionBitCount - 1));

        if ((remainder > half) || ((remainder == half) && !truncated.IsEven)) { ++truncated; }

        var signed = (negative ? -truncated : truncated);
        var wrapped = signed & ((BigInteger.One << 64) - BigInteger.One);

        return ((wrapped >= (BigInteger.One << 63)) ? (long)(wrapped - (BigInteger.One << 64)) : (long)wrapped);
    }
    /// <summary>One ties-to-even rounding of an exact rational numerator/denominator onto the Q16 grid, then wrapped
    /// to the signed 64-bit carrier.</summary>
    private static long RoundRatioQ16(BigInteger numerator, BigInteger denominator) {
        var negative = (numerator.Sign < 0);
        var quotient = BigInteger.DivRem(dividend: (BigInteger.Abs(value: numerator) << FixedQ4816.FractionBitCount), divisor: denominator, remainder: out var remainder);
        var distanceToNext = (denominator - remainder);

        if ((remainder > distanceToNext) || ((remainder == distanceToNext) && !quotient.IsEven)) { ++quotient; }
        if (negative) { quotient = -quotient; }

        var wrapped = quotient & ((BigInteger.One << 64) - BigInteger.One);

        return ((wrapped >= (BigInteger.One << 63)) ? (long)(wrapped - (BigInteger.One << 64)) : (long)wrapped);
    }
    /// <summary>The exact Hamilton (quaternion) product of two raw [X,Y,Z,W] lane quads, one <see
    /// cref="RoundProductSumOracle"/> rounding per lane.</summary>
    private static long[] HamiltonProductOracle(long[] left, long[] right) {
        var (lx, ly, lz, lw) = (left[0], left[1], left[2], left[3]);
        var (rx, ry, rz, rw) = (right[0], right[1], right[2], right[3]);

        return [
            RoundProductSumOracle(sum: ((((((BigInteger)lw) * rx) + (((BigInteger)lx) * rw)) + (((BigInteger)ly) * rz)) - (((BigInteger)lz) * ry))),
            RoundProductSumOracle(sum: ((((((BigInteger)lw) * ry) - (((BigInteger)lx) * rz)) + (((BigInteger)ly) * rw)) + (((BigInteger)lz) * rx))),
            RoundProductSumOracle(sum: ((((((BigInteger)lw) * rz) + (((BigInteger)lx) * ry)) - (((BigInteger)ly) * rx)) + (((BigInteger)lz) * rw))),
            RoundProductSumOracle(sum: ((((((BigInteger)lw) * rw) - (((BigInteger)lx) * rx)) - (((BigInteger)ly) * ry)) - (((BigInteger)lz) * rz))),
        ];
    }
    /// <summary>A BigInteger bisection search for the nearest ties-to-even Q16 raw to <c>|component|·|vector|⁻¹</c>,
    /// signed back at the end.</summary>
    private static long[] NormalizeOracle(long[] values) {
        var squaredSum = BigInteger.Zero;

        foreach (var value in values) {
            var magnitude = BigInteger.Abs(value: new BigInteger(value: value));

            squaredSum += (magnitude * magnitude);
        }

        var result = new long[values.Length];

        if (squaredSum.IsZero) { return result; }

        for (var i = 0; (i < values.Length); ++i) {
            var numerator = (BigInteger.Abs(value: new BigInteger(value: values[i])) << FixedQ4816.FractionBitCount);
            var numeratorSquared = (numerator * numerator);
            var low = 0L;
            var high = (FixedQ4816.One.Value + 1L);

            while ((low + 1L) < high) {
                var middle = ((low + high) >> 1);

                if (((((BigInteger)middle) * middle) * squaredSum) <= numeratorSquared) { low = middle; } else { high = middle; }
            }

            var doubledNumeratorSquared = (4 * numeratorSquared);
            var midpoint = ((2 * ((BigInteger)low)) + BigInteger.One);
            var midpointSquared = ((midpoint * midpoint) * squaredSum);

            if ((doubledNumeratorSquared > midpointSquared) || ((doubledNumeratorSquared == midpointSquared) && ((low & 1L) != 0L))) { ++low; }

            result[i] = ((values[i] < 0L) ? -low : low);
        }

        return result;
    }
    // ---- exact enclosure plumbing for the axis-angle, Exp/Log and dual-derivative ladders below: EVERY expected
    // value in those three claims comes from Oracles.EncloseSinCos / EncloseAtan2 (for the transcendentals),
    // Oracles.NearestIntegerRoot / IntegerSquareRoot (for the exact magnitude and square-root pieces beneath them)
    // and the NormalizeOracle bisection above (for the exact unit axis) — never from an offline double computation,
    // so none of those three carries a REGRESSION PIN any longer. Written BY HAND, on the same "shares no code"
    // basis as this file's other primitives. ----

    // The guard-scale tolerance in raw ULP units — e.g. GuardUlpUnits(3, 4) is 3/4 of one raw ULP, the SAME regime
    // scalar.sincos-vs-series and scalar.atan2-vs-series already pin for the kernels these ladders call, reused here
    // as an established fact rather than re-derived.
    private static BigInteger GuardUlpUnits(int numerator, int denominator) =>
        (((BigInteger.One << Oracles.GuardBitCount) * numerator) / denominator);
    // Scales a guard-unit tolerance by a raw factor's own magnitude (a genuine multiplication the claim performs
    // scales its propagated error the same way), rounding UP so the scaled tolerance never understates it.
    private static BigInteger ScaleToleranceByRawFactor(BigInteger toleranceUnits, long factorRawMagnitude) =>
        ((((toleranceUnits * BigInteger.Abs(value: new BigInteger(value: factorRawMagnitude))) + 65535) / 65536));
    // Attenuates (or, for a sub-unit divisor, amplifies) a guard-unit tolerance by dividing by a raw factor's own
    // magnitude — the mirror of ScaleToleranceByRawFactor for a claim step that DIVIDES rather than multiplies.
    // Rounds UP, so it never understates the propagated tolerance either.
    private static BigInteger DivideToleranceByRawFactor(BigInteger toleranceUnits, long divisorRawMagnitude) {
        var magnitude = BigInteger.Abs(value: new BigInteger(value: divisorRawMagnitude));

        return ((((toleranceUnits << FixedQ4816.FractionBitCount) + magnitude) - 1) / magnitude);
    }
    // The floor, respectively ceiling, of an exact BigInteger ratio for a POSITIVE denominator — BigInteger's own
    // division truncates toward zero, which floors a non-negative numerator but NOT a negative one (a sine
    // enclosure bound can be negative), so every directed rounding below routes through one of these two rather than
    // the raw `/` operator.
    private static BigInteger FloorDivide(BigInteger numerator, BigInteger positiveDenominator) {
        var quotient = BigInteger.DivRem(dividend: numerator, divisor: positiveDenominator, remainder: out var remainder);

        return ((remainder < 0) ? (quotient - 1) : quotient);
    }
    private static BigInteger CeilingDivide(BigInteger numerator, BigInteger positiveDenominator) {
        var quotient = BigInteger.DivRem(dividend: numerator, divisor: positiveDenominator, remainder: out var remainder);

        return ((remainder > 0) ? (quotient + 1) : quotient);
    }
    // A raw lies within a BigInteger enclosure (at guard scale) widened by a raw-ULP tolerance, also at guard scale.
    private static string? WithinGuardEnvelope(string name, long subjectRaw, Oracles.Enclosure enclosure, BigInteger toleranceUnits) {
        var scaled = (new BigInteger(value: subjectRaw) << Oracles.GuardBitCount);

        if (scaled < (enclosure.Low - toleranceUnits)) { return $"{name} is {subjectRaw}, below the exact envelope [{enclosure.Low}, {enclosure.High}] (guard scale) by more than {toleranceUnits} guard units"; }
        if (scaled > (enclosure.High + toleranceUnits)) { return $"{name} is {subjectRaw}, above the exact envelope [{enclosure.Low}, {enclosure.High}] (guard scale) by more than {toleranceUnits} guard units"; }

        return null;
    }
    // The exact enclosure of "multiplierRaw · (the enclosed value)" — both bounds narrowed by the multiplier's own
    // Q48.16 scale with directed rounding, so this is the IDEAL (never rounded) product, not the kernel's own
    // rounded one.
    private static Oracles.Enclosure ScaleByRawFactor(Oracles.Enclosure enclosure, long multiplierRaw) {
        var multiplier = new BigInteger(value: multiplierRaw);

        var (loProduct, hiProduct) = ((multiplier.Sign >= 0)
            ? ((multiplier * enclosure.Low), (multiplier * enclosure.High))
            : ((multiplier * enclosure.High), (multiplier * enclosure.Low)));

        return new(High: -((-hiProduct) >> FixedQ4816.FractionBitCount), Low: (loProduct >> FixedQ4816.FractionBitCount));
    }
    // The exact enclosure of "(the enclosed value) / divisorRaw" for a POSITIVE divisor raw — the division mirror of
    // ScaleByRawFactor, both bounds narrowed with directed rounding so the IDEAL (never rounded) quotient is bracketed.
    private static Oracles.Enclosure DivideEnclosureByRawFactor(Oracles.Enclosure enclosure, long divisorRaw) {
        var divisor = new BigInteger(value: divisorRaw);

        return new(
            Low: FloorDivide(numerator: (enclosure.Low << FixedQ4816.FractionBitCount), positiveDenominator: divisor),
            High: CeilingDivide(numerator: (enclosure.High << FixedQ4816.FractionBitCount), positiveDenominator: divisor)
        );
    }
    // Interval arithmetic on two independently-bracketed enclosures at the SAME scale: the widest possible sum,
    // respectively difference, so the result still brackets the true value whichever operand realizes its extreme.
    private static Oracles.Enclosure AddEnclosures(Oracles.Enclosure left, Oracles.Enclosure right) =>
        new(Low: (left.Low + right.Low), High: (left.High + right.High));
    private static Oracles.Enclosure SubtractEnclosures(Oracles.Enclosure left, Oracles.Enclosure right) =>
        new(Low: (left.Low - right.High), High: (left.High - right.Low));
    // Widens an enclosure by a flat additive tolerance on both sides — the standard way an ADDITIONAL rounding (a
    // kernel's own ULP envelope, or one more fixed-point operation's single rounding) enters a propagated bound.
    private static Oracles.Enclosure WidenEnclosure(Oracles.Enclosure enclosure, BigInteger toleranceUnits) =>
        new(Low: (enclosure.Low - toleranceUnits), High: (enclosure.High + toleranceUnits));
    /// <summary>The largest exact component of the 3-D cross product of two raw triples, taken in <see
    /// cref="BigInteger"/> so no product can leave the carrier — the alignment check <c>FixedQuaternion.FromTo</c>
    /// and <c>FixedComplex.FromTo</c> both satisfy by definition (image aligned with target ⇔ cross ≈ 0, dot ≥ 0),
    /// transcribed from the same technique already independently used by <c>Subjects.QuaternionFromToShortestArc</c>
    /// in this project — re-derived here rather than called, so this file still shares no code with it.</summary>
    private static BigInteger MaxAbsCross3(long ix, long iy, long iz, long tx, long ty, long tz) {
        var (bix, biy, biz) = (((BigInteger)ix), ((BigInteger)iy), ((BigInteger)iz));
        var (btx, bty, btz) = (((BigInteger)tx), ((BigInteger)ty), ((BigInteger)tz));

        return BigInteger.Max(
            left: BigInteger.Abs(value: ((biy * btz) - (biz * bty))),
            right: BigInteger.Max(left: BigInteger.Abs(value: ((biz * btx) - (bix * btz))), right: BigInteger.Abs(value: ((bix * bty) - (biy * btx)))));
    }

    // Roughly one degree of angular slack at Q16, exactly the bound Subjects.QuaternionFromToShortestArc already
    // uses for the identical alignment check — an independently re-derived choice that happens to land on the same
    // honest number, not a shared constant.
    private static readonly BigInteger AlignmentBound = (new BigInteger(value: 1024L) << 16);
    // The nine-raw edge battery every full-width quad/pair oracle below sweeps: both carrier extremes with the raw
    // next to each, one raw ULP at both signs, Q16 one at both signs, and zero.
    private static readonly long[] LongEdges9 = [long.MinValue, (long.MinValue + 1L), -65536L, -1L, 0L, 1L, 65536L, (long.MaxValue - 1L), long.MaxValue];
    // The ten-raw edge battery the complex division/multiply oracle sweeps: both carrier extremes, two intermediate
    // powers of two at both signs, one raw ULP at both signs, and zero.
    private static readonly long[] ComplexEdges10 = [long.MinValue, (long.MinValue + 1L), -(1L << 62), -(1L << 32), -1L, 0L, 1L, (1L << 32), (1L << 62), long.MaxValue];
    // A curated 14-row quaternion edge set (one lane at an extreme with the rest zero, all-extreme rows, and a few
    // mixed rows) crossed against itself: 196 combinations spanning the carrier's boundary behavior without the 9^8
    // cost a literal exhaustive cross product over four lanes on each side would carry.
    private static readonly long[][] QuaternionEdgeQuads = [
        [long.MinValue, 0L, 0L, 0L], [0L, long.MinValue, 0L, 0L], [0L, 0L, long.MinValue, 0L], [0L, 0L, 0L, long.MinValue],
        [long.MaxValue, 0L, 0L, 0L], [0L, long.MaxValue, 0L, 0L], [0L, 0L, long.MaxValue, 0L], [0L, 0L, 0L, long.MaxValue],
        [long.MinValue, long.MinValue, long.MinValue, long.MinValue], [long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue],
        [long.MinValue, long.MaxValue, long.MinValue, long.MaxValue], [1L, -1L, 1L, -1L], [65536L, 65536L, 65536L, 65536L], [-65536L, 32768L, -32768L, 65536L],
    ];
    // A curated 10-row vector3 edge set, crossed against itself: 100 combinations, the same reduced-cost tradeoff as
    // QuaternionEdgeQuads above.
    private static readonly long[][] Vector3EdgeTriples = [
        [long.MinValue, 0L, 0L], [0L, long.MinValue, 0L], [0L, 0L, long.MinValue],
        [long.MaxValue, 0L, 0L], [0L, long.MaxValue, 0L], [0L, 0L, long.MaxValue],
        [long.MinValue, long.MinValue, long.MinValue], [long.MaxValue, long.MaxValue, long.MaxValue],
        [1L, -1L, 65536L], [-65536L, 32768L, -1L],
    ];
    // A safely-bounded relative of Vector3EdgeTriples for the sandwich-product witness in
    // QuaternionRotateScheduleTranscriptionSurface below: forming the INTERMEDIATE quaternion q⊗(0,v) puts v's
    // magnitude into the scalar (W) lane too — as up to THREE summed lane products rather than one — so the
    // carrier's own long.MinValue/MaxValue rows overflow that intermediate (wrap to garbage) regardless of q being
    // unit. long.MaxValue >> 3 keeps three such terms, even at q's full unit magnitude, safely under 2^63.
    private static readonly long[][] Vector3ModerateEdgeTriples = [
        [(long.MaxValue >> 3), 0L, 0L], [0L, (long.MaxValue >> 3), 0L], [0L, 0L, (long.MaxValue >> 3)],
        [-(long.MaxValue >> 3), 0L, 0L], [0L, -(long.MaxValue >> 3), 0L], [0L, 0L, -(long.MaxValue >> 3)],
        [(long.MaxValue >> 3), (long.MaxValue >> 3), (long.MaxValue >> 3)], [-(long.MaxValue >> 3), -(long.MaxValue >> 3), -(long.MaxValue >> 3)],
        [1L, -1L, 65536L], [-65536L, 32768L, -1L],
    ];

    private static FixedQ4816 Raw(long value) => FixedQ4816.FromRawBits(value: value);
    private static FixedQuaternion QuaternionOf(long[] lanes) => new(X: Raw(value: lanes[0]), Y: Raw(value: lanes[1]), Z: Raw(value: lanes[2]), W: Raw(value: lanes[3]));
    private static FixedVector3 Vector3Of(long[] lanes) => new(X: Raw(value: lanes[0]), Y: Raw(value: lanes[1]), Z: Raw(value: lanes[2]));

    // ==== "quaternion / dual" banner ====================================================================

    // The axis/angle rows the ladder below sweeps: all three basis axes and a tilted axis, at a spread of angles
    // (including an odd raw — the half turn — whose half-angle is an exact ties-to-even tie, and a negative angle).
    // No expected quad is declared here any longer; QuaternionFromAxisAngleLadderSurface derives its own from
    // Oracles.EncloseSinCos at each row's EXACT half angle.
    private static readonly (long[] Axis, long AngleRaw)[] AxisAngleLadder = [
        ([0L, 0L, 65536L], 102944L),
        ([0L, 0L, 65536L], 205887L),
        ([0L, 0L, 65536L], 68628L),
        ([0L, 0L, 65536L], 137256L),
        ([0L, 0L, 65536L], -102944L),
        ([65536L, 0L, 0L], 51472L),
        ([0L, 65536L, 0L], 137256L),
        ([37837L, 37837L, 37837L], 68628L),
    ];

    // FixedQuaternion.FromAxisAngle multiplies by the EXACT constant Half (raw 32768 = 2^15), so the Q48.16 product
    // angleRaw·32768/65536 is angleRaw/2 exactly when angleRaw is even, or an EXACT tie (rounded to even) when it is
    // odd — the same ties-to-even rule scalar.mul-vs-oracle already pins for every FixedQ4816 multiply, applied here
    // to the one constant divisor that can ever produce a tie. No offline double computation anywhere.
    private static long HalfRawTiesToEven(long raw) {
        var floorHalf = (raw >> 1);

        if ((raw & 1L) == 0L) { return floorHalf; }

        return (((floorHalf & 1L) == 0L) ? floorHalf : (floorHalf + 1L));
    }

    /// <summary>Proves <see cref="FixedQuaternion.FromAxisAngle"/> against Oracles.EncloseSinCos at the EXACT half
    /// angle each row's angleRaw forms, spanning all three basis axes and a tilted axis, plus the exact zero-angle
    /// pole.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionFromAxisAngleLadderSurface() {
        var kernelUlp = GuardUlpUnits(denominator: 4, numerator: 3);

        foreach (var (axis, angleRaw) in AxisAngleLadder) {
            var half = HalfRawTiesToEven(raw: angleRaw);
            var enclosure = Oracles.EncloseSinCos(guardBitCount: Oracles.GuardBitCount, raw: half);
            var value = FixedQuaternion.FromAxisAngle(axis: Vector3Of(lanes: axis), angle: Raw(value: angleRaw));

            // W = cos(half) directly, with no further rounding, so it stands against the kernel's own committed
            // SinCos envelope alone.
            if (WithinGuardEnvelope(name: $"W of FromAxisAngle(axis=[{axis[0]},{axis[1]},{axis[2]}], angleRaw={angleRaw})", subjectRaw: value.W.Value, enclosure: enclosure.Cos, toleranceUnits: kernelUlp) is { } wDetail) { return wDetail; }

            // X, Y, Z are axis-component times sin(half): the kernel's own sine envelope (propagated through that
            // ONE further Q48.16 rounding) scaled by the axis component's own magnitude, plus that rounding's own
            // half raw ULP.
            var lanes = new[] { value.X.Value, value.Y.Value, value.Z.Value };
            var names = new[] { "X", "Y", "Z" };

            for (var lane = 0; (lane < 3); ++lane) {
                var laneTolerance = (ScaleToleranceByRawFactor(toleranceUnits: kernelUlp, factorRawMagnitude: axis[lane]) + GuardUlpUnits(denominator: 2, numerator: 1));
                var expected = ScaleByRawFactor(enclosure: enclosure.Sin, multiplierRaw: axis[lane]);

                if (WithinGuardEnvelope(name: $"{names[lane]} of FromAxisAngle(axis=[{axis[0]},{axis[1]},{axis[2]}], angleRaw={angleRaw})", subjectRaw: lanes[lane], enclosure: expected, toleranceUnits: laneTolerance) is { } laneDetail) { return laneDetail; }
            }

            if (FixedQuaternion.FromAxisAngle(axis: Vector3Of(lanes: axis), angle: FixedQ4816.Zero) != FixedQuaternion.Identity) {
                return $"a zero angle about [{axis[0]},{axis[1]},{axis[2]}] is not the identity";
            }
        }

        return null;
    }

    // T5/T6-equivalent ladders: the bivectors and quaternions themselves, unchanged; no expected quad/triple is
    // declared any longer — QuaternionExpLogSurface derives its own from Oracles.EncloseSinCos / EncloseAtan2 at
    // each row's EXACT magnitude (Oracles.NearestIntegerRoot) and EXACT unit axis (NormalizeOracle above).
    private static readonly long[][] ExpLadder = [
        [0L, 0L, 51472L],
        [0L, 51472L, 0L],
        [51472L, 0L, 0L],
        [0L, 0L, -51472L],
        [0L, 0L, 102944L],
        [0L, 0L, 205887L],
        [29717L, 29717L, 29717L],
    ];
    private static readonly long[][] LogLadder = [
        [0L, 0L, 46341L, 46341L],
        [0L, 0L, 65536L, 0L],
        [0L, 0L, 32768L, 56756L],
        [0L, 0L, -46341L, 46341L],
        [21845L, 43691L, 43691L, 0L],
    ];

    // The wrap check's own tolerance: both sides are the SAME kernel call at raws that name the identical rotation,
    // so this stays a structural round-trip rather than an oracle comparison, and keeps its prior hand-picked band.
    private const long WrapTolerance = 4L;

    /// <summary>Proves <see cref="FixedQuaternion.Exp"/> against Oracles.EncloseSinCos at each row's EXACT magnitude
    /// (<see cref="Oracles.NearestIntegerRoot"/> of the exact squared sum, matching <c>FixedVectorMath</c>'s own
    /// proven round-to-nearest rule bit for bit) and EXACT unit axis (the NormalizeOracle bisection above), and
    /// <see cref="FixedQuaternion.Log"/> against Oracles.EncloseAtan2 at each row's EXACT vector length and unit
    /// axis the same way — plus the three exact poles (<c>Exp(0)</c>, <c>Log(±Identity)</c>) and that a bivector
    /// beyond a full turn wraps through the turn-domain reduction rather than diverging.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionExpLogSurface() {
        if (FixedQuaternion.Exp(bivector: FixedVector3.Zero) != FixedQuaternion.Identity) { return "Exp(0) is not the identity"; }
        if (FixedQuaternion.Identity.Log() != FixedVector3.Zero) { return "Log(Identity) is not the zero bivector"; }
        if ((-FixedQuaternion.Identity).Log() != FixedVector3.Zero) { return "Log(-Identity) is not the zero bivector"; }

        var kernelUlp = GuardUlpUnits(denominator: 4, numerator: 3);
        var laneNames = new[] { "X", "Y", "Z" };

        foreach (var bivector in ExpLadder) {
            var squaredSum = (((((BigInteger)bivector[0]) * bivector[0]) + (((BigInteger)bivector[1]) * bivector[1])) + (((BigInteger)bivector[2]) * bivector[2]));
            var idealMagnitude = Oracles.NearestIntegerRoot(value: squaredSum);
            var enclosure = Oracles.EncloseSinCos(guardBitCount: Oracles.GuardBitCount, raw: ((long)idealMagnitude));
            var idealAxis = NormalizeOracle(values: bivector);
            var value = FixedQuaternion.Exp(bivector: Vector3Of(lanes: bivector));

            // W is cos(magnitude) directly, with no further rounding, so it stands against the kernel's own
            // committed SinCos envelope alone.
            if (WithinGuardEnvelope(name: $"W of Exp([{bivector[0]},{bivector[1]},{bivector[2]}])", subjectRaw: value.W.Value, enclosure: enclosure.Cos, toleranceUnits: kernelUlp) is { } wDetail) { return wDetail; }

            var lanes = new[] { value.X.Value, value.Y.Value, value.Z.Value };

            for (var lane = 0; (lane < 3); ++lane) {
                var laneTolerance = (ScaleToleranceByRawFactor(toleranceUnits: kernelUlp, factorRawMagnitude: idealAxis[lane]) + GuardUlpUnits(denominator: 1, numerator: 1));
                var expected = ScaleByRawFactor(enclosure: enclosure.Sin, multiplierRaw: idealAxis[lane]);

                if (WithinGuardEnvelope(name: $"{laneNames[lane]} of Exp([{bivector[0]},{bivector[1]},{bivector[2]}])", subjectRaw: lanes[lane], enclosure: expected, toleranceUnits: laneTolerance) is { } laneDetail) { return laneDetail; }
            }
        }

        foreach (var quaternion in LogLadder) {
            var (qx, qy, qz, qw) = (quaternion[0], quaternion[1], quaternion[2], quaternion[3]);
            var squaredSum = (((((BigInteger)qx) * qx) + (((BigInteger)qy) * qy)) + (((BigInteger)qz) * qz));
            var vectorLength = Oracles.NearestIntegerRoot(value: squaredSum);

            if (vectorLength.IsZero) { continue; }

            var thetaEnclosure = Oracles.EncloseAtan2(guardBitCount: Oracles.GuardBitCount, xRaw: qw, yRaw: ((long)vectorLength));
            var idealAxis = NormalizeOracle(values: [qx, qy, qz]);
            var value = QuaternionOf(lanes: quaternion).Log();
            var lanes = new[] { value.X.Value, value.Y.Value, value.Z.Value };
            // Log computes scale = Atan2(vectorLength, W)/vectorLength then component·scale directly (no separate
            // normalize step), so a raw ULP of Atan2/division error is attenuated by the SAME factor a component's
            // own magnitude is bounded by (≤ vectorLength) — this bounds it the OTHER, more generous, way: by how
            // much a sub-unit vectorLength AMPLIFIES the division alone, never relying on that cancellation.
            var amplification = (((65536L + ((long)vectorLength)) - 1L) / ((long)vectorLength));

            for (var lane = 0; (lane < 3); ++lane) {
                var laneTolerance = ((amplification * (kernelUlp + GuardUlpUnits(denominator: 2, numerator: 1))) + GuardUlpUnits(denominator: 2, numerator: 1));
                var expected = ScaleByRawFactor(enclosure: thetaEnclosure, multiplierRaw: idealAxis[lane]);

                if (WithinGuardEnvelope(name: $"{laneNames[lane]} of Log([{qx},{qy},{qz},{qw}])", subjectRaw: lanes[lane], enclosure: expected, toleranceUnits: laneTolerance) is { } laneDetail) { return laneDetail; }
            }
        }

        // A bivector beyond a full turn wraps: 2π raw is 411775, so a quarter turn plus one full turn is the same
        // rotation.
        var quarter = FixedQuaternion.Exp(bivector: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: Raw(value: 51472L)));
        var wrapped = FixedQuaternion.Exp(bivector: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: Raw(value: (51472L + 411775L))));

        if (Math.Abs(value: (wrapped.Z.Value - quarter.Z.Value)) > WrapTolerance) { return $"the wrapped bivector's Z lane is {wrapped.Z.Value}, expected {quarter.Z.Value}"; }
        if (Math.Abs(value: (wrapped.W.Value - quarter.W.Value)) > WrapTolerance) { return $"the wrapped bivector's W lane is {wrapped.W.Value}, expected {quarter.W.Value}"; }

        var enormous = FixedQuaternion.Exp(bivector: new FixedVector3(X: FixedQ4816.MaxValue, Y: FixedQ4816.MaxValue, Z: FixedQ4816.MaxValue));

        if (!enormous.TryLength(length: out var enormousLength) || (Math.Abs(value: (enormousLength.Value - FixedQ4816.One.Value)) > 8L)) {
            return "a bivector at the carrier's extreme did not exponentiate to a unit rotation";
        }

        return null;
    }

    // T7-equivalent ladder, from Identity to the quarter-turn quaternion [0,0,46341,46341]. No expected Z/W is
    // declared any longer (see QuaternionSlerpSurface below) — only the swept `amount` raws remain.
    private static readonly long[] SlerpLadderAmounts = [0L, 16384L, 32768L, 49152L, 65536L];

    // The cosine above which the kernel falls back to a normalized linear blend, read verbatim off the documented
    // threshold (65503/65536).
    private const long NlerpThreshold = 65503L;

    /// <summary>Proves <see cref="FixedQuaternion.Slerp"/>'s DIRECTION on the sine-ratio branch against an
    /// enclosure-exact reference built from <see cref="Oracles.EncloseAtan2"/> / <see cref="Oracles.EncloseSinCos"/>
    /// threading through the SAME Dot/Sqrt/Atan2/SinCos/divide/multiply chain the kernel performs — <see
    /// cref="FixedQ4816.Sqrt"/> is EXACT here (its own XML doc: the result is <c>⌊√(raw·2¹⁶)⌋</c>, matching <see
    /// cref="Oracles.IntegerSquareRoot"/> bit for bit), so only Atan2 and SinCos contribute a kernel envelope — plus
    /// that both branches are reached and the exact zero endpoint.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// This checks DIRECTION (an exact BigInteger cross-product alignment against the ACTUAL, already-normalized
    /// output) rather than the raw lane values a REGRESSION-PIN ladder would fix. That sidesteps the composition
    /// problem the OWED marker named: Slerp's return value passes through a final <c>Normalize()</c>, stacking a
    /// sqrt/divide chain on top of the interpolation's own chain, and bounding the FULL composition end to end would
    /// need Normalize's own error folded into the same proof. Route (a) from the OWED marker turns out to be
    /// reachable here because normalization preserves DIRECTION exactly — <c>v/|v|</c> is a positive scalar multiple
    /// of <c>v</c>, so <c>v × normalize(v) = 0</c> identically, for ANY nonzero <c>v</c>. Checking alignment (cross
    /// product) rather than value needs no division by <c>Normalize</c>'s own (possibly amplifying) denominator at
    /// all: the only slop <c>Normalize</c> contributes is its OWN independently-established ±1-raw-per-lane
    /// quantization (<c>quaternion.normalize-unit-direction</c>, general over any exact input, not curated edges),
    /// entering the cross term ADDITIVELY — multiplied by the near-unit magnitude of the OTHER lane, never divided
    /// by anything. This is genuinely amplification-aware (it reasons about how Normalize's own error propagates
    /// into the cross term) while staying DERIVED rather than fitted: every tolerance term below is either an
    /// established kernel envelope reused from elsewhere in this file, or one flat additional half-ULP for one more
    /// real rounding the actual chain performs.
    /// </remarks>
    public static string? QuaternionSlerpSurface() {
        var quarter = new FixedQuaternion(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: Raw(value: 46341L), W: Raw(value: 46341L));
        var nearby = new FixedQuaternion(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: Raw(value: 256L), W: Raw(value: 65535L));

        if (FixedQuaternion.Dot(left: FixedQuaternion.Identity, right: quarter).Value > NlerpThreshold) { return "the quarter-turn pair does not reach the sine-ratio branch"; }
        if (FixedQuaternion.Dot(left: FixedQuaternion.Identity, right: nearby).Value <= NlerpThreshold) { return "the nearly-parallel pair does not reach the normalized linear blend branch"; }

        // Dot(Identity, quarter): Identity.W is exactly FixedQ4816.One, and multiplying by exactly one never rounds
        // (the remainder against the RoundProductSum divisor is zero), so this is exact — not merely enclosed —
        // matching RoundProductSumOracle's transcription of FixedQuaternion.Dot's own rounding rule, already
        // established as classical evidence for Dot elsewhere in this file (quaternion.hamilton-product-dot-inverse-full-width).
        var dotRaw = RoundProductSumOracle(sum: (((BigInteger)FixedQuaternion.Identity.W.Value) * quarter.W.Value));
        // sinTheta² = One − dot·dot: the ONE further multiply rounds (RoundProductSumOracle again, exact match), and
        // the subtraction from One is exact (no rounding for same-scale addition/subtraction).
        var sinThetaSquaredRaw = (FixedQ4816.One.Value - RoundProductSumOracle(sum: (((BigInteger)dotRaw) * dotRaw)));
        // sinTheta = Sqrt(sinTheta²): EXACT, per FixedQ4816.Sqrt's own documented ⌊√(raw·2¹⁶)⌋ — bit for bit the
        // same value Oracles.IntegerSquareRoot computes, so this carries no envelope of its own.
        var sinThetaRaw = ((long)Oracles.IntegerSquareRoot(value: (((BigInteger)sinThetaSquaredRaw) << FixedQ4816.FractionBitCount)));
        // The established Atan2/SinCos kernel ULP envelope this file already reuses (see QuaternionFromAxisAngleLadderSurface
        // and QuaternionExpLogSurface) rather than re-deriving it.
        var kernelUlp = GuardUlpUnits(denominator: 4, numerator: 3);
        var halfUlp = GuardUlpUnits(denominator: 2, numerator: 1);
        // theta = Atan2(sinTheta, dot): both operands exact, so the enclosure needs widening only by the kernel's
        // own established envelope, not by any operand uncertainty.
        var thetaEnclosure = WidenEnclosure(enclosure: Oracles.EncloseAtan2(guardBitCount: Oracles.GuardBitCount, xRaw: dotRaw, yRaw: sinThetaRaw), toleranceUnits: kernelUlp);
        // Normalize's OWN established ±1-raw-per-lane quantization (quaternion.normalize-unit-direction), entering
        // the cross term additively: each lane's quantization multiplies the OTHER near-unit-magnitude lane (bounded
        // by FixedQ4816.One, since the pre-normalize interpolant of two near-unit quaternions stays near unit), so
        // two guard units — one per lane — safely covers both without dividing by anything.
        var normalizeSlop = GuardUlpUnits(denominator: 1, numerator: 2);

        foreach (var amountRaw in SlerpLadderAmounts) {
            // angleArg = amount·theta: amount is exact, theta only enclosed, so the enclosure scales exactly and
            // then widens by one more half-ULP for THIS multiply's own rounding.
            var angleArgEnclosure = WidenEnclosure(enclosure: ScaleByRawFactor(enclosure: thetaEnclosure, multiplierRaw: amountRaw), toleranceUnits: halfUlp);
            var midpoint = ((angleArgEnclosure.Low + angleArgEnclosure.High) / 2);
            var representativeRaw = ((long)(midpoint >> Oracles.GuardBitCount));
            // The gap between the representative integer raw EncloseSinCos needs and the true (interval-valued)
            // angleArg: the interval's own width, plus the floor-rounding slop turning its midpoint into an
            // integer. Sin and cosine each have derivative magnitude at most one (in these consistent guard-scale
            // units, since both angle and sin/cos share the same 2^(16+guard) scale), so this gap widens their
            // enclosures by exactly that much — no amplification, a flat Lipschitz addition.
            var representativeGap = ((angleArgEnclosure.High - angleArgEnclosure.Low) + GuardUlpUnits(denominator: 1, numerator: 1));

            var (sinBase, cosBase) = Oracles.EncloseSinCos(guardBitCount: Oracles.GuardBitCount, raw: representativeRaw);
            var sinEnclosure = WidenEnclosure(enclosure: sinBase, toleranceUnits: (kernelUlp + representativeGap));
            var cosEnclosure = WidenEnclosure(enclosure: cosBase, toleranceUnits: (kernelUlp + representativeGap));

            // toWeight = sinScaled/sinTheta: sinTheta is exact, sinScaled only enclosed; one more half-ULP for this
            // division's own rounding.
            var toWeightEnclosure = WidenEnclosure(enclosure: DivideEnclosureByRawFactor(divisorRaw: sinThetaRaw, enclosure: sinEnclosure), toleranceUnits: halfUlp);
            // fromWeight = cosScaled − dot·toWeight: dot is exact, the multiply rounds once more (half-ULP), the
            // subtraction of two same-scale enclosures is exact interval arithmetic.
            var fromWeightEnclosure = WidenEnclosure(enclosure: SubtractEnclosures(left: cosEnclosure, right: ScaleByRawFactor(enclosure: toWeightEnclosure, multiplierRaw: dotRaw)), toleranceUnits: halfUlp);
            // Z_pre = from.Z·fromWeight + to.Z·toWeight = 0·fromWeight + 46341·toWeight (from.Z is exactly zero, so
            // that term is exact zero, not merely enclosed) — one more half-ULP for the surviving multiply.
            var zPreEnclosure = WidenEnclosure(enclosure: ScaleByRawFactor(enclosure: toWeightEnclosure, multiplierRaw: 46341L), toleranceUnits: halfUlp);
            // W_pre = from.W·fromWeight + to.W·toWeight = 1·fromWeight (exact, multiplying by One never rounds) +
            // 46341·toWeight (one more half-ULP); the addition of two enclosures is exact interval arithmetic.
            var wPreEnclosure = WidenEnclosure(enclosure: AddEnclosures(left: fromWeightEnclosure, right: ScaleByRawFactor(enclosure: toWeightEnclosure, multiplierRaw: 46341L)), toleranceUnits: halfUlp);

            var value = FixedQuaternion.Slerp(from: FixedQuaternion.Identity, to: quarter, amount: Raw(value: amountRaw));

            if ((value.X.Value != 0L) || (value.Y.Value != 0L)) { return $"the interpolation at {amountRaw} left the rotation plane"; }

            // Cross-product alignment: value × (Z_pre, W_pre) should enclose zero — value is parallel to its OWN
            // pre-normalize input up to Normalize's established ±1-raw-per-lane slop, and (Z_pre, W_pre) is an
            // enclosure of that SAME pre-normalize input.
            var crossEnclosure = SubtractEnclosures(
                left: ScaleByRawFactor(enclosure: wPreEnclosure, multiplierRaw: value.Z.Value),
                right: ScaleByRawFactor(enclosure: zPreEnclosure, multiplierRaw: value.W.Value)
            );

            if (((crossEnclosure.Low - normalizeSlop) > BigInteger.Zero) || ((crossEnclosure.High + normalizeSlop) < BigInteger.Zero)) {
                return $"the interpolation at {amountRaw} produced ({value.Z.Value},{value.W.Value}), which does not align with the enclosure-exact pre-normalize direction (cross=[{crossEnclosure.Low},{crossEnclosure.High}] widened by {normalizeSlop})";
            }

            var dotWithPre = AddEnclosures(
                left: ScaleByRawFactor(enclosure: zPreEnclosure, multiplierRaw: value.Z.Value),
                right: ScaleByRawFactor(enclosure: wPreEnclosure, multiplierRaw: value.W.Value)
            );

            if ((dotWithPre.High + normalizeSlop) < BigInteger.Zero) { return $"the interpolation at {amountRaw} points away from the enclosure-exact pre-normalize direction"; }
        }

        var atZero = FixedQuaternion.Slerp(from: FixedQuaternion.Identity, to: quarter, amount: FixedQ4816.Zero);

        if (atZero != FixedQuaternion.Identity) { return "the interpolation at zero is not the normalized start"; }

        return null;
    }
    /// <summary>Proves the quaternion algebraic sanity checks: <c>q·conj(q)</c> is the
    /// identity within a small tolerance, rotation preserves length, and <see
    /// cref="FixedQuaternion.FromTo(FixedVector3,FixedVector3)"/>'s antiparallel fallback and zero-input poles hold
    /// exactly.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionAlgebraicSanitySurface() {
        foreach (var (axis, angleRaw) in AxisAngleLadder) {
            var q = FixedQuaternion.FromAxisAngle(axis: Vector3Of(lanes: axis), angle: Raw(value: angleRaw));
            var idProbe = (q * q.Conjugate());

            if (Math.Abs(value: (idProbe.W.Value - FixedQ4816.One.Value)) > 8L) { return $"q*conj(q) at axis=[{axis[0]},{axis[1]},{axis[2]}] angleRaw={angleRaw} has W={idProbe.W.Value}, expected near {FixedQ4816.One.Value}"; }
            if (Math.Abs(value: idProbe.X.Value) > 8L) { return $"q*conj(q) at axis=[{axis[0]},{axis[1]},{axis[2]}] angleRaw={angleRaw} has non-zero X={idProbe.X.Value}"; }

            var lengthProbe = q.Rotate(vector: new FixedVector3(X: FixedQ4816.FromInteger(value: 3L), Y: FixedQ4816.FromInteger(value: 4L), Z: FixedQ4816.Zero));

            if (Math.Abs(value: (lengthProbe.Length.Value - (5L * FixedQ4816.One.Value))) > 16L) { return $"rotation by axis=[{axis[0]},{axis[1]},{axis[2]}] angleRaw={angleRaw} did not preserve length: {lengthProbe.Length.Value}"; }
        }

        // All three arms of the least-aligned-axis antiparallel fallback, reached rather than believed reachable.
        foreach (var axis in Vector3EdgeTriples) {
            var start = Vector3Of(lanes: axis);
            var startDirection = start.Normalize();

            if (startDirection == FixedVector3.Zero) { continue; }

            var reversed = new FixedVector3(X: -startDirection.X, Y: -startDirection.Y, Z: -startDirection.Z);
            var half = FixedQuaternion.FromTo(from: startDirection, to: reversed);

            if (half.W != FixedQ4816.Zero) { return $"the antiparallel witness [{axis[0]},{axis[1]},{axis[2]}] returned a non-zero scalar lane"; }

            var image = half.Rotate(vector: startDirection);
            var cross = MaxAbsCross3(ix: image.X.Value, iy: image.Y.Value, iz: image.Z.Value, tx: reversed.X.Value, ty: reversed.Y.Value, tz: reversed.Z.Value);

            if (cross > AlignmentBound) { return $"the antiparallel witness [{axis[0]},{axis[1]},{axis[2]}] did not rotate onto the reversed direction"; }
        }

        if (FixedQuaternion.FromTo(from: FixedVector3.Zero, to: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)) != FixedQuaternion.Identity) {
            return "a zero start direction did not return the identity";
        }
        if (FixedQuaternion.FromTo(from: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), to: FixedVector3.Zero) != FixedQuaternion.Identity) {
            return "a zero end direction did not return the identity";
        }

        return null;
    }
    /// <summary>Proves <see cref="FixedQuaternion.FromTo(FixedVector3,FixedVector3)"/>'s DEFINING property — the
    /// rotor really takes the start direction onto the end direction — over the curated full-width triple set, by an
    /// exact <see cref="BigInteger"/> alignment inequality rather than any angle or trig comparison.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionFromToAlignmentSurface() {
        foreach (var from in Vector3EdgeTriples) {
            foreach (var to in Vector3EdgeTriples) {
                var fromVector = Vector3Of(lanes: from);
                var toVector = Vector3Of(lanes: to);
                var fromDirection = fromVector.Normalize();
                var toDirection = toVector.Normalize();

                if ((fromDirection == FixedVector3.Zero) || (toDirection == FixedVector3.Zero)) { continue; }

                var rotation = FixedQuaternion.FromTo(from: fromVector, to: toVector);
                var image = rotation.Rotate(vector: fromDirection);
                var cross = MaxAbsCross3(ix: image.X.Value, iy: image.Y.Value, iz: image.Z.Value, tx: toDirection.X.Value, ty: toDirection.Y.Value, tz: toDirection.Z.Value);
                var dot = (((((BigInteger)image.X.Value) * toDirection.X.Value) + (((BigInteger)image.Y.Value) * toDirection.Y.Value)) + (((BigInteger)image.Z.Value) * toDirection.Z.Value));

                if (cross > AlignmentBound) { return $"from=[{from[0]},{from[1]},{from[2]}] to=[{to[0]},{to[1]},{to[2]}]: the rotated start direction is off the end direction (cross={cross})"; }
                if (dot.Sign < 0) { return $"from=[{from[0]},{from[1]},{from[2]}] to=[{to[0]},{to[1]},{to[2]}]: the rotated start direction points away from the end direction"; }
            }
        }

        return null;
    }

    // Dual chain-rule ladder for f(x) = sqrt(x)·sin(x) + x²/(x+1): just the operand raws now — DualDerivativeSurface
    // derives its own expected derivative from Oracles.EncloseSinCos (sin/cos), Oracles.IntegerSquareRoot (the
    // SAME floor FixedQ4816.Sqrt is proven exact against elsewhere) and plain exact rational arithmetic for the
    // polynomial term, which needs no oracle at all. The exact spot checks below still need no ladder since their
    // derivatives are exact rationals.
    private static readonly long[] DualChainRuleXRaws = [65536L, 163840L, 262144L, 49152L, 393216L];

    /// <summary>Proves <see cref="FixedDual{TValue}"/>'s chain rule for <c>f(x) = sqrt(x)·sin(x) + x²/(x+1)</c>
    /// against an EXACT BigInteger derivative — Oracles.EncloseSinCos for the sin/cos terms, Oracles.IntegerSquareRoot
    /// for sqrt(x) (matching <see cref="FixedQ4816.Sqrt"/>'s own proven-exact floor bit for bit) and exact rational
    /// arithmetic for the polynomial term — plus two EXACT spot checks whose derivatives are exact rationals and so
    /// need no ladder at all: <c>d(x²)/dx</c> at <c>x=3</c> is exactly <c>6</c>, and <c>d(√x)/dx</c> at <c>x=4</c> is
    /// exactly <c>¼</c>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? DualDerivativeSurface() {
        var kernelUlp = GuardUlpUnits(denominator: 4, numerator: 3);

        foreach (var xRaw in DualChainRuleXRaws) {
            var x = FixedDual.Variable(value: Raw(value: xRaw));

            var (sinD, _) = FixedDual.SinCos(angle: x);
            var f = ((FixedDual.Sqrt(value: x) * sinD) + FixedDual.Divide(left: (x * x), right: (x + FixedDual.Constant(value: FixedQ4816.One))));

            var xBig = new BigInteger(value: xRaw);
            // Matches FixedQ4816.Sqrt(x) bit for bit: both are the exact floor of sqrt(xRaw << 16).
            var idealRoot = Oracles.IntegerSquareRoot(value: (xBig << FixedQ4816.FractionBitCount));
            var doubledRoot = (idealRoot * 2);
            var enclosure = Oracles.EncloseSinCos(guardBitCount: Oracles.GuardBitCount, raw: xRaw);

            // term1 = sin(x)/(2*idealRoot): an exact-interval division of the sine enclosure by the exact doubled
            // root. The sine enclosure sits at guard scale (raw · 2^32) but doubledRoot is a plain RAW magnitude
            // (scale 2^16); dividing them directly would land two 2^16 short of guard scale, so the numerator is
            // shifted left by FractionBitCount FIRST to bring the quotient back to the guard scale every other term
            // (and the final comparison) uses — the same compensation DivideToleranceByRawFactor applies to a
            // tolerance built the same way.
            var term1Low = FloorDivide(numerator: (enclosure.Sin.Low << FixedQ4816.FractionBitCount), positiveDenominator: doubledRoot);
            var term1High = CeilingDivide(numerator: (enclosure.Sin.High << FixedQ4816.FractionBitCount), positiveDenominator: doubledRoot);

            // term2 = idealRoot*cos(x): the cosine enclosure scaled by the exact root.
            var term2 = ScaleByRawFactor(enclosure: enclosure.Cos, multiplierRaw: ((long)idealRoot));

            // term3 = (x^2+2x)/(x+1)^2: an EXACT rational, no oracle involved at all — bounded directly at guard
            // scale by directed rounding on the one division.
            var denominatorRaw = (xBig + (1L << FixedQ4816.FractionBitCount));
            var denominatorSquared = (denominatorRaw * denominatorRaw);
            var numeratorReal = ((xBig * xBig) + ((2 * xBig) * (1L << FixedQ4816.FractionBitCount)));
            var numeratorScaled = (numeratorReal << (FixedQ4816.FractionBitCount + Oracles.GuardBitCount));
            var term3Low = FloorDivide(numerator: numeratorScaled, positiveDenominator: denominatorSquared);
            var term3High = CeilingDivide(numerator: numeratorScaled, positiveDenominator: denominatorSquared);

            var idealEnclosure = new Oracles.Enclosure(
                Low: ((term1Low + term2.Low) + term3Low),
                High: ((term1High + term2.High) + term3High)
            );

            // The kernel's own sin/cos envelope, propagated through the ONE further rounding each of term1 (a
            // division, attenuated by 2*idealRoot) and term2 (a multiply, scaled by idealRoot) carries, PLUS one
            // full raw ULP for each of the four remaining fused single-roundings the chain performs (sqrt(x)'s own
            // Dual division, the sqrt(x)*sinD product's fused Dual rounding, x*x's Real rounding propagated into the
            // quotient's numerator, and the quotient's own fused Dual rounding) — every one of the four is a flat
            // half-ULP contribution at its OWN output scale, never amplified, so a whole ULP each is a safe margin.
            var tolerance = ((DivideToleranceByRawFactor(divisorRawMagnitude: ((long)doubledRoot), toleranceUnits: kernelUlp)
                + ScaleToleranceByRawFactor(factorRawMagnitude: ((long)idealRoot), toleranceUnits: kernelUlp))
                + GuardUlpUnits(denominator: 1, numerator: 2));

            if (WithinGuardEnvelope(name: $"d/dx[sqrt(x)*sin(x)+x^2/(x+1)] at xRaw={xRaw}", subjectRaw: f.Dual.Value, enclosure: idealEnclosure, toleranceUnits: tolerance) is { } detail) {
                return detail;
            }
        }

        var three = FixedDual.Variable(value: FixedQ4816.FromInteger(value: 3L));

        if ((three * three).Dual.Value != (6L * FixedQ4816.One.Value)) { return "d(x^2)/dx at x=3 is not exactly 6"; }

        var four = FixedDual.Variable(value: FixedQ4816.FromInteger(value: 4L));

        if (FixedDual.Sqrt(value: four).Dual.Value != (FixedQ4816.One.Value >> 2)) { return "d(sqrt x)/dx at x=4 is not exactly 1/4"; }

        return null;
    }
    // ==== "vector2 wedge/dot" banner ======================================================================

    /// <summary>Proves <see cref="FixedQuaternion"/>'s Hamilton product, <see cref="FixedQuaternion.Dot"/> and <see
    /// cref="FixedQuaternion.Inverse"/> against exact <see cref="BigInteger"/> oracles over the curated full-width
    /// quaternion edge set.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionHamiltonProductDotInverseSurface() {
        foreach (var left in QuaternionEdgeQuads) {
            foreach (var right in QuaternionEdgeQuads) {
                var l = QuaternionOf(lanes: left);
                var r = QuaternionOf(lanes: right);
                var expectedProduct = HamiltonProductOracle(left: left, right: right);
                var product = (l * r);

                if ((product.X.Value != expectedProduct[0]) || (product.Y.Value != expectedProduct[1]) || (product.Z.Value != expectedProduct[2]) || (product.W.Value != expectedProduct[3])) {
                    return $"left=[{left[0]},{left[1]},{left[2]},{left[3]}] right=[{right[0]},{right[1]},{right[2]},{right[3]}]: the Hamilton product is ({product.X.Value},{product.Y.Value},{product.Z.Value},{product.W.Value}), expected ({expectedProduct[0]},{expectedProduct[1]},{expectedProduct[2]},{expectedProduct[3]})";
                }

                var expectedDot = RoundProductSumOracle(sum: ((((((BigInteger)left[0]) * right[0]) + (((BigInteger)left[1]) * right[1])) + (((BigInteger)left[2]) * right[2])) + (((BigInteger)left[3]) * right[3])));

                if (FixedQuaternion.Dot(left: l, right: r).Value != expectedDot) {
                    return $"left=[{left[0]},{left[1]},{left[2]},{left[3]}] right=[{right[0]},{right[1]},{right[2]},{right[3]}]: Dot is {FixedQuaternion.Dot(left: l, right: r).Value}, expected {expectedDot}";
                }
            }

            var squaredNorm = ((((((BigInteger)left[0]) * left[0]) + (((BigInteger)left[1]) * left[1])) + (((BigInteger)left[2]) * left[2])) + (((BigInteger)left[3]) * left[3]));

            if (squaredNorm.IsZero) { continue; }

            var subject = QuaternionOf(lanes: left);
            var expectedInverse = new long[] {
                RoundRatioQ16(numerator: -(((BigInteger)left[0]) << FixedQ4816.FractionBitCount), denominator: squaredNorm),
                RoundRatioQ16(numerator: -(((BigInteger)left[1]) << FixedQ4816.FractionBitCount), denominator: squaredNorm),
                RoundRatioQ16(numerator: -(((BigInteger)left[2]) << FixedQ4816.FractionBitCount), denominator: squaredNorm),
                RoundRatioQ16(numerator: (((BigInteger)left[3]) << FixedQ4816.FractionBitCount), denominator: squaredNorm),
            };
            var inverse = subject.Inverse();

            if ((inverse.X.Value != expectedInverse[0]) || (inverse.Y.Value != expectedInverse[1]) || (inverse.Z.Value != expectedInverse[2]) || (inverse.W.Value != expectedInverse[3])) {
                return $"left=[{left[0]},{left[1]},{left[2]},{left[3]}]: Inverse is ({inverse.X.Value},{inverse.Y.Value},{inverse.Z.Value},{inverse.W.Value}), expected ({expectedInverse[0]},{expectedInverse[1]},{expectedInverse[2]},{expectedInverse[3]})";
            }
        }

        return null;
    }

    // The DIRECTION-sensitive witness' tolerance has TWO genuinely different sources, discovered by probing an
    // extreme row before landing this check (see the leg): a FLAT few-raw budget from each schedule's OWN
    // roundings (Rotate's two, the sandwich's four across two Hamilton products — an absolute grid, independent of
    // the vector's magnitude), and a RELATIVE term from Normalize's own ±1-raw-per-lane direction accuracy
    // (quaternion.normalize-unit-direction): a rotor q with |q|² = 1+ε is EXACT algebra away from the sandwich
    // identity — expanding both v+2u×(u×v+w·v) (Rotate's formula) and q⊗(0,v)⊗conj(q) (the sandwich) shows they
    // agree in every term EXCEPT the coefficient of v itself, (1−2|u|²) versus (w²−|u|²), which differ by EXACTLY
    // −ε. That −ε·v difference is what a small, otherwise-harmless normalize imprecision turns into an ABSOLUTE
    // deviation that SCALES WITH the vector's own magnitude: bounded empirically (and by hand: 4 lanes × ≤1 raw
    // error each, ≤8/65536 relative) at roughly 16/65536, given room. Expressed as an exact BigInteger inequality
    // with NO square root: cross(actual,sandwich) = cross(actual−sandwich,sandwich) (cross(sandwich,sandwich)=0),
    // so |cross| ≤ |actual−sandwich|·|sandwich| ≤ (flatBound + εBound·|v|)·|sandwich| — squared and expanded, using
    // |v|²≈|sandwich|² (rotation preserves length) to keep every term a product of SQUARED norms.
    private const long RotateScheduleFlatBudget = 64L;
    private const long RotateScheduleEpsilonDenominator = 65536L;
    private const long RotateScheduleEpsilonNumerator = 16L;

    /// <summary>Proves <see cref="FixedQuaternion.Rotate"/> against a transcription of its own intermediate rounding
    /// schedule (<c>t = u×v + w·v</c> rounded once per component, then <c>u×t</c> rounded again, then <c>r + 2d</c>)
    /// over the curated full-width edge set — carriage evidence, proving Rotate reproduces its OWN documented
    /// schedule bit for bit — plus a DIRECTION-sensitive INDEPENDENT witness restricted to the NORMALIZED locus,
    /// where the sandwich identity <c>q⊗(0,v)⊗conj(q)</c> genuinely equals the rotation (see the leg for why the
    /// full-width, non-unit locus stays uncovered).</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionRotateScheduleTranscriptionSurface() {
        foreach (var left in QuaternionEdgeQuads) {
            foreach (var right in Vector3EdgeTriples) {
                var (lx, ly, lz, lw) = (left[0], left[1], left[2], left[3]);
                var (rx, ry, rz) = (right[0], right[1], right[2]);
                var rotor = QuaternionOf(lanes: left);
                var vector = Vector3Of(lanes: right);
                var tx = RoundProductSumOracle(sum: (((((BigInteger)ly) * rz) - (((BigInteger)lz) * ry)) + (((BigInteger)lw) * rx)));
                var ty = RoundProductSumOracle(sum: (((((BigInteger)lz) * rx) - (((BigInteger)lx) * rz)) + (((BigInteger)lw) * ry)));
                var tz = RoundProductSumOracle(sum: (((((BigInteger)lx) * ry) - (((BigInteger)ly) * rx)) + (((BigInteger)lw) * rz)));
                var dx = RoundProductSumOracle(sum: ((((BigInteger)ly) * tz) - (((BigInteger)lz) * ty)));
                var dy = RoundProductSumOracle(sum: ((((BigInteger)lz) * tx) - (((BigInteger)lx) * tz)));
                var dz = RoundProductSumOracle(sum: ((((BigInteger)lx) * ty) - (((BigInteger)ly) * tx)));
                var expected = new FixedVector3(
                    X: Raw(value: unchecked((rx + (dx << 1)))),
                    Y: Raw(value: unchecked((ry + (dy << 1)))),
                    Z: Raw(value: unchecked((rz + (dz << 1)))));
                var actual = rotor.Rotate(vector: vector);

                if (actual != expected) {
                    return $"rotor=[{left[0]},{left[1]},{left[2]},{left[3]}] vector=[{right[0]},{right[1]},{right[2]}]: Rotate is ({actual.X.Value},{actual.Y.Value},{actual.Z.Value}), expected ({expected.X.Value},{expected.Y.Value},{expected.Z.Value})";
                }
            }
        }

        // The DIRECTION-sensitive witness: restrict to NORMALIZED rotors (the locus the sandwich identity actually
        // holds on — off it, a previous agent confirmed empirically that the two diverge by billions of raw units,
        // because Rotate's shortcut is not scale-equivariant with the sandwich product there). FixedQuaternion.Normalize
        // is called on the SAME curated edge quads used above, so this still sweeps a wide variety of directions
        // without inventing a new operand source. ENVELOPE: the carrier's own long.MinValue/MaxValue vector rows are
        // NOT swept here — Vector3ModerateEdgeTriples stands in for Vector3EdgeTriples because forming the
        // INTERMEDIATE quaternion q⊗(0,v) genuinely overflows the 64-bit carrier at those extremes (confirmed by a
        // standalone probe: even for a unit q, the intermediate scalar lane wraps, corrupting the second product
        // into garbage unrelated to the true rotation) — a defect of the WITNESS TECHNIQUE forming an intermediate
        // that briefly needs more headroom than v's own scale, not of Rotate, which never forms that intermediate
        // and stays exact at those same rows in the transcription check above.
        foreach (var left in QuaternionEdgeQuads) {
            var rotor = QuaternionOf(lanes: left).Normalize();
            var q = new[] { rotor.X.Value, rotor.Y.Value, rotor.Z.Value, rotor.W.Value };
            var conjugateQ = new[] { -q[0], -q[1], -q[2], q[3] };

            foreach (var right in Vector3ModerateEdgeTriples) {
                var vector = Vector3Of(lanes: right);
                var vectorAsQuaternion = new[] { right[0], right[1], right[2], 0L };
                // q⊗(0,v)⊗conj(q): two Hamilton products, EACH independently transcribed by HamiltonProductOracle
                // (already established elsewhere in this file as classical evidence for the Hamilton product itself,
                // via quaternion.hamilton-product-dot-inverse-full-width) — a genuinely different rounding schedule
                // from Rotate's fused "t, then d, then v+2d" schedule: this one rounds a FOURTH (scalar) lane twice
                // that Rotate's schedule never forms at all.
                var sandwich = HamiltonProductOracle(left: HamiltonProductOracle(left: q, right: vectorAsQuaternion), right: conjugateQ);
                var sandwichSquaredNorm = (((((BigInteger)sandwich[0]) * sandwich[0]) + (((BigInteger)sandwich[1]) * sandwich[1])) + (((BigInteger)sandwich[2]) * sandwich[2]));
                var vectorSquaredNorm = (((((BigInteger)right[0]) * right[0]) + (((BigInteger)right[1]) * right[1])) + (((BigInteger)right[2]) * right[2]));
                var actual = rotor.Rotate(vector: vector);
                var cross = MaxAbsCross3(ix: actual.X.Value, iy: actual.Y.Value, iz: actual.Z.Value, tx: sandwich[0], ty: sandwich[1], tz: sandwich[2]);
                // |cross(actual, sandwich)| ≤ (flatBudget + (εNum/εDen)·|v|)·|sandwich| — squared, and multiplied
                // through by εDen² to stay in exact integers: cross²·εDen² ≤ sandwichSquaredNorm·(flatBudget²·εDen²
                // + εNum²·vectorSquaredNorm), using |v|²≈sandwichSquaredNorm's own scale for the cross terms (both
                // sides are within the SAME small ratio of |v|, so this stays a safe, not razor-tight, bound).
                var crossToleranceRight = (sandwichSquaredNorm * (((RotateScheduleFlatBudget * RotateScheduleFlatBudget) * (RotateScheduleEpsilonDenominator * RotateScheduleEpsilonDenominator)) + ((RotateScheduleEpsilonNumerator * RotateScheduleEpsilonNumerator) * vectorSquaredNorm)));

                if (((cross * cross) * (RotateScheduleEpsilonDenominator * RotateScheduleEpsilonDenominator)) > crossToleranceRight) {
                    return $"rotor=[{left[0]},{left[1]},{left[2]},{left[3]}] (normalized) vector=[{right[0]},{right[1]},{right[2]}]: Rotate is ({actual.X.Value},{actual.Y.Value},{actual.Z.Value}), off the sandwich-product direction ({sandwich[0]},{sandwich[1]},{sandwich[2]}) by more than the schedule-plus-normalize budget allows";
                }

                var dot = (((((BigInteger)actual.X.Value) * sandwich[0]) + (((BigInteger)actual.Y.Value) * sandwich[1])) + (((BigInteger)actual.Z.Value) * sandwich[2]));

                if (dot.Sign < 0) {
                    return $"rotor=[{left[0]},{left[1]},{left[2]},{left[3]}] (normalized) vector=[{right[0]},{right[1]},{right[2]}]: Rotate points away from the sandwich-product direction";
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="FixedVector2.Dot"/> and <see cref="FixedVector2.Wedge"/> against an exact <see
    /// cref="BigInteger"/> oracle over the full 9⁴ edge grid, the antisymmetry/symmetry algebraic identities, and the
    /// norm policy at the extremes (<see cref="FixedQ4816.Epsilon"/> length, full-range <see
    /// cref="FixedVector2.TryLength"/>, and <see cref="FixedVector2.TryLengthSquared"/> refusing where <see
    /// cref="FixedVector2.Length"/> saturates).</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Vector2FullWidthOracleAndIdentitiesSurface() {
        var tinyVector = new FixedVector2(X: FixedQ4816.Epsilon, Y: FixedQ4816.Epsilon);

        if (tinyVector.Length != FixedQ4816.Epsilon) { return "an epsilon-component vector did not report epsilon length"; }

        var fullRangeComponent = FixedQ4816.FromRawBits(value: (1L << 40));
        var fullRangeVector = new FixedVector2(X: fullRangeComponent, Y: FixedQ4816.Zero);

        if (!fullRangeVector.TryLength(length: out var fullRangeLength) || (fullRangeLength != fullRangeComponent)) { return "a full-range single-axis vector did not report its exact axis-aligned length"; }
        if (fullRangeVector.TryLengthSquared(squaredLength: out _)) { return "TryLengthSquared succeeded where the squared length saturates"; }
        if (fullRangeVector.LengthSquared != FixedQ4816.MaxValue) { return "LengthSquared did not saturate to MaxValue"; }

        foreach (var ax in LongEdges9) {
            foreach (var ay in LongEdges9) {
                foreach (var bx in LongEdges9) {
                    foreach (var by in LongEdges9) {
                        var a = new FixedVector2(X: Raw(value: ax), Y: Raw(value: ay));
                        var b = new FixedVector2(X: Raw(value: bx), Y: Raw(value: by));
                        var expectedDot = RoundProductSumOracle(sum: ((((BigInteger)ax) * bx) + (((BigInteger)ay) * by)));
                        var expectedWedge = RoundProductSumOracle(sum: ((((BigInteger)ax) * by) - (((BigInteger)ay) * bx)));

                        if (FixedVector2.Dot(left: a, right: b).Value != expectedDot) { return $"Dot(({ax},{ay}),({bx},{by})) is {FixedVector2.Dot(left: a, right: b).Value}, expected {expectedDot}"; }
                        if (FixedVector2.Wedge(left: a, right: b).Value != expectedWedge) { return $"Wedge(({ax},{ay}),({bx},{by})) is {FixedVector2.Wedge(left: a, right: b).Value}, expected {expectedWedge}"; }
                        if (FixedVector2.Wedge(left: a, right: a).Value != 0L) { return $"Wedge(a,a) at ({ax},{ay}) is not zero"; }
                        if (FixedVector2.Wedge(left: a, right: b).Value != -FixedVector2.Wedge(left: b, right: a).Value) { return $"Wedge is not antisymmetric at ({ax},{ay}),({bx},{by})"; }
                        if (FixedVector2.Dot(left: a, right: b).Value != FixedVector2.Dot(left: b, right: a).Value) { return $"Dot is not symmetric at ({ax},{ay}),({bx},{by})"; }
                    }
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="FixedVector3.Dot"/> and <see cref="FixedVector3.Cross"/> against an exact <see
    /// cref="BigInteger"/> oracle over the curated full-width triple set, antisymmetry of the cross product, and the
    /// length-overflow policy: <see cref="FixedVector3.TryLength"/> is exact where representable and refuses (with
    /// <see cref="FixedVector3.Length"/> saturating) where it is not, and <see cref="FixedVector3.TryLengthSquared"/>
    /// refuses independently where the SQUARED length overflows first.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Vector3DotCrossOracleSurface() {
        foreach (var a in Vector3EdgeTriples) {
            foreach (var b in Vector3EdgeTriples) {
                var av = Vector3Of(lanes: a);
                var bv = Vector3Of(lanes: b);
                var expectedDot = RoundProductSumOracle(sum: (((((BigInteger)a[0]) * b[0]) + (((BigInteger)a[1]) * b[1])) + (((BigInteger)a[2]) * b[2])));
                var expectedCross = new FixedVector3(
                    X: Raw(value: RoundProductSumOracle(sum: ((((BigInteger)a[1]) * b[2]) - (((BigInteger)a[2]) * b[1])))),
                    Y: Raw(value: RoundProductSumOracle(sum: ((((BigInteger)a[2]) * b[0]) - (((BigInteger)a[0]) * b[2])))),
                    Z: Raw(value: RoundProductSumOracle(sum: ((((BigInteger)a[0]) * b[1]) - (((BigInteger)a[1]) * b[0])))));
                var cross = FixedVector3.Cross(left: av, right: bv);

                if (FixedVector3.Dot(left: av, right: bv).Value != expectedDot) { return $"a=[{a[0]},{a[1]},{a[2]}] b=[{b[0]},{b[1]},{b[2]}]: Dot is {FixedVector3.Dot(left: av, right: bv).Value}, expected {expectedDot}"; }
                if (cross != expectedCross) { return $"a=[{a[0]},{a[1]},{a[2]}] b=[{b[0]},{b[1]},{b[2]}]: Cross is ({cross.X.Value},{cross.Y.Value},{cross.Z.Value}), expected ({expectedCross.X.Value},{expectedCross.Y.Value},{expectedCross.Z.Value})"; }
                if (cross != -FixedVector3.Cross(left: bv, right: av)) { return $"a=[{a[0]},{a[1]},{a[2]}] b=[{b[0]},{b[1]},{b[2]}]: Cross is not antisymmetric"; }
            }
        }

        var lengthAtMax = new FixedVector3(X: FixedQ4816.MaxValue, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var lengthAboveMax = new FixedVector3(X: FixedQ4816.MaxValue, Y: FixedQ4816.FromRawBits(value: (1L << 32)), Z: FixedQ4816.Zero);
        var squaredLengthFits = new FixedVector3(X: FixedQ4816.FromRawBits(value: (1L << 39)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var squaredLengthOverflows = new FixedVector3(X: FixedQ4816.FromRawBits(value: (1L << 40)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);

        if (!lengthAtMax.TryLength(length: out var exactMaxLength) || (exactMaxLength != FixedQ4816.MaxValue)) { return "an axis-aligned vector at exactly MaxValue did not report its exact length"; }
        if (lengthAboveMax.TryLength(length: out _) || (lengthAboveMax.Length != FixedQ4816.MaxValue)) { return "a vector whose length exceeds MaxValue did not refuse TryLength and saturate Length"; }
        if (!squaredLengthFits.TryLengthSquared(squaredLength: out _)) { return "a vector whose squared length fits refused TryLengthSquared"; }
        if (squaredLengthOverflows.TryLengthSquared(squaredLength: out _) || (squaredLengthOverflows.LengthSquared != FixedQ4816.MaxValue)) { return "a vector whose squared length overflows did not refuse TryLengthSquared and saturate LengthSquared"; }

        return null;
    }
    // ==== "complex / rigid transform" banner ==============================================================

    /// <summary>Proves <see cref="FixedComplex"/> division and multiplication against exact <see
    /// cref="BigInteger"/> oracles over the full 10⁴ edge grid, and that dividing by the additive identity throws
    /// <see cref="DivideByZeroException"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ComplexDivisionMultiplyFullWidthOracleSurface() {
        foreach (var ar in ComplexEdges10) {
            foreach (var ai in ComplexEdges10) {
                foreach (var br in ComplexEdges10) {
                    foreach (var bi in ComplexEdges10) {
                        var a = new FixedComplex(Real: Raw(value: ar), Imaginary: Raw(value: ai));
                        var b = new FixedComplex(Real: Raw(value: br), Imaginary: Raw(value: bi));
                        var expectedProductReal = RoundProductSumOracle(sum: ((((BigInteger)ar) * br) - (((BigInteger)ai) * bi)));
                        var expectedProductImaginary = RoundProductSumOracle(sum: ((((BigInteger)ar) * bi) + (((BigInteger)ai) * br)));
                        var product = (a * b);

                        if ((product.Real.Value != expectedProductReal) || (product.Imaginary.Value != expectedProductImaginary)) {
                            return $"({ar},{ai})*({br},{bi}) is ({product.Real.Value},{product.Imaginary.Value}), expected ({expectedProductReal},{expectedProductImaginary})";
                        }

                        if ((br | bi) == 0L) { continue; }

                        var denominator = ((((BigInteger)br) * br) + (((BigInteger)bi) * bi));
                        var expectedQuotientReal = RoundRatioQ16(denominator: denominator, numerator: ((((BigInteger)ar) * br) + (((BigInteger)ai) * bi)));
                        var expectedQuotientImaginary = RoundRatioQ16(denominator: denominator, numerator: ((((BigInteger)ai) * br) - (((BigInteger)ar) * bi)));
                        var quotient = (a / b);

                        if ((quotient.Real.Value != expectedQuotientReal) || (quotient.Imaginary.Value != expectedQuotientImaginary)) {
                            return $"({ar},{ai})/({br},{bi}) is ({quotient.Real.Value},{quotient.Imaginary.Value}), expected ({expectedQuotientReal},{expectedQuotientImaginary})";
                        }
                    }
                }
            }
        }

        var threw = false;

        try {
            _ = (FixedComplex.MultiplicativeIdentity / FixedComplex.AdditiveIdentity);
        } catch (DivideByZeroException) {
            threw = true;
        }

        if (!threw) { return "dividing by the additive identity did not throw DivideByZeroException"; }

        return null;
    }
    /// <summary>Proves <see cref="FixedComplex.FromTo"/>'s DEFINING property over the curated full-width edge set by
    /// an exact <see cref="BigInteger"/> alignment inequality, and the two scale-safety poles: negation commutes with
    /// <see cref="FixedComplex.Normalize"/>, and an epsilon-component complex normalizes to the multiplicative
    /// identity.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ComplexFromToAndScaleSafetySurface() {
        long[] edges = [long.MinValue, -65536L, -1L, 1L, 65536L, long.MaxValue];

        foreach (var fx in edges) {
            foreach (var fy in edges) {
                foreach (var tx in edges) {
                    foreach (var ty in edges) {
                        var from = new FixedVector2(X: Raw(value: fx), Y: Raw(value: fy));
                        var to = new FixedVector2(X: Raw(value: tx), Y: Raw(value: ty));

                        if ((from == FixedVector2.Zero) || (to == FixedVector2.Zero)) { continue; }

                        var rotor = FixedComplex.FromTo(from: from, to: to);
                        var fromLength = from.Length;
                        var toLength = to.Length;
                        var fromDirection = new FixedVector2(X: (from.X / fromLength), Y: (from.Y / fromLength));
                        var toDirection = new FixedVector2(X: (to.X / toLength), Y: (to.Y / toLength));
                        var image = rotor.Rotate(vector: fromDirection);
                        var cross = BigInteger.Abs(value: (((((BigInteger)image.X.Value) * toDirection.Y.Value) - (((BigInteger)image.Y.Value) * toDirection.X.Value))));
                        var dot = (((((BigInteger)image.X.Value) * toDirection.X.Value) + (((BigInteger)image.Y.Value) * toDirection.Y.Value)));

                        if (cross > AlignmentBound) { return $"from=({fx},{fy}) to=({tx},{ty}): the rotated start direction is off the end direction (cross={cross})"; }
                        if (dot.Sign < 0) { return $"from=({fx},{fy}) to=({tx},{ty}): the rotated start direction points away from the end direction"; }
                    }
                }
            }
        }

        var epsilonAxis = new FixedVector2(X: FixedQ4816.Epsilon, Y: FixedQ4816.Zero);

        if (FixedComplex.FromTo(from: epsilonAxis, to: -epsilonAxis) != new FixedComplex(Real: FixedQ4816.NegativeOne, Imaginary: FixedQ4816.Zero)) {
            return "FromTo of exact opposite epsilon-scale directions did not return exactly -1";
        }
        if (new FixedComplex(Real: FixedQ4816.Epsilon, Imaginary: FixedQ4816.Zero).Normalize() != FixedComplex.MultiplicativeIdentity) {
            return "an epsilon-component complex did not normalize to the multiplicative identity";
        }

        var extreme = new FixedComplex(Real: FixedQ4816.FromRawBits(value: long.MaxValue), Imaginary: FixedQ4816.FromRawBits(value: (1L << 46)));

        if ((-extreme).Normalize() != -(extreme.Normalize())) { return "negation does not commute with Normalize at the carrier's extreme"; }

        return null;
    }
    /// <summary>Proves <see cref="FixedVector3.Normalize"/> and <see cref="FixedQuaternion.Normalize"/> against a
    /// <see cref="BigInteger"/> bisection oracle over the curated full-width set, gated at 1 raw ULP, plus the
    /// four-square carry at the all-<see cref="FixedQ4816.MinValue"/> quaternion.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? NormalizeFullWidthOracleSurface() {
        foreach (var triple in Vector3EdgeTriples) {
            var vector = Vector3Of(lanes: triple).Normalize();
            var expected = NormalizeOracle(values: triple);

            if (Math.Abs(value: (vector.X.Value - expected[0])) > 1L) { return $"X of Normalize([{triple[0]},{triple[1]},{triple[2]}]) is {vector.X.Value}, expected {expected[0]}"; }
            if (Math.Abs(value: (vector.Y.Value - expected[1])) > 1L) { return $"Y of Normalize([{triple[0]},{triple[1]},{triple[2]}]) is {vector.Y.Value}, expected {expected[1]}"; }
            if (Math.Abs(value: (vector.Z.Value - expected[2])) > 1L) { return $"Z of Normalize([{triple[0]},{triple[1]},{triple[2]}]) is {vector.Z.Value}, expected {expected[2]}"; }
        }

        foreach (var quad in QuaternionEdgeQuads) {
            var squaredNorm = ((((((BigInteger)quad[0]) * quad[0]) + (((BigInteger)quad[1]) * quad[1])) + (((BigInteger)quad[2]) * quad[2])) + (((BigInteger)quad[3]) * quad[3]));

            if (squaredNorm.IsZero) { continue; }

            var quaternion = QuaternionOf(lanes: quad).Normalize();
            var expected = NormalizeOracle(values: quad);

            if (Math.Abs(value: (quaternion.X.Value - expected[0])) > 1L) { return $"X of quaternion Normalize([{quad[0]},{quad[1]},{quad[2]},{quad[3]}]) is {quaternion.X.Value}, expected {expected[0]}"; }
            if (Math.Abs(value: (quaternion.Y.Value - expected[1])) > 1L) { return $"Y of quaternion Normalize([{quad[0]},{quad[1]},{quad[2]},{quad[3]}]) is {quaternion.Y.Value}, expected {expected[1]}"; }
            if (Math.Abs(value: (quaternion.Z.Value - expected[2])) > 1L) { return $"Z of quaternion Normalize([{quad[0]},{quad[1]},{quad[2]},{quad[3]}]) is {quaternion.Z.Value}, expected {expected[2]}"; }
            if (Math.Abs(value: (quaternion.W.Value - expected[3])) > 1L) { return $"W of quaternion Normalize([{quad[0]},{quad[1]},{quad[2]},{quad[3]}]) is {quaternion.W.Value}, expected {expected[3]}"; }
        }

        // The all-MinValue quaternion: −2⁶³ has no positive counterpart in two's complement, so the four-square sum
        // must carry through the wider carrier rather than overflow, and TryLength must refuse (the exact squared
        // norm exceeds the signed 128-bit carrier this member accepts).
        var allMinimum = new FixedQuaternion(X: FixedQ4816.MinValue, Y: FixedQ4816.MinValue, Z: FixedQ4816.MinValue, W: FixedQ4816.MinValue);
        var normalizedAllMinimum = allMinimum.Normalize();
        var expectedAllMinimum = FixedQ4816.FromRawBits(value: -32768L);

        if ((normalizedAllMinimum.X != expectedAllMinimum) || (normalizedAllMinimum.Y != expectedAllMinimum) || (normalizedAllMinimum.Z != expectedAllMinimum) || (normalizedAllMinimum.W != expectedAllMinimum)) {
            return $"the all-MinValue quaternion normalized to ({normalizedAllMinimum.X.Value},{normalizedAllMinimum.Y.Value},{normalizedAllMinimum.Z.Value},{normalizedAllMinimum.W.Value}), expected all lanes {expectedAllMinimum.Value}";
        }
        if (allMinimum.TryLength(length: out _)) { return "the all-MinValue quaternion did not refuse TryLength"; }

        return null;
    }

    // Rotations built at the SAME hand-derived AxisAngleLadder angles used above (Z quarter-turn raw 102944, X
    // third-turn raw 68628, a tilted two-thirds-turn raw 137256), so this claim needs no ladder of its own — every
    // comparison below is a self-consistency round trip, none against an external reference.
    private static readonly (long[] Axis, long AngleRaw)[] RigidLadderRotations = [
        ([0L, 0L, 65536L], 102944L), ([65536L, 0L, 0L], 68628L), ([37837L, 37837L, 37837L], 137256L),
    ];
    private static readonly long[][] RigidLadderTranslations = [
        [131072L, -196608L, 32768L], [-458752L, 262144L, 65536L], [0L, 655360L, -655360L],
    ];

    /// <summary>Proves <see cref="FixedRigidTransform"/>'s round trips over the ladder-derived rotation/translation
    /// pairs: composition matches sequential application, translation extraction round-trips, the inverse undoes the
    /// transform, the screw exp/log round-trips, pure translation is the screw's exact pole, and <see
    /// cref="FixedRigidTransform.ScLerp"/>'s endpoints return the operands.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RigidTransformRoundTripSurface() {
        var probe = new FixedVector3(X: FixedQ4816.FromInteger(value: 3L), Y: FixedQ4816.FromInteger(value: -2L), Z: FixedQ4816.FromInteger(value: 1L));
        var transforms = new FixedRigidTransform[RigidLadderRotations.Length];

        for (var i = 0; (i < RigidLadderRotations.Length); ++i) {
            var (axis, angleRaw) = RigidLadderRotations[i];
            var rotation = FixedQuaternion.FromAxisAngle(axis: Vector3Of(lanes: axis), angle: Raw(value: angleRaw));
            var translation = Vector3Of(lanes: RigidLadderTranslations[i]);

            transforms[i] = FixedRigidTransform.FromRotationTranslation(rotation: rotation, translation: translation);

            var extracted = transforms[i].Translation;

            if (Math.Abs(value: (extracted.X.Value - translation.X.Value)) > 32L) { return $"row {i}: Translation X is {extracted.X.Value}, expected near {translation.X.Value}"; }
            if (Math.Abs(value: (extracted.Y.Value - translation.Y.Value)) > 32L) { return $"row {i}: Translation Y is {extracted.Y.Value}, expected near {translation.Y.Value}"; }
            if (Math.Abs(value: (extracted.Z.Value - translation.Z.Value)) > 32L) { return $"row {i}: Translation Z is {extracted.Z.Value}, expected near {translation.Z.Value}"; }

            var back = transforms[i].Inverse().TransformPoint(point: transforms[i].TransformPoint(point: probe));

            if (Math.Abs(value: (back.X.Value - probe.X.Value)) > 64L) { return $"row {i}: inverse round trip X is {back.X.Value}, expected near {probe.X.Value}"; }
            if (Math.Abs(value: (back.Y.Value - probe.Y.Value)) > 64L) { return $"row {i}: inverse round trip Y is {back.Y.Value}, expected near {probe.Y.Value}"; }
            if (Math.Abs(value: (back.Z.Value - probe.Z.Value)) > 64L) { return $"row {i}: inverse round trip Z is {back.Z.Value}, expected near {probe.Z.Value}"; }

            var (screwReal, screwDual) = transforms[i].Log();
            var expBack = FixedRigidTransform.Exp(dual: screwDual, real: screwReal);
            var realDiff = Math.Max(val1: Math.Max(val1: Math.Abs(value: (expBack.Value.Real.X.Value - transforms[i].Value.Real.X.Value)), val2: Math.Abs(value: (expBack.Value.Real.Y.Value - transforms[i].Value.Real.Y.Value))),
                val2: Math.Max(val1: Math.Abs(value: (expBack.Value.Real.Z.Value - transforms[i].Value.Real.Z.Value)), val2: Math.Abs(value: (expBack.Value.Real.W.Value - transforms[i].Value.Real.W.Value))));
            var dualDiff = Math.Max(val1: Math.Max(val1: Math.Abs(value: (expBack.Value.Dual.X.Value - transforms[i].Value.Dual.X.Value)), val2: Math.Abs(value: (expBack.Value.Dual.Y.Value - transforms[i].Value.Dual.Y.Value))),
                val2: Math.Max(val1: Math.Abs(value: (expBack.Value.Dual.Z.Value - transforms[i].Value.Dual.Z.Value)), val2: Math.Abs(value: (expBack.Value.Dual.W.Value - transforms[i].Value.Dual.W.Value))));

            if (realDiff > 16L) { return $"row {i}: the screw exp/log round trip's rotation part differs by {realDiff}"; }
            if (dualDiff > 16L) { return $"row {i}: the screw exp/log round trip's translation part differs by {dualDiff}"; }
        }

        for (var i = 0; (i < transforms.Length); ++i) {
            for (var j = 0; (j < transforms.Length); ++j) {
                var composed = (transforms[i] * transforms[j]).TransformPoint(point: probe);
                var sequential = transforms[i].TransformPoint(point: transforms[j].TransformPoint(point: probe));
                var diff = Math.Max(val1: Math.Abs(value: (composed.X.Value - sequential.X.Value)), val2: Math.Max(val1: Math.Abs(value: (composed.Y.Value - sequential.Y.Value)), val2: Math.Abs(value: (composed.Z.Value - sequential.Z.Value))));

                if (diff > 64L) { return $"rows ({i},{j}): (A∘B)(p) differs from A(B(p)) by {diff}"; }
            }
        }

        // Pure translation is the screw's exact pole: Log has no rotation part and Exp reproduces it bit-for-bit.
        var pureTranslation = FixedRigidTransform.FromRotationTranslation(
            rotation: FixedQuaternion.Identity,
            translation: new FixedVector3(X: FixedQ4816.FromInteger(value: 2L), Y: FixedQ4816.FromInteger(value: -3L), Z: FixedQ4816.FromInteger(value: 1L)));

        var (pureReal, pureDual) = pureTranslation.Log();

        if (pureReal != FixedVector3.Zero) { return "pure translation's Log has a non-zero rotation part"; }
        if (FixedRigidTransform.Exp(dual: pureDual, real: pureReal) != pureTranslation) { return "pure translation did not round-trip through Exp(Log(...)) bit-for-bit"; }

        // ScLerp endpoints return the operands.
        var atZero = FixedRigidTransform.ScLerp(from: transforms[0], to: transforms[1], amount: FixedQ4816.Zero).TransformPoint(point: probe);
        var atOne = FixedRigidTransform.ScLerp(from: transforms[0], to: transforms[1], amount: FixedQ4816.One).TransformPoint(point: probe);
        var expectedZero = transforms[0].TransformPoint(point: probe);
        var expectedOne = transforms[1].TransformPoint(point: probe);

        if (Math.Abs(value: (atZero.X.Value - expectedZero.X.Value)) > 32L) { return "ScLerp at amount zero did not return the start transform"; }
        if (Math.Abs(value: (atOne.X.Value - expectedOne.X.Value)) > 32L) { return "ScLerp at amount one did not return the end transform"; }

        return null;
    }
}
