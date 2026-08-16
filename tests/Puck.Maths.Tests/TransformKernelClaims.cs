using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// The quaternion, dual and sin/cos accuracy statements: the transform wing's transcendental and normalizer kernels
/// gated over their full carriers.
/// </summary>
/// <remarks>
/// <para>
/// Two of these are the ONLY gate over a shipped member: <c>FixedQ4816.SinCosRaw</c> — the full-unsigned-width
/// entry point <see cref="FixedQuaternion.FromAxisAngle"/>, <see cref="FixedQuaternion.Exp"/> and
/// <see cref="FixedRigidTransform.Exp"/> all reach their sine and cosine through — and
/// <c>FixedVectorMath.TryNormalizeWithMagnitude</c>, the one-pass axis-and-norm those same three call first. Both are
/// internal, so <c>coverage-manifest.json</c> cannot name them and the ratchet cannot notice their gate disappearing;
/// the laws here are what notices instead.
/// </para>
/// <para>
/// Neither is measured against a <see cref="double"/> reference — no floating-point arithmetic may enter law logic —
/// so both statements are made over exact integers: an enclosure from <c>Oracles.EncloseSinCos</c> carried across the
/// angle-addition identity for the transcendental, and <c>Oracles.NearestIntegerRoot</c> /
/// <c>Oracles.IdealUnitVector</c> for the normalizer. The magnitude statement is therefore an exact integer identity
/// rather than a measured ULP bound.
/// </para>
/// </remarks>
internal static class TransformKernelClaims {
    private const long RawOne = 65536L;
    // The guard bits every enclosure below carries under the Q48.16 grid, so a sub-ULP envelope is an integer
    // comparison rather than a rounding argument.
    private const int GuardBitCount = Oracles.GuardBitCount;
    // The flat part of the envelope, in raw Q16 units: the Q60 polynomial's own 0.51 ULP plus the half ULP the Q60→Q16
    // narrowing costs, rounded up to one raw, plus one raw for the two interval multiplies the angle-addition
    // reference performs above the seam. Everything else in the envelope is PROPORTIONAL to the angle and is derived
    // in SinCosRawEnvelope.
    private const int SinCosRawFlatBudget = 2;
    // The mixed draws each sweep takes past its hand-listed ladder. Every draw is a pure function of a running
    // counter (SplitMix64), never System.Random and never the wall clock, so the operand stream is a fact of the
    // source rather than of the run.
    private const int SinCosRawDrawCount = 224;
    private const int NormalizeDrawCount = 512;
    // The Exhaustive siblings' own volume, written out inline because an Exhaustive case must take its own basis
    // rather than consume a Domain. 411775 raw is one whole turn (2π·2¹⁶), swept densely; the draw counts then carry
    // the same statements across the rest of the carrier.
    private const ulong DenseTurnLimit = 411775UL;
    private const int NormalizeSweepDrawCount = 200_000;
    private const int SinCosRawSweepDrawCount = 250_000;

    // The unsigned angle ladder: the exact pole, the single-raw quanta, one raw either side of every gate the turn
    // reduction and the polynomial fold branch on, the SIGNED/UNSIGNED SEAM at 2^63 — which is the whole reason this
    // entry point exists beside the public SinCos — and both carrier extremes. 411775 is the last raw of one full
    // turn, where the dense sweep ends.
    private static readonly ulong[] UnsignedAngleLadder = [
        0UL, 1UL, 2UL, 3UL,
        32768UL, 65535UL, 65536UL, 65537UL,
        411774UL, 411775UL, 411776UL,
        ((1UL << 31) - 1UL), (1UL << 31), ((1UL << 31) + 1UL),
        ((1UL << 47) - 1UL), (1UL << 47), ((1UL << 47) + 1UL),
        ((1UL << 62) - 1UL), (1UL << 62), ((1UL << 62) + 1UL),
        ((1UL << 63) - 1UL), (1UL << 63), ((1UL << 63) + 1UL),
        ((1UL << 63) + (1UL << 62)),
        (ulong.MaxValue - 1UL), ulong.MaxValue,
    ];
    // The direction ladder. Rows 0-4 are the quanta and the unit axes; row 5 is the 3-4-5 triple, whose norm is EXACT
    // (25·2^32 is a perfect square) so the nearest-root statement runs with no rounding anywhere; rows 9-11 straddle
    // the narrow/wide branch gate at a 2^48 norm; row 12 is the hand-derived witness that lands INSIDE the 2^32-wide
    // band below 2^96 the narrow path deliberately excludes (its squared sum is 2^96 − 2^25 + 2); the last three are
    // the carrier's own extremes, where the magnitude leaves the signed Q48.16 range altogether.
    private static readonly long[][] DirectionLadder = [
        [1L, 0L, 0L],
        [0L, 1L, 0L],
        [0L, 0L, 1L],
        [1L, 1L, 1L],
        [-1L, -1L, -1L],
        [(3L * RawOne), (4L * RawOne), 0L],
        [RawOne, 0L, 0L],
        [-3L, 5L, -7L],
        [(1L << 31), (1L << 31), (1L << 31)],
        [((1L << 48) - 1L), 0L, 0L],
        [(1L << 48), 0L, 0L],
        [((1L << 48) + 1L), 0L, 0L],
        [((1L << 48) - 1L), (1L << 24), ((1L << 24) - 1L)],
        [(1L << 47), -(1L << 47), (1L << 47)],
        [long.MaxValue, 0L, 0L],
        [long.MinValue, 0L, 0L],
        [long.MaxValue, long.MinValue, long.MaxValue],
        [long.MinValue, long.MinValue, long.MinValue],
    ];

    /// <summary>
    /// <c>FixedQ4816.SinCosRaw</c> at the FULL UNSIGNED WIDTH, against an independent enclosure.
    /// </summary>
    /// <returns>The counterexample, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The reference never calls the subject and never forms a <see cref="double"/>: <c>Oracles.EncloseSinCos</c> is an
    /// alternating Taylor series over a Machin-derived π, reduced IN RADIANS at three hundred and eighty-four working
    /// bits, and it is carried past the signed carrier by the angle-addition identity — the angle is split at
    /// <c>u = ⌊u/2⌋ + ⌈u/2⌉</c>, both halves being representable longs, and the two enclosures are combined by
    /// <c>sin(a+b) = sin a·cos b + cos a·sin b</c> and <c>cos(a+b) = cos a·cos b − sin a·sin b</c> in interval
    /// arithmetic. That is the ONE thing the public <c>SinCos</c> law cannot say, and it is exactly the regime
    /// <c>SinCosRaw</c> exists for.
    /// </remarks>
    internal static string? SinCosRawFullUnsignedWidthSurface() {
        var (poleSin, poleCos) = FixedQ4816.SinCosRaw(rawAngle: 0UL);

        if ((poleSin.Value != 0L) || (poleCos.Value != RawOne)) {
            return $"SinCosRaw(0) returned ({poleSin.Value}, {poleCos.Value}) rather than the exact pole (0, {RawOne})";
        }

        var reachedSignedHalf = false;
        var reachedUnsignedHalf = false;
        var counter = 0UL;

        for (var index = 0; (index < (UnsignedAngleLadder.Length + SinCosRawDrawCount)); ++index) {
            var rawAngle = ((index < UnsignedAngleLadder.Length)
                ? UnsignedAngleLadder[index]
                : MixIndex(index: ++counter));

            if (rawAngle < (1UL << 63)) {
                reachedSignedHalf = true;
            } else {
                reachedUnsignedHalf = true;
            }

            var (sin, cos) = FixedQ4816.SinCosRaw(rawAngle: rawAngle);
            var (sinEnclosure, cosEnclosure) = EncloseUnsignedAngle(rawAngle: rawAngle);
            var envelope = SinCosRawEnvelope(rawAngle: rawAngle);
            var sinGap = Deviation(enclosure: sinEnclosure, raw: sin.Value);

            if (sinGap > envelope) {
                return $"SinCosRaw({rawAngle}).Sin is {sin.Value}, which sits {sinGap} raw outside the enclosure [{sinEnclosure.Low}, {sinEnclosure.High}] (envelope {envelope})";
            }

            var cosGap = Deviation(enclosure: cosEnclosure, raw: cos.Value);

            if (cosGap > envelope) {
                return $"SinCosRaw({rawAngle}).Cos is {cos.Value}, which sits {cosGap} raw outside the enclosure [{cosEnclosure.Low}, {cosEnclosure.High}] (envelope {envelope})";
            }

            // Below the seam the two entry points denote the SAME angle, so they must return the same bits: the
            // unsigned overload is a widening of the signed one, not a second approximation of it.
            if (rawAngle < (1UL << 63)) {
                var (signedSin, signedCos) = FixedQ4816.SinCos(angle: FixedQ4816.FromRawBits(value: ((long)rawAngle)));

                if ((signedSin.Value != sin.Value) || (signedCos.Value != cos.Value)) {
                    return $"at raw {rawAngle}, below the signed seam, SinCosRaw returned ({sin.Value}, {cos.Value}) but SinCos returned ({signedSin.Value}, {signedCos.Value})";
                }
            }
        }

        if (!reachedSignedHalf) {
            return "no swept angle fell below the 2^63 seam, so the shared-with-SinCos statement never ran";
        }

        if (!reachedUnsignedHalf) {
            return "no swept angle reached the upper unsigned half, so the statement this case exists to make never ran";
        }

        return null;
    }
    /// <summary>
    /// <c>FixedVectorMath.TryNormalizeWithMagnitude</c> against exact integer references, over the full signed carrier.
    /// </summary>
    /// <returns>The counterexample, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The magnitude statement is an EXACT identity, not a tolerance: whichever of the two branches runs, the returned
    /// raw magnitude is precisely <c>Oracles.NearestIntegerRoot</c> of the exact <see cref="BigInteger"/> sum of squares.
    /// The direction statement is <c>Oracles.IdealUnitVector</c> to within one raw per lane, the same bound
    /// <c>vector.normalize-vs-ideal-and-staged</c> proves for the public normalizer, reached here for the pipeline
    /// that shares none of its preconditioning.
    /// </remarks>
    internal static string? NormalizeWithMagnitudeFullUnsignedWidthSurface() {
        if (FixedVectorMath.TryNormalizeWithMagnitude(
            x: 0L,
            y: 0L,
            z: 0L,
            unitX: out var refusedX,
            unitY: out var refusedY,
            unitZ: out var refusedZ,
            rawMagnitude: out var refusedMagnitude
        )) {
            return "the zero direction was normalized rather than refused";
        }

        if ((refusedX != 0L) || (refusedY != 0L) || (refusedZ != 0L) || (refusedMagnitude != 0UL)) {
            return $"the refused zero direction left ({refusedX}, {refusedY}, {refusedZ}) with magnitude {refusedMagnitude} rather than the documented default";
        }

        var narrowGate = ((BigInteger.One << 96) - (BigInteger.One << 32));
        var reachedNarrow = false;
        var reachedWide = false;
        var reachedGuardedBand = false;
        var reachedBeyondSignedCarrier = false;
        var raws = new long[3];
        var ideal = new long[3];
        var counter = 0UL;

        for (var index = 0; (index < (DirectionLadder.Length + NormalizeDrawCount)); ++index) {
            if (index < DirectionLadder.Length) {
                var row = DirectionLadder[index];

                (raws[0], raws[1], raws[2]) = (row[0], row[1], row[2]);
            } else {
                raws[0] = DrawComponent(counter: ref counter);
                raws[1] = DrawComponent(counter: ref counter);
                raws[2] = DrawComponent(counter: ref counter);

                if ((raws[0] == 0L) && (raws[1] == 0L) && (raws[2] == 0L)) { continue; }
            }

            if (!FixedVectorMath.TryNormalizeWithMagnitude(
                x: raws[0],
                y: raws[1],
                z: raws[2],
                unitX: out var unitX,
                unitY: out var unitY,
                unitZ: out var unitZ,
                rawMagnitude: out var magnitude
            )) {
                return $"the non-zero direction ({raws[0]}, {raws[1]}, {raws[2]}) was refused";
            }

            var squaredSum = Oracles.SquaredNorm(raws: raws);

            if (squaredSum < narrowGate) {
                reachedNarrow = true;
            } else {
                reachedWide = true;

                if (squaredSum < (BigInteger.One << 96)) { reachedGuardedBand = true; }
            }

            var expectedMagnitude = Oracles.NearestIntegerRoot(value: squaredSum);

            if (new BigInteger(value: magnitude) != expectedMagnitude) {
                return $"the magnitude of ({raws[0]}, {raws[1]}, {raws[2]}) is {magnitude}, and the nearest integer root of its exact squared sum is {expectedMagnitude}";
            }

            if (expectedMagnitude > long.MaxValue) { reachedBeyondSignedCarrier = true; }

            Oracles.IdealUnitVector(raws: raws, result: ideal);

            var deviation = Math.Max(
                Math.Abs(value: (unitX - ideal[0])),
                Math.Max(Math.Abs(value: (unitY - ideal[1])), Math.Abs(value: (unitZ - ideal[2])))
            );

            if (deviation > 1L) {
                return $"the unit direction of ({raws[0]}, {raws[1]}, {raws[2]}) is ({unitX}, {unitY}, {unitZ}), which departs from the ideal ({ideal[0]}, {ideal[1]}, {ideal[2]}) by {deviation} raw";
            }
        }

        if (!reachedNarrow) {
            return "no swept direction took the narrow Q16-radicand branch";
        }

        if (!reachedWide) {
            return "no swept direction took the wide exact-root branch";
        }

        if (!reachedGuardedBand) {
            return "no swept direction landed in the 2^32-wide band below 2^96 the narrow path excludes, so the exclusion is unexercised";
        }

        if (!reachedBeyondSignedCarrier) {
            return "no swept direction produced a magnitude past long.MaxValue, so the full-unsigned-width contract this member exists for is unexercised";
        }

        return null;
    }
    /// <summary>
    /// The same statement as <see cref="SinCosRawFullUnsignedWidthSurface"/>, at full volume: every raw of one whole
    /// turn, then a quarter-million draws across the entire unsigned width.
    /// </summary>
    /// <returns>The counterexample, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The sweep is written out inline rather than taken from a <c>Domain</c>: an Exhaustive case that consumed one
    /// would advance the frontier counter its Default sibling reads, sliding that sibling's operands as a side effect
    /// of this sweep having run.
    /// </remarks>
    internal static string? SinCosRawWidthSweepSurface() {
        for (var rawAngle = 0UL; (rawAngle <= DenseTurnLimit); ++rawAngle) {
            var failure = CompareSinCosRaw(rawAngle: rawAngle);

            if (failure is not null) { return failure; }
        }

        var counter = 0UL;

        for (var draw = 0; (draw < SinCosRawSweepDrawCount); ++draw) {
            var failure = CompareSinCosRaw(rawAngle: MixIndex(index: ++counter));

            if (failure is not null) { return failure; }
        }

        return null;
    }
    /// <summary>
    /// The unit-direction bound over the full signed carrier, at full volume, for BOTH
    /// normalizers: the public <see cref="FixedVector3.Normalize"/> and the internal one-pass
    /// <c>FixedVectorMath.TryNormalizeWithMagnitude</c>.
    /// </summary>
    /// <returns>The counterexample, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The two pipelines differ: the public one preconditions onto leading bit forty-five and divides by a Q16-scaled
    /// common root, while the one-pass member takes the unpreconditioned squared sum and switches denominators at a
    /// 2⁴⁸ norm. They are held to the SAME one-raw bound against the same ideal, so a defect in either shows.
    /// </remarks>
    internal static string? NormalizeIdealBoundWidthSweepSurface() {
        var raws = new long[3];
        var ideal = new long[3];
        var counter = 0UL;

        for (var draw = 0; (draw < NormalizeSweepDrawCount); ++draw) {
            raws[0] = DrawComponent(counter: ref counter);
            raws[1] = DrawComponent(counter: ref counter);
            raws[2] = DrawComponent(counter: ref counter);

            if ((raws[0] == 0L) && (raws[1] == 0L) && (raws[2] == 0L)) { continue; }

            Oracles.IdealUnitVector(raws: raws, result: ideal);

            var staged = new FixedVector3(
                X: FixedQ4816.FromRawBits(value: raws[0]),
                Y: FixedQ4816.FromRawBits(value: raws[1]),
                Z: FixedQ4816.FromRawBits(value: raws[2])
            ).Normalize();
            var stagedDeviation = Math.Max(
                val1: Math.Abs(value: (staged.X.Value - ideal[0])),
                val2: Math.Max(val1: Math.Abs(value: (staged.Y.Value - ideal[1])), val2: Math.Abs(value: (staged.Z.Value - ideal[2])))
            );

            if (stagedDeviation > 1L) {
                return $"FixedVector3.Normalize of ({raws[0]}, {raws[1]}, {raws[2]}) is ({staged.X.Value}, {staged.Y.Value}, {staged.Z.Value}), which departs from the ideal ({ideal[0]}, {ideal[1]}, {ideal[2]}) by {stagedDeviation} raw";
            }

            if (!FixedVectorMath.TryNormalizeWithMagnitude(
                x: raws[0],
                y: raws[1],
                z: raws[2],
                unitX: out var unitX,
                unitY: out var unitY,
                unitZ: out var unitZ,
                rawMagnitude: out var magnitude
            )) {
                return $"the non-zero direction ({raws[0]}, {raws[1]}, {raws[2]}) was refused";
            }

            var onePassDeviation = Math.Max(
                Math.Abs(value: (unitX - ideal[0])),
                Math.Max(Math.Abs(value: (unitY - ideal[1])), Math.Abs(value: (unitZ - ideal[2])))
            );

            if (onePassDeviation > 1L) {
                return $"the one-pass unit direction of ({raws[0]}, {raws[1]}, {raws[2]}) is ({unitX}, {unitY}, {unitZ}), which departs from the ideal ({ideal[0]}, {ideal[1]}, {ideal[2]}) by {onePassDeviation} raw";
            }

            var expectedMagnitude = Oracles.NearestIntegerRoot(value: Oracles.SquaredNorm(raws: raws));

            if (new BigInteger(value: magnitude) != expectedMagnitude) {
                return $"the magnitude of ({raws[0]}, {raws[1]}, {raws[2]}) is {magnitude}, and the nearest integer root of its exact squared sum is {expectedMagnitude}";
            }
        }

        return null;
    }

    private static string? CompareSinCosRaw(ulong rawAngle) {
        var (sin, cos) = FixedQ4816.SinCosRaw(rawAngle: rawAngle);
        var (sinEnclosure, cosEnclosure) = EncloseUnsignedAngle(rawAngle: rawAngle);
        var envelope = SinCosRawEnvelope(rawAngle: rawAngle);
        var sinGap = Deviation(enclosure: sinEnclosure, raw: sin.Value);

        if (sinGap > envelope) {
            return $"SinCosRaw({rawAngle}).Sin is {sin.Value}, which sits {sinGap} raw outside the enclosure [{sinEnclosure.Low}, {sinEnclosure.High}] (envelope {envelope})";
        }

        var cosGap = Deviation(enclosure: cosEnclosure, raw: cos.Value);

        if (cosGap > envelope) {
            return $"SinCosRaw({rawAngle}).Cos is {cos.Value}, which sits {cosGap} raw outside the enclosure [{cosEnclosure.Low}, {cosEnclosure.High}] (envelope {envelope})";
        }

        return null;
    }
    /// <summary>The envelope, in raw Q16 units, the turn-domain reduction's own arithmetic implies at a given angle.
    /// DERIVED, never fitted to an observation.</summary>
    /// <param name="rawAngle">The unsigned raw angle.</param>
    /// <returns>The largest departure from the ideal sine or cosine the kernel may show at that angle.</returns>
    /// <remarks>
    /// The reduction constant is <c>c = round(2⁶⁴/2π)</c>, so <c>|c − 2⁶⁴/2π| ≤ ½</c>. The kernel forms
    /// <c>u·c</c> exactly and reads the fractional turn off bit 16 upward, which makes the phase error at most
    /// <c>u/2⁸¹</c> turns — <c>2π·u/2⁸¹</c> radians. Since neither sine nor cosine moves faster than one per radian,
    /// the value error that phase implies is at most <c>2¹⁶·2π·u/2⁸¹</c> raw, which is <c>π·u/2⁶⁴</c> and reaches π at
    /// the top of the unsigned width. 355/113 stands in for π as a strict OVERESTIMATE, and the quotient is taken as a
    /// CEILING, so the whole term is an upper bound reached in integers with no rounding slipped in.
    /// </remarks>
    private static BigInteger SinCosRawEnvelope(ulong rawAngle) {
        var numerator = (new BigInteger(value: rawAngle) * 355);
        var denominator = (new BigInteger(value: 113) << 64);

        return (SinCosRawFlatBudget + ((numerator + (denominator - BigInteger.One)) / denominator));
    }
    // ---- the angle-addition carriage of Oracles.EncloseSinCos past the signed carrier ----

    private static (Oracles.Enclosure Sin, Oracles.Enclosure Cos) EncloseUnsignedAngle(ulong rawAngle) {
        // Below the seam the oracle takes the angle unchanged; the split exists only for the half no long can name.
        var enclosed = ((rawAngle <= ((ulong)long.MaxValue))
            ? Oracles.EncloseSinCos(guardBitCount: GuardBitCount, raw: ((long)rawAngle))
            : SplitEnclosure(rawAngle: rawAngle));

        return (
            Narrow(shift: GuardBitCount, value: enclosed.Sin),
            Narrow(shift: GuardBitCount, value: enclosed.Cos)
        );
    }
    // The angle above the seam is written as a sum of representable halves — plus, at the single word 2^64 − 1 where
    // no two halves suffice, one further raw — and the enclosures are combined by the angle-addition identity. Every
    // part is exact: the split adds no rounding, only the interval arithmetic does.
    private static (Oracles.Enclosure Sin, Oracles.Enclosure Cos) SplitEnclosure(ulong rawAngle) {
        var lower = (rawAngle >> 1);
        var upper = (rawAngle - lower);
        var carried = 0UL;

        if (upper > ((ulong)long.MaxValue)) {
            upper -= 1UL;
            carried = 1UL;
        }

        var combined = Combine(
            left: Oracles.EncloseSinCos(guardBitCount: GuardBitCount, raw: ((long)lower)),
            right: Oracles.EncloseSinCos(guardBitCount: GuardBitCount, raw: ((long)upper))
        );

        return ((carried == 0UL)
            ? combined
            : Combine(left: combined, right: Oracles.EncloseSinCos(guardBitCount: GuardBitCount, raw: ((long)carried))));
    }
    // sin(a+b) = sin a·cos b + cos a·sin b and cos(a+b) = cos a·cos b − sin a·sin b, in interval arithmetic, with the
    // doubled scale narrowed straight back so the result composes with a further part.
    private static (Oracles.Enclosure Sin, Oracles.Enclosure Cos) Combine(
        (Oracles.Enclosure Sin, Oracles.Enclosure Cos) left,
        (Oracles.Enclosure Sin, Oracles.Enclosure Cos) right
    ) {
        var narrowing = (16 + GuardBitCount);

        return (
            Narrow(
                value: Add(
                    left: Multiply(left: left.Sin, right: right.Cos),
                    right: Multiply(left: left.Cos, right: right.Sin)
                ),
                shift: narrowing
            ),
            Narrow(
                value: Subtract(
                    left: Multiply(left: left.Cos, right: right.Cos),
                    right: Multiply(left: left.Sin, right: right.Sin)
                ),
                shift: narrowing
            )
        );
    }
    private static Oracles.Enclosure Add(Oracles.Enclosure left, Oracles.Enclosure right) =>
        new(Low: (left.Low + right.Low), High: (left.High + right.High));
    private static Oracles.Enclosure Subtract(Oracles.Enclosure left, Oracles.Enclosure right) =>
        new(Low: (left.Low - right.High), High: (left.High - right.Low));
    private static Oracles.Enclosure Multiply(Oracles.Enclosure left, Oracles.Enclosure right) {
        var lowLow = (left.Low * right.Low);
        var lowHigh = (left.Low * right.High);
        var highLow = (left.High * right.Low);
        var highHigh = (left.High * right.High);

        return new(
            Low: BigInteger.Min(left: BigInteger.Min(left: lowLow, right: lowHigh), right: BigInteger.Min(left: highLow, right: highHigh)),
            High: BigInteger.Max(left: BigInteger.Max(left: lowLow, right: lowHigh), right: BigInteger.Max(left: highLow, right: highHigh))
        );
    }
    // BigInteger's right shift floors, which is what a lower bound wants; the upper bound negates around it so the
    // narrowed pair still brackets the same real value.
    private static Oracles.Enclosure Narrow(Oracles.Enclosure value, int shift) =>
        new(Low: (value.Low >> shift), High: (-((-value.High) >> shift)));
    private static BigInteger Deviation(Oracles.Enclosure enclosure, long raw) {
        var exact = new BigInteger(value: raw);

        if (exact < enclosure.Low) { return (enclosure.Low - exact); }

        if (exact > enclosure.High) { return (exact - enclosure.High); }

        return BigInteger.Zero;
    }
    // ---- SplitMix64 index mixer: a pure function of a running counter, never System.Random and never wall-clock ----

    private static ulong MixIndex(ulong index) {
        var mixed = (index + 0x9E3779B97F4A7C15UL);

        mixed = ((mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL);
        mixed = ((mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL);

        return mixed ^ (mixed >> 31);
    }
    // A draw whose MAGNITUDE sweeps every scale: the mixed word is shifted right arithmetically by a count taken from
    // its own low bits, so the stream spans single raws through the carrier's extremes rather than clustering at the
    // top of the range where only the wide branch runs.
    private static long DrawComponent(ref ulong counter) {
        counter += 1UL;

        var mixed = MixIndex(index: counter);

        counter += 1UL;

        return (unchecked((long)mixed) >> ((int)(MixIndex(index: counter) % 64UL)));
    }
}
