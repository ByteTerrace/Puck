using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    // ---- the fixed-point vectors: the plane and the space ----
    //
    // Every reference below is ONE ties-to-even rounding of the exact expression at the ideal scale, formed in
    // BigInteger. None builds a machine-width accumulator, none observes the subjects' narrow/wide lane gate, and none
    // calls a Puck.Maths kernel. The rounding faces are the module's own — RoundDyadic, RoundToEvenUnits,
    // RoundRationalTiesToEven and NearestIntegerRoot — shared with the other oracles here and with nothing else.

    /// <summary>The reference fused dot product — ONE ties-to-even rounding of the exact sum of raw Q32 products at
    /// shift sixteen, wrapped to the carrier. The lane count is the span length, so the plane and the space share one
    /// derivation.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws, the same width.</param>
    /// <returns>The dot product's raw.</returns>
    public static long FusedDot(ReadOnlySpan<long> left, ReadOnlySpan<long> right) {
        var exact = BigInteger.Zero;

        for (var lane = 0; (lane < left.Length); ++lane) {
            exact += (((BigInteger)left[lane]) * right[lane]);
        }

        return RoundDyadic(exact: exact, shift: 16);
    }
    /// <summary>The reference fused wedge — ONE ties-to-even rounding of the exact <c>x₁·y₂ − y₁·x₂</c> at shift
    /// sixteen, wrapped to the carrier.</summary>
    /// <param name="leftX">The first vector's first raw.</param>
    /// <param name="leftY">The first vector's second raw.</param>
    /// <param name="rightX">The second vector's first raw.</param>
    /// <param name="rightY">The second vector's second raw.</param>
    /// <returns>The bivector coefficient's raw.</returns>
    public static long FusedWedge(long leftX, long leftY, long rightX, long rightY) =>
        RoundDyadic(exact: ((((BigInteger)leftX) * rightY) - (((BigInteger)leftY) * rightX)), shift: 16);
    /// <summary>The reference fused cross product — each lane ONE ties-to-even rounding of its exact two-product
    /// difference at shift sixteen, wrapped to the carrier.</summary>
    /// <param name="left">The first vector's three raws.</param>
    /// <param name="right">The second vector's three raws.</param>
    /// <param name="result">The destination lanes, three wide.</param>
    /// <remarks>The right-handed cycle is spelled out lane by lane rather than delegated, so a transposed or mis-signed
    /// lane assignment in the subject has an independently authored orientation to fail against.</remarks>
    public static void FusedCross(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        result[0] = RoundDyadic(exact: ((((BigInteger)left[1]) * right[2]) - (((BigInteger)left[2]) * right[1])), shift: 16);
        result[1] = RoundDyadic(exact: ((((BigInteger)left[2]) * right[0]) - (((BigInteger)left[0]) * right[2])), shift: 16);
        result[2] = RoundDyadic(exact: ((((BigInteger)left[0]) * right[1]) - (((BigInteger)left[1]) * right[0])), shift: 16);
    }
    /// <summary>The per-product-rounding discipline for a dot product: EACH raw Q32 product rounded to Q16 on its own,
    /// then summed exactly and wrapped. The alternative a kernel without a fused accumulator is forced into; it exists
    /// only so a canary can require the fused kernel to differ from it.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws, the same width.</param>
    /// <returns>The per-product dot product's raw.</returns>
    public static long PerProductDot(ReadOnlySpan<long> left, ReadOnlySpan<long> right) {
        var total = BigInteger.Zero;

        for (var lane = 0; (lane < left.Length); ++lane) {
            total += RoundToEvenUnits(magnitude: (((BigInteger)left[lane]) * right[lane]), shift: 16);
        }

        return WrapToRaw(value: total);
    }
    /// <summary>The per-product-rounding discipline for a wedge — both raw Q32 products rounded to Q16 on their own
    /// before the exact difference.</summary>
    /// <param name="leftX">The first vector's first raw.</param>
    /// <param name="leftY">The first vector's second raw.</param>
    /// <param name="rightX">The second vector's first raw.</param>
    /// <param name="rightY">The second vector's second raw.</param>
    /// <returns>The per-product bivector coefficient's raw.</returns>
    public static long PerProductWedge(long leftX, long leftY, long rightX, long rightY) =>
        WrapToRaw(value: (
            RoundToEvenUnits(magnitude: (((BigInteger)leftX) * rightY), shift: 16) -
            RoundToEvenUnits(magnitude: (((BigInteger)leftY) * rightX), shift: 16)
        ));
    /// <summary>The exact rational <c>numerator · 2^fractionBitCount / denominator</c> rounded to a signed 64-bit
    /// raw, to nearest with ties to even, refusing rather than wrapping.</summary>
    /// <param name="numerator">The exact numerator.</param>
    /// <param name="denominator">The exact denominator.</param>
    /// <param name="fractionBitCount">The result's fraction bit count.</param>
    /// <param name="result">The rounded raw on success; zero on refusal.</param>
    /// <returns>Whether the value is representable.</returns>
    /// <remarks>Independent of the subject in both the scaling and the tie: the scale is a
    /// <see cref="BigInteger.Pow(BigInteger, int)"/> multiply rather than a shift, and the tie is decided by
    /// DOUBLING the remainder against the divisor rather than by comparing it with the distance to the next
    /// multiple.</remarks>
    public static bool RoundedRational(BigInteger numerator, BigInteger denominator, int fractionBitCount, out long result) {
        result = 0L;

        if (denominator.IsZero || (fractionBitCount < 0)) {
            return false;
        }

        var negative = ((numerator.Sign < 0) != (denominator.Sign < 0));
        var scaled = (BigInteger.Abs(value: numerator) * BigInteger.Pow(exponent: fractionBitCount, value: 2));
        var divisor = BigInteger.Abs(value: denominator);
        var quotient = BigInteger.Divide(dividend: scaled, divisor: divisor);
        var doubledRemainder = ((scaled - (quotient * divisor)) * 2);

        if (
            (doubledRemainder > divisor) ||
            ((doubledRemainder == divisor) && !quotient.IsEven)
        ) {
            quotient += BigInteger.One;
        }

        var signed = (negative
            ? -quotient
            : quotient
        );

        if (
            (signed < long.MinValue) ||
            (signed > long.MaxValue)
        ) {
            return false;
        }

        result = ((long)signed);

        return true;
    }
    /// <summary>The per-square-rounding discipline for a squared norm: EACH raw Q32 square rounded to Q16 on its own,
    /// then summed exactly and returned UNWRAPPED.</summary>
    /// <param name="raws">The vector's raws.</param>
    /// <returns>The per-square squared norm, unwrapped.</returns>
    public static BigInteger PerSquareNorm(ReadOnlySpan<long> raws) {
        var total = BigInteger.Zero;

        foreach (var raw in raws) {
            var magnitude = BigInteger.Abs(value: new BigInteger(value: raw));

            total += RoundToEvenUnits(magnitude: (magnitude * magnitude), shift: 16);
        }

        return total;
    }
    /// <summary>The exact raw Q32 sum of squares — the value both norm kernels start from, unrounded and
    /// unwrapped.</summary>
    /// <param name="raws">The vector's raws.</param>
    /// <returns>The exact sum of squares.</returns>
    public static BigInteger SquaredNorm(ReadOnlySpan<long> raws) {
        var total = BigInteger.Zero;

        foreach (var raw in raws) {
            var exact = new BigInteger(value: raw);

            total += (exact * exact);
        }

        return total;
    }
    /// <summary>The reference squared length — ONE ties-to-even rounding of <see cref="SquaredNorm"/> at shift sixteen,
    /// returned UNWRAPPED so the caller can state the saturation predicate against <see cref="long.MaxValue"/>, which a
    /// wrap would destroy.</summary>
    /// <param name="raws">The vector's raws.</param>
    /// <returns>The rounded squared length, unwrapped.</returns>
    public static BigInteger RoundedSquaredNorm(ReadOnlySpan<long> raws) =>
        RoundToEvenUnits(magnitude: SquaredNorm(raws: raws), shift: 16);
    /// <summary>The reference length — the NEAREST integer square root of the exact raw Q32 sum of squares, returned
    /// unwrapped. Rooting a raw Q32 quantity yields a raw Q16 one, so the only rounding is that final root.</summary>
    /// <param name="raws">The vector's raws.</param>
    /// <returns>The rounded length, unwrapped.</returns>
    /// <remarks>The root is <see cref="NearestIntegerRoot"/>, a bracketed integer search whose predicate is one exact
    /// squaring — deliberately a different route from the subject's floor-then-compare-the-remainder-with-the-root
    /// repair, so a transcription error in either repair rule fails the law.</remarks>
    public static BigInteger NormRoot(ReadOnlySpan<long> raws) =>
        NearestIntegerRoot(value: SquaredNorm(raws: raws));
    /// <summary>The reference linear interpolation over the TRUE mathematical result — the exact rational
    /// <c>from + (to − from)·amount</c>, formed as one arbitrary-width <see cref="BigInteger"/> intermediate with NO
    /// intermediate wrap at any width, and rounded to the Q48.16 grid exactly once.</summary>
    /// <param name="from">The origin's raw.</param>
    /// <param name="to">The destination's raw.</param>
    /// <param name="amount">The interpolation fraction's raw.</param>
    /// <returns>The interpolated raw.</returns>
    /// <remarks>Classical rather than transcription: the arithmetic is re-derived here in <see cref="BigInteger"/>,
    /// not an implementation detail read off a kernel. The difference <c>to − from</c> is never taken as its own raw
    /// and never wrapped — a <see cref="BigInteger"/> difference cannot leave any carrier — so this would catch a
    /// subject that wraps that difference before multiplying even where every operand and the true result are
    /// representable.</remarks>
    public static long LerpRaw(long from, long to, long amount) =>
        RoundDyadic(
            exact: ((new BigInteger(value: from) << 16) + ((new BigInteger(value: to) - from) * amount)),
            shift: 16
        );
    /// <summary>The exact scalar MoveToward landing/ordering decision: the true displacement
    /// <c>targetRaw − currentRaw</c> is formed in <see cref="BigInteger"/>, with no wrap at any width. Returns
    /// <paramref name="targetRaw"/> when that exact displacement's magnitude is at most <paramref name="maxDeltaRaw"/>;
    /// otherwise <paramref name="currentRaw"/> stepped by <paramref name="maxDeltaRaw"/> in the displacement's own
    /// sign, reduced to the signed 64-bit carrier.</summary>
    /// <param name="currentRaw">The current raw.</param>
    /// <param name="targetRaw">The target raw.</param>
    /// <param name="maxDeltaRaw">The non-negative maximum step raw.</param>
    /// <returns>The expected raw.</returns>
    /// <remarks>Shares nothing with the subject: <see cref="FixedQ4816.MoveToward"/> reads ordering and separation
    /// from a widened UNSIGNED reading of the same two raws, where this oracle forms the exact SIGNED displacement in
    /// arbitrary width and never reads a wrapped subtraction at any point.</remarks>
    public static long MoveTowardRaw(long currentRaw, long targetRaw, long maxDeltaRaw) {
        var displacement = (new BigInteger(value: targetRaw) - currentRaw);

        if (BigInteger.Abs(value: displacement) <= maxDeltaRaw) {
            return targetRaw;
        }

        var step = ((displacement.Sign > 0) ? maxDeltaRaw : -maxDeltaRaw);

        return WrapToRaw(value: (new BigInteger(value: currentRaw) + step));
    }
    /// <summary>The exact vector MoveToward landing verdict and per-axis displacement sign, independent of the
    /// subject's own subtraction: each axis's raw displacement <c>targetRaw − currentRaw</c> is formed in
    /// <see cref="BigInteger"/> with no wrap at any width, and landing is decided by comparing the exact squared
    /// Euclidean distance against <c>maxDeltaRaw²</c>, never a wrapped or narrowed intermediate.</summary>
    /// <param name="currentX">The current vector's X raw.</param>
    /// <param name="currentY">The current vector's Y raw.</param>
    /// <param name="currentZ">The current vector's Z raw.</param>
    /// <param name="targetX">The target vector's X raw.</param>
    /// <param name="targetY">The target vector's Y raw.</param>
    /// <param name="targetZ">The target vector's Z raw.</param>
    /// <param name="maxDeltaRaw">The non-negative maximum step raw.</param>
    /// <returns>Whether the exact distance lands within <paramref name="maxDeltaRaw"/>, and each axis's exact
    /// displacement sign (<c>-1</c>, <c>0</c>, or <c>1</c>).</returns>
    public static (bool Landing, int SignX, int SignY, int SignZ) MoveTowardVerdict(
        long currentX,
        long currentY,
        long currentZ,
        long targetX,
        long targetY,
        long targetZ,
        long maxDeltaRaw
    ) {
        var dx = (new BigInteger(value: targetX) - currentX);
        var dy = (new BigInteger(value: targetY) - currentY);
        var dz = (new BigInteger(value: targetZ) - currentZ);
        var squaredSum = (((dx * dx) + (dy * dy)) + (dz * dz));
        var maxDeltaSquared = (new BigInteger(value: maxDeltaRaw) * maxDeltaRaw);

        return (
            Landing: (squaredSum <= maxDeltaSquared),
            SignX: dx.Sign,
            SignY: dy.Sign,
            SignZ: dz.Sign
        );
    }
    /// <summary>The IDEAL Q16 unit direction: each component ONE ties-to-even rounding of the exact ratio
    /// <c>rawᵢ·2¹⁶ / √(Σ rawⱼ²)</c>, with no preconditioning and no intermediate quantization. A zero vector maps to
    /// the zero vector.</summary>
    /// <param name="raws">The direction's raws.</param>
    /// <param name="result">The destination lanes, the same width.</param>
    /// <remarks>Derived without ever forming a square root: the integer part is bracketed by the exact comparison
    /// <c>q²·S ≤ (|rawᵢ|·2¹⁶)²</c> over the closed range <c>[0, 2¹⁶]</c> — closed because every component's square is
    /// one term of <c>S</c>, so the ratio cannot exceed <c>2¹⁶</c> — and the rounding decision by <c>(2q+1)²·S</c>
    /// against <c>4·(|rawᵢ|·2¹⁶)²</c> with equality resolved to even. It shares no shift, no common denominator and no
    /// root with the staged pipeline the subject runs, which is what makes agreement to within one raw evidence rather
    /// than a restatement.</remarks>
    public static void IdealUnitVector(ReadOnlySpan<long> raws, Span<long> result) {
        var squaredNorm = SquaredNorm(raws: raws);

        if (squaredNorm.IsZero) {
            for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = 0L; }

            return;
        }

        for (var lane = 0; (lane < raws.Length); ++lane) {
            var numerator = (BigInteger.Abs(value: new BigInteger(value: raws[lane])) << 16);
            var squaredNumerator = (numerator * numerator);
            var low = BigInteger.Zero;
            var high = ((BigInteger.One << 16) + BigInteger.One);

            while ((high - low) > BigInteger.One) {
                var middle = ((low + high) >> 1);

                if (((middle * middle) * squaredNorm) <= squaredNumerator) { low = middle; } else { high = middle; }
            }

            var odd = ((low << 1) + BigInteger.One);
            var comparison = BigInteger.Compare(left: ((odd * odd) * squaredNorm), right: (squaredNumerator << 2));
            var rounded = (((comparison < 0) || ((0 == comparison) && !((low & BigInteger.One).IsZero)))
                ? (low + BigInteger.One)
                : low);

            result[lane] = WrapToRaw(value: ((raws[lane] < 0L) ? -rounded : rounded));
        }
    }
    /// <summary>The STAGED normalization the shipped pipeline performs, re-derived in <see cref="BigInteger"/>: the
    /// common power-of-two precondition at leading bit forty-five (ties to even on a shrinking shift), the Q16-scaled
    /// nearest root as the single common denominator, and one ties-to-even ratio per component.</summary>
    /// <param name="raws">The direction's raws.</param>
    /// <param name="result">The destination lanes, the same width.</param>
    /// <remarks>A TRANSCRIPTION of the subject's own derivation — it shares no code, and it deliberately shares the
    /// STAGING, so a shared staging error would cancel. Any law standing on it declares faithful carriage and names
    /// <see cref="IdealUnitVector"/> beside it as the independent witness.</remarks>
    public static void StagedUnitVector(ReadOnlySpan<long> raws, Span<long> result) {
        var maximum = BigInteger.Zero;

        foreach (var raw in raws) {
            maximum = BigInteger.Max(left: maximum, right: BigInteger.Abs(value: new BigInteger(value: raw)));
        }

        if (maximum.IsZero) {
            for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = 0L; }

            return;
        }

        // Stage one: the common power-of-two precondition at leading bit forty-five. A non-negative shift is a pure
        // left shift and is EXACT; a negative one is a ties-to-even right shift, the pipeline's one lossy step.
        var shift = (45 - ((int)(maximum.GetBitLength() - 1L)));
        var scaled = new BigInteger[raws.Length];
        var squaredSum = BigInteger.Zero;

        for (var lane = 0; (lane < raws.Length); ++lane) {
            var magnitude = BigInteger.Abs(value: new BigInteger(value: raws[lane]));
            var preconditioned = ((shift >= 0) ? (magnitude << shift) : RoundToEvenUnits(magnitude: magnitude, shift: -shift));

            scaled[lane] = ((raws[lane] < 0L) ? -preconditioned : preconditioned);
            squaredSum += (preconditioned * preconditioned);
        }

        // Stage two: the Q16-scaled nearest root, the one common denominator every component divides by. Stage three:
        // one ties-to-even ratio per component against it.
        var denominator = NearestIntegerRoot(value: (squaredSum << 32));

        for (var lane = 0; (lane < raws.Length); ++lane) {
            var quotient = RoundRationalTiesToEven(numerator: (BigInteger.Abs(value: scaled[lane]) << 32), denominator: denominator);

            result[lane] = WrapToRaw(value: ((scaled[lane].Sign < 0) ? -quotient : quotient));
        }
    }
    /// <summary>The exact rational solve of a symmetric 2×2 system by Cramer's rule, direct in <see cref="BigInteger"/>
    /// with NO intermediate rounding, NO wrapping, and — unlike <see cref="FixedSymmetricSolve"/> — no common
    /// power-of-two preconditioning of any kind: every entry is used at its own raw magnitude, so a defect in the
    /// subject's shift selection or its bit budget cannot also appear here.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="rhsX">The right-hand side's first component.</param>
    /// <param name="rhsY">The right-hand side's second component.</param>
    /// <param name="outputFractionShift">The requested output fraction bit count.</param>
    /// <param name="x">The first solution component on success; zero on refusal.</param>
    /// <param name="y">The second solution component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> for an exactly singular matrix or a result outside the signed 64-bit range.</returns>
    public static bool TrySolveSymmetric2(long a, long b, long d, long rhsX, long rhsY, int outputFractionShift, out long x, out long y) {
        BigInteger ba = a, bb = b, bd = d, brx = rhsX, bry = rhsY;
        var det = ((ba * bd) - (bb * bb));

        if (det.IsZero) {
            x = 0L;
            y = 0L;
            return false;
        }

        var nx = ((bd * brx) - (bb * bry));
        var ny = ((ba * bry) - (bb * brx));

        // The refusal contract is "false AND every output zero" — computed into locals first and only exposed
        // through the out parameters once EVERY component is known to round successfully, so a later component's
        // overflow can never leave an earlier one's already-computed value behind.
        var okX = TryRoundRatio(denominator: det, numerator: nx, raw: out var rx, shift: outputFractionShift);
        var okY = TryRoundRatio(denominator: det, numerator: ny, raw: out var ry, shift: outputFractionShift);

        if (!okX || !okY) {
            x = 0L;
            y = 0L;
            return false;
        }

        x = rx;
        y = ry;
        return true;
    }
    /// <summary>The exact rational solve of a symmetric 3×3 system by Cramer's rule. See
    /// <see cref="TrySolveSymmetric2"/> for the independence argument.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="c">The (0,2) = (2,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="e">The (1,2) = (2,1) entry.</param>
    /// <param name="f">The (2,2) entry.</param>
    /// <param name="rhsX">The right-hand side's first component.</param>
    /// <param name="rhsY">The right-hand side's second component.</param>
    /// <param name="rhsZ">The right-hand side's third component.</param>
    /// <param name="outputFractionShift">The requested output fraction bit count.</param>
    /// <param name="x">The first solution component on success; zero on refusal.</param>
    /// <param name="y">The second solution component on success; zero on refusal.</param>
    /// <param name="z">The third solution component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> for an exactly singular matrix or a result outside the signed 64-bit range.</returns>
    public static bool TrySolveSymmetric3(
        long a,
        long b,
        long c,
        long d,
        long e,
        long f,
        long rhsX,
        long rhsY,
        long rhsZ,
        int outputFractionShift,
        out long x,
        out long y,
        out long z
    ) {
        BigInteger ba = a, bb = b, bc = c, bd = d, be = e, bf = f, brx = rhsX, bry = rhsY, brz = rhsZ;
        var det = ((((((ba * bd) * bf) - ((ba * be) * be)) - ((bb * bb) * bf)) + (((2 * bb) * bc) * be)) - ((bc * bc) * bd));

        if (det.IsZero) {
            x = 0L;
            y = 0L;
            z = 0L;
            return false;
        }

        var c11 = ((bd * bf) - (be * be));
        var c12 = ((bc * be) - (bb * bf));
        var c13 = ((bb * be) - (bc * bd));
        var c22 = ((ba * bf) - (bc * bc));
        var c23 = ((bb * bc) - (ba * be));
        var c33 = ((ba * bd) - (bb * bb));
        var nx = (((c11 * brx) + (c12 * bry)) + (c13 * brz));
        var ny = (((c12 * brx) + (c22 * bry)) + (c23 * brz));
        var nz = (((c13 * brx) + (c23 * bry)) + (c33 * brz));

        // See TrySolveSymmetric2's own note: no output is exposed until every component is known to round.
        var okX = TryRoundRatio(denominator: det, numerator: nx, raw: out var rx, shift: outputFractionShift);
        var okY = TryRoundRatio(denominator: det, numerator: ny, raw: out var ry, shift: outputFractionShift);
        var okZ = TryRoundRatio(denominator: det, numerator: nz, raw: out var rz, shift: outputFractionShift);

        if (!okX || !okY || !okZ) {
            x = 0L;
            y = 0L;
            z = 0L;
            return false;
        }

        x = rx;
        y = ry;
        z = rz;
        return true;
    }
    /// <summary>The exact rational inverse of a symmetric 2×2 matrix's three distinct entries. See
    /// <see cref="TrySolveSymmetric2"/> for the independence argument.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="outputFractionShift">The requested output fraction bit count.</param>
    /// <param name="invA">The inverse's (0,0) entry on success; zero on refusal.</param>
    /// <param name="invB">The inverse's (0,1) = (1,0) entry on success; zero on refusal.</param>
    /// <param name="invD">The inverse's (1,1) entry on success; zero on refusal.</param>
    /// <returns><see langword="false"/> for an exactly singular matrix or a result outside the signed 64-bit range.</returns>
    public static bool TryInvertSymmetric2(long a, long b, long d, int outputFractionShift, out long invA, out long invB, out long invD) {
        BigInteger ba = a, bb = b, bd = d;
        var det = ((ba * bd) - (bb * bb));

        if (det.IsZero) {
            invA = 0L;
            invB = 0L;
            invD = 0L;
            return false;
        }

        // See TrySolveSymmetric2's own note: no output is exposed until every component is known to round.
        var okA = TryRoundRatio(denominator: det, numerator: bd, raw: out var ra, shift: outputFractionShift);
        var okB = TryRoundRatio(denominator: det, numerator: -bb, raw: out var rb, shift: outputFractionShift);
        var okD = TryRoundRatio(denominator: det, numerator: ba, raw: out var rd, shift: outputFractionShift);

        if (!okA || !okB || !okD) {
            invA = 0L;
            invB = 0L;
            invD = 0L;
            return false;
        }

        invA = ra;
        invB = rb;
        invD = rd;
        return true;
    }
    /// <summary>The exact rational inverse of a symmetric 3×3 matrix's six distinct entries. See
    /// <see cref="TrySolveSymmetric2"/> for the independence argument.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="c">The (0,2) = (2,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="e">The (1,2) = (2,1) entry.</param>
    /// <param name="f">The (2,2) entry.</param>
    /// <param name="outputFractionShift">The requested output fraction bit count.</param>
    /// <param name="invA">The inverse's (0,0) entry on success; zero on refusal.</param>
    /// <param name="invB">The inverse's (0,1) = (1,0) entry on success; zero on refusal.</param>
    /// <param name="invC">The inverse's (0,2) = (2,0) entry on success; zero on refusal.</param>
    /// <param name="invD">The inverse's (1,1) entry on success; zero on refusal.</param>
    /// <param name="invE">The inverse's (1,2) = (2,1) entry on success; zero on refusal.</param>
    /// <param name="invF">The inverse's (2,2) entry on success; zero on refusal.</param>
    /// <returns><see langword="false"/> for an exactly singular matrix or a result outside the signed 64-bit range.</returns>
    public static bool TryInvertSymmetric3(
        long a,
        long b,
        long c,
        long d,
        long e,
        long f,
        int outputFractionShift,
        out long invA,
        out long invB,
        out long invC,
        out long invD,
        out long invE,
        out long invF
    ) {
        BigInteger ba = a, bb = b, bc = c, bd = d, be = e, bf = f;
        var det = ((((((ba * bd) * bf) - ((ba * be) * be)) - ((bb * bb) * bf)) + (((2 * bb) * bc) * be)) - ((bc * bc) * bd));

        if (det.IsZero) {
            invA = 0L;
            invB = 0L;
            invC = 0L;
            invD = 0L;
            invE = 0L;
            invF = 0L;
            return false;
        }

        var c11 = ((bd * bf) - (be * be));
        var c12 = ((bc * be) - (bb * bf));
        var c13 = ((bb * be) - (bc * bd));
        var c22 = ((ba * bf) - (bc * bc));
        var c23 = ((bb * bc) - (ba * be));
        var c33 = ((ba * bd) - (bb * bb));
        // See TrySolveSymmetric2's own note: no output is exposed until every component is known to round.
        var okA = TryRoundRatio(denominator: det, numerator: c11, raw: out var ra, shift: outputFractionShift);
        var okB = TryRoundRatio(denominator: det, numerator: c12, raw: out var rb, shift: outputFractionShift);
        var okC = TryRoundRatio(denominator: det, numerator: c13, raw: out var rc, shift: outputFractionShift);
        var okD = TryRoundRatio(denominator: det, numerator: c22, raw: out var rd, shift: outputFractionShift);
        var okE = TryRoundRatio(denominator: det, numerator: c23, raw: out var re, shift: outputFractionShift);
        var okF = TryRoundRatio(denominator: det, numerator: c33, raw: out var rf, shift: outputFractionShift);

        if (!okA || !okB || !okC || !okD || !okE || !okF) {
            invA = 0L;
            invB = 0L;
            invC = 0L;
            invD = 0L;
            invE = 0L;
            invF = 0L;
            return false;
        }

        invA = ra;
        invB = rb;
        invC = rc;
        invD = rd;
        invE = re;
        invF = rf;
        return true;
    }

    // The shared "round once, refuse rather than wrap" tail every symmetric-solve oracle above narrows through: the
    // exact rational quotient at the requested scale, rounded ties to even by RoundRationalTiesToEven (this module's
    // one tie body), reported UNWRAPPED so the caller can refuse before ever reducing to the 64-bit carrier.
    private static bool TryRoundRatio(BigInteger numerator, BigInteger denominator, int shift, out long raw) {
        var rounded = RoundRationalTiesToEven(denominator: denominator, numerator: (numerator << shift));

        if ((rounded < long.MinValue) || (rounded > long.MaxValue)) {
            raw = 0L;
            return false;
        }

        raw = ((long)rounded);
        return true;
    }

    /// <summary>The exact rational solve of a symmetric 2×2 system by FRACTION-FREE (Bareiss) Gaussian elimination
    /// — the SECOND, algorithmically independent oracle this family carries. <see cref="TrySolveSymmetric2"/> above
    /// and <see cref="FixedSymmetricSolve"/>'s subject both expand the SAME adjugate/cofactor formula
    /// (<c>ad − b²</c>, and its two-term numerators), so a sign error transcribed identically into both is invisible
    /// to <c>Solve2VsOracle</c> and — because the resulting wrong vector can still satisfy <c>K·x ≈ rhs</c> under an
    /// ill-conditioned <c>K</c> — invisible to the residual laws too (a matrix residual bounds component error only
    /// FORWARD; treating a small residual as proof of small component error runs the bound backward, which
    /// cancellation can defeat). <see cref="TryBareissEliminate"/> never expands a determinant or names a cofactor
    /// at all: it row-reduces the augmented matrix directly, so this path and the adjugate path could not fail
    /// together on a cofactor-sign mutation. See <see cref="Puck.Maths.Tests.SymmetricSolveClaims"/>'s dedicated
    /// Bareiss laws for the componentwise comparison against the subject.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="rhsX">The right-hand side's first component.</param>
    /// <param name="rhsY">The right-hand side's second component.</param>
    /// <param name="outputFractionShift">The requested output fraction bit count.</param>
    /// <param name="x">The first solution component on success; zero on refusal.</param>
    /// <param name="y">The second solution component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> for an exactly singular matrix or a result outside the signed 64-bit range.</returns>
    public static bool TryBareissSolveSymmetric2(long a, long b, long d, long rhsX, long rhsY, int outputFractionShift, out long x, out long y) {
        var augmented = new BigInteger[,] {
            { a, b, rhsX },
            { b, d, rhsY },
        };

        if (!TryBareissEliminate(augmented: augmented, order: 2, totalColumns: 3)) {
            x = 0L;
            y = 0L;
            return false;
        }

        BareissBackSubstitute(augmented: augmented, augmentedColumn: 2, denominators: out var denominators, numerators: out var numerators, order: 2);

        var okX = TryRoundRatio(numerator: numerators[0], denominator: denominators[0], shift: outputFractionShift, raw: out var rx);
        var okY = TryRoundRatio(numerator: numerators[1], denominator: denominators[1], shift: outputFractionShift, raw: out var ry);

        if (!okX || !okY) {
            x = 0L;
            y = 0L;
            return false;
        }

        x = rx;
        y = ry;
        return true;
    }
    /// <summary>The 3×3 sibling of <see cref="TryBareissSolveSymmetric2"/>. See it for the independence
    /// argument against both the shared-cofactor oracle above and the residual laws.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="c">The (0,2) = (2,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="e">The (1,2) = (2,1) entry.</param>
    /// <param name="f">The (2,2) entry.</param>
    /// <param name="rhsX">The right-hand side's first component.</param>
    /// <param name="rhsY">The right-hand side's second component.</param>
    /// <param name="rhsZ">The right-hand side's third component.</param>
    /// <param name="outputFractionShift">The requested output fraction bit count.</param>
    /// <param name="x">The first solution component on success; zero on refusal.</param>
    /// <param name="y">The second solution component on success; zero on refusal.</param>
    /// <param name="z">The third solution component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> for an exactly singular matrix or a result outside the signed 64-bit range.</returns>
    public static bool TryBareissSolveSymmetric3(
        long a,
        long b,
        long c,
        long d,
        long e,
        long f,
        long rhsX,
        long rhsY,
        long rhsZ,
        int outputFractionShift,
        out long x,
        out long y,
        out long z
    ) {
        var augmented = new BigInteger[,] {
            { a, b, c, rhsX },
            { b, d, e, rhsY },
            { c, e, f, rhsZ },
        };

        if (!TryBareissEliminate(augmented: augmented, order: 3, totalColumns: 4)) {
            x = 0L;
            y = 0L;
            z = 0L;
            return false;
        }

        BareissBackSubstitute(augmented: augmented, augmentedColumn: 3, denominators: out var denominators, numerators: out var numerators, order: 3);

        var okX = TryRoundRatio(numerator: numerators[0], denominator: denominators[0], shift: outputFractionShift, raw: out var rx);
        var okY = TryRoundRatio(numerator: numerators[1], denominator: denominators[1], shift: outputFractionShift, raw: out var ry);
        var okZ = TryRoundRatio(numerator: numerators[2], denominator: denominators[2], shift: outputFractionShift, raw: out var rz);

        if (!okX || !okY || !okZ) {
            x = 0L;
            y = 0L;
            z = 0L;
            return false;
        }

        x = rx;
        y = ry;
        z = rz;
        return true;
    }
    /// <summary>The exact rational inverse of a symmetric 2×2 matrix by the same fraction-free elimination as
    /// <see cref="TryBareissSolveSymmetric2"/>, solving <c>K·X = I</c> for both columns of <c>X</c> in one
    /// elimination pass over <c>[K | I]</c>. See <see cref="TryBareissSolveSymmetric2"/> for the independence
    /// argument.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="outputFractionShift">The requested output fraction bit count.</param>
    /// <param name="invA">The inverse's (0,0) entry on success; zero on refusal.</param>
    /// <param name="invB">The inverse's (0,1) = (1,0) entry on success; zero on refusal.</param>
    /// <param name="invD">The inverse's (1,1) entry on success; zero on refusal.</param>
    /// <returns><see langword="false"/> for an exactly singular matrix or a result outside the signed 64-bit range.</returns>
    public static bool TryBareissInvertSymmetric2(long a, long b, long d, int outputFractionShift, out long invA, out long invB, out long invD) {
        var augmented = new BigInteger[,] {
            { a, b, 1, 0 },
            { b, d, 0, 1 },
        };

        if (!TryBareissEliminate(augmented: augmented, order: 2, totalColumns: 4)) {
            invA = 0L;
            invB = 0L;
            invD = 0L;
            return false;
        }

        BareissBackSubstitute(augmented: augmented, augmentedColumn: 2, denominators: out var column0Denominators, numerators: out var column0Numerators, order: 2);
        BareissBackSubstitute(augmented: augmented, augmentedColumn: 3, denominators: out var column1Denominators, numerators: out var column1Numerators, order: 2);

        var okA = TryRoundRatio(numerator: column0Numerators[0], denominator: column0Denominators[0], shift: outputFractionShift, raw: out var ra);
        var okB = TryRoundRatio(numerator: column1Numerators[0], denominator: column1Denominators[0], shift: outputFractionShift, raw: out var rb);
        var okD = TryRoundRatio(numerator: column1Numerators[1], denominator: column1Denominators[1], shift: outputFractionShift, raw: out var rd);

        if (!okA || !okB || !okD) {
            invA = 0L;
            invB = 0L;
            invD = 0L;
            return false;
        }

        invA = ra;
        invB = rb;
        invD = rd;
        return true;
    }
    /// <summary>The 3×3 sibling of <see cref="TryBareissInvertSymmetric2"/>, solving <c>K·X = I</c> for all three
    /// distinct columns of <c>X</c> in one elimination pass over <c>[K | I]</c>. See
    /// <see cref="TryBareissSolveSymmetric2"/> for the independence argument.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="c">The (0,2) = (2,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="e">The (1,2) = (2,1) entry.</param>
    /// <param name="f">The (2,2) entry.</param>
    /// <param name="outputFractionShift">The requested output fraction bit count.</param>
    /// <param name="invA">The inverse's (0,0) entry on success; zero on refusal.</param>
    /// <param name="invB">The inverse's (0,1) = (1,0) entry on success; zero on refusal.</param>
    /// <param name="invC">The inverse's (0,2) = (2,0) entry on success; zero on refusal.</param>
    /// <param name="invD">The inverse's (1,1) entry on success; zero on refusal.</param>
    /// <param name="invE">The inverse's (1,2) = (2,1) entry on success; zero on refusal.</param>
    /// <param name="invF">The inverse's (2,2) entry on success; zero on refusal.</param>
    /// <returns><see langword="false"/> for an exactly singular matrix or a result outside the signed 64-bit range.</returns>
    public static bool TryBareissInvertSymmetric3(
        long a,
        long b,
        long c,
        long d,
        long e,
        long f,
        int outputFractionShift,
        out long invA,
        out long invB,
        out long invC,
        out long invD,
        out long invE,
        out long invF
    ) {
        var augmented = new BigInteger[,] {
            { a, b, c, 1, 0, 0 },
            { b, d, e, 0, 1, 0 },
            { c, e, f, 0, 0, 1 },
        };

        if (!TryBareissEliminate(augmented: augmented, order: 3, totalColumns: 6)) {
            invA = 0L;
            invB = 0L;
            invC = 0L;
            invD = 0L;
            invE = 0L;
            invF = 0L;
            return false;
        }

        BareissBackSubstitute(augmented: augmented, augmentedColumn: 3, denominators: out var column0Denominators, numerators: out var column0Numerators, order: 3);
        BareissBackSubstitute(augmented: augmented, augmentedColumn: 4, denominators: out var column1Denominators, numerators: out var column1Numerators, order: 3);
        BareissBackSubstitute(augmented: augmented, augmentedColumn: 5, denominators: out var column2Denominators, numerators: out var column2Numerators, order: 3);

        var okA = TryRoundRatio(numerator: column0Numerators[0], denominator: column0Denominators[0], shift: outputFractionShift, raw: out var ra);
        var okB = TryRoundRatio(numerator: column1Numerators[0], denominator: column1Denominators[0], shift: outputFractionShift, raw: out var rb);
        var okC = TryRoundRatio(numerator: column2Numerators[0], denominator: column2Denominators[0], shift: outputFractionShift, raw: out var rc);
        var okD = TryRoundRatio(numerator: column1Numerators[1], denominator: column1Denominators[1], shift: outputFractionShift, raw: out var rd);
        var okE = TryRoundRatio(numerator: column2Numerators[1], denominator: column2Denominators[1], shift: outputFractionShift, raw: out var re);
        var okF = TryRoundRatio(numerator: column2Numerators[2], denominator: column2Denominators[2], shift: outputFractionShift, raw: out var rf);

        if (!okA || !okB || !okC || !okD || !okE || !okF) {
            invA = 0L;
            invB = 0L;
            invC = 0L;
            invD = 0L;
            invE = 0L;
            invF = 0L;
            return false;
        }

        invA = ra;
        invB = rb;
        invC = rc;
        invD = rd;
        invE = re;
        invF = rf;
        return true;
    }

    /// <summary>In-place fraction-free (Bareiss) Gaussian elimination, WITH PARTIAL PIVOTING, on an
    /// <paramref name="order"/>-by-<paramref name="totalColumns"/> augmented matrix, reducing its leading
    /// <paramref name="order"/>-by-<paramref name="order"/> block to upper-triangular form. Every intermediate entry
    /// stays an EXACT <see cref="BigInteger"/> integer, never a fraction: by the Bareiss/Sylvester identity, each one
    /// is a signed minor of the (possibly row-permuted) matrix, so the division by the immediately preceding pivot
    /// the recurrence performs is always exact — asserted at runtime (a thrown exception, not a stripped
    /// <c>Debug.Assert</c>) rather than merely assumed. A row swap is used whenever the natural pivot is zero but a
    /// later candidate row still has a nonzero entry in that column, so a leading zero on an otherwise-nonsingular
    /// matrix (a symmetric 2×2 with <c>a = 0</c>, <c>b ≠ 0</c>, for instance) does not falsely refuse; a row swap
    /// permutes the system's EQUATIONS, which does not change its solution, so back-substitution afterward needs no
    /// separate sign correction. THE ENTIRE DERIVATION ROUTE IS ROW ELIMINATION: no determinant is expanded and no
    /// cofactor is named anywhere in this method or in <see cref="BareissBackSubstitute"/> — the independence
    /// argument every Bareiss law in this family leans on.</summary>
    /// <param name="augmented">The augmented matrix, mutated in place into upper-triangular form.</param>
    /// <param name="order">The count of leading (matrix) rows and columns.</param>
    /// <param name="totalColumns">The total column count, including every extra (right-hand-side or identity)
    /// column beyond <paramref name="order"/>.</param>
    /// <returns><see langword="false"/> when some column has no nonzero pivot candidate among its remaining rows —
    /// the leading <paramref name="order"/>-by-<paramref name="order"/> block is exactly singular.</returns>
    private static bool TryBareissEliminate(BigInteger[,] augmented, int order, int totalColumns) {
        var previousPivot = BigInteger.One;

        for (var k = 0; (k < order); ++k) {
            var pivotRow = -1;

            for (var candidate = k; (candidate < order); ++candidate) {
                if (!augmented[candidate, k].IsZero) {
                    pivotRow = candidate;
                    break;
                }
            }

            if (pivotRow < 0) { return false; }

            if (pivotRow != k) {
                for (var column = 0; (column < totalColumns); ++column) {
                    (augmented[k, column], augmented[pivotRow, column]) = (augmented[pivotRow, column], augmented[k, column]);
                }
            }

            var pivotValue = augmented[k, k];

            for (var i = (k + 1); (i < order); ++i) {
                var factor = augmented[i, k];

                for (var j = (k + 1); (j < totalColumns); ++j) {
                    var updated = ((pivotValue * augmented[i, j]) - (factor * augmented[k, j]));
                    var quotient = BigInteger.DivRem(dividend: updated, divisor: previousPivot, remainder: out var remainder);

                    if (!remainder.IsZero) {
                        throw new InvalidOperationException(message: "Bareiss elimination produced a non-exact division; the fraction-free identity does not hold for this input.");
                    }

                    augmented[i, j] = quotient;
                }

                augmented[i, k] = BigInteger.Zero;
            }

            previousPivot = pivotValue;
        }

        return true;
    }
    /// <summary>Back-substitutes ONE column of <see cref="TryBareissEliminate"/>'s upper-triangular result into an
    /// exact, UNREDUCED rational component per row. Unlike the forward elimination this step is not fraction-free —
    /// the solution itself is generally not an integer — but every step is still exact <see cref="BigInteger"/>
    /// arithmetic (plain cross-multiplied fraction subtraction), never a rounding; the one rounding happens later, in
    /// <see cref="TryRoundRatio"/>.</summary>
    /// <param name="augmented">The eliminated augmented matrix from <see cref="TryBareissEliminate"/>.</param>
    /// <param name="order">The count of leading (matrix) rows and columns.</param>
    /// <param name="augmentedColumn">The extra column (at or past index <paramref name="order"/>) to solve for.</param>
    /// <param name="numerators">Each row's solution numerator, index-aligned with <paramref name="denominators"/>.</param>
    /// <param name="denominators">Each row's solution denominator (never zero), index-aligned with
    /// <paramref name="numerators"/>.</param>
    private static void BareissBackSubstitute(BigInteger[,] augmented, int order, int augmentedColumn, out BigInteger[] numerators, out BigInteger[] denominators) {
        var resultNumerators = new BigInteger[order];
        var resultDenominators = new BigInteger[order];

        for (var row = (order - 1); (row >= 0); --row) {
            var accumulatorNumerator = augmented[row, augmentedColumn];
            var accumulatorDenominator = BigInteger.One;

            for (var column = (row + 1); (column < order); ++column) {
                var coefficient = augmented[row, column];

                if (coefficient.IsZero) { continue; }

                var termNumerator = (coefficient * resultNumerators[column]);
                var termDenominator = resultDenominators[column];

                accumulatorNumerator = ((accumulatorNumerator * termDenominator) - (termNumerator * accumulatorDenominator));
                accumulatorDenominator *= termDenominator;
            }

            accumulatorDenominator *= augmented[row, row];

            resultNumerators[row] = accumulatorNumerator;
            resultDenominators[row] = accumulatorDenominator;
        }

        numerators = resultNumerators;
        denominators = resultDenominators;
    }

    /// <summary>The reference mixed-scale product: ONE ties-to-even rounding of the exact value
    /// <c>a·b·2^(fractionBitsOut − fractionBitsA − fractionBitsB)</c>, reported both as the unwrapped
    /// representability verdict and as the value wrapped to the signed 64-bit carrier.</summary>
    /// <param name="a">The first factor's raw.</param>
    /// <param name="fractionBitsA">The first factor's fraction bit count.</param>
    /// <param name="b">The second factor's raw.</param>
    /// <param name="fractionBitsB">The second factor's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <returns>Whether the exact rounded product fits the signed 64-bit raw, and that product wrapped to it.</returns>
    /// <remarks>Shares nothing with the subject: the whole product is formed in arbitrary width and the single
    /// rounding is one <see cref="RoundRationalTiesToEven"/> against an explicit power-of-two denominator, where the
    /// subject accumulates a sign-plus-<see cref="UInt128"/> magnitude and settles the tie by inspecting the discarded
    /// bits against a half-unit it forms itself. Callers are expected to keep the three counts inside a sane band; the
    /// power-of-two denominator here is built directly from them.</remarks>
    public static (bool Fits, long Raw) MixedScaleProduct(long a, int fractionBitsA, long b, int fractionBitsB, int fractionBitsOut) {
        var exact = ExactMixedScale(product: (((BigInteger)a) * b), shift: ((((long)fractionBitsOut) - fractionBitsA) - fractionBitsB));

        return (((exact >= long.MinValue) && (exact <= long.MaxValue)), WrapToRaw(value: exact));
    }
    /// <summary>The reference mixed-scale product of THREE factors at independent scales — one ties-to-even rounding of
    /// the exact triple product at the requested scale.</summary>
    /// <param name="a">The first factor's raw.</param>
    /// <param name="fractionBitsA">The first factor's fraction bit count.</param>
    /// <param name="b">The second factor's raw.</param>
    /// <param name="fractionBitsB">The second factor's fraction bit count.</param>
    /// <param name="c">The third factor's raw.</param>
    /// <param name="fractionBitsC">The third factor's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <returns>Whether the exact triple product's magnitude stays inside <c>2^128</c> (the width the subject declines
    /// past), whether the exact rounded value fits the signed 64-bit raw, and that value.</returns>
    public static (bool WidthFits, bool Fits, BigInteger Exact) MixedScaleTripleProduct(
        long a,
        int fractionBitsA,
        long b,
        int fractionBitsB,
        long c,
        int fractionBitsC,
        int fractionBitsOut
    ) {
        var product = ((((BigInteger)a) * b) * c);
        var exact = ExactMixedScale(product: product, shift: (((((long)fractionBitsOut) - fractionBitsA) - fractionBitsB) - fractionBitsC));

        return (
            (BigInteger.Abs(value: product) < (BigInteger.One << 128)),
            ((exact >= long.MinValue) && (exact <= long.MaxValue)),
            exact
        );
    }
    /// <summary>The reference mixed-scale dot product of two three-component raw vectors: the three exact products
    /// are summed in arbitrary width and rounded once at the requested scale.</summary>
    /// <param name="ax">The first vector's X raw.</param>
    /// <param name="ay">The first vector's Y raw.</param>
    /// <param name="az">The first vector's Z raw.</param>
    /// <param name="fractionBitsA">The first vector's fraction bit count.</param>
    /// <param name="bx">The second vector's X raw.</param>
    /// <param name="by">The second vector's Y raw.</param>
    /// <param name="bz">The second vector's Z raw.</param>
    /// <param name="fractionBitsB">The second vector's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <returns>Whether the exact rounded dot product fits the signed 64-bit raw, and that product wrapped to it.</returns>
    public static (bool Fits, long Raw) MixedScaleDotProduct(
        long ax,
        long ay,
        long az,
        int fractionBitsA,
        long bx,
        long by,
        long bz,
        int fractionBitsB,
        int fractionBitsOut
    ) {
        var exact = ExactMixedScale(
            product: (((((BigInteger)ax) * bx) + (((BigInteger)ay) * by)) + (((BigInteger)az) * bz)),
            shift: ((((long)fractionBitsOut) - fractionBitsA) - fractionBitsB)
        );

        return (((exact >= long.MinValue) && (exact <= long.MaxValue)), WrapToRaw(value: exact));
    }
    /// <summary>The reference reciprocal of a positive raw carried at one fixed-point scale, rounded once onto a
    /// second fixed-point scale.</summary>
    /// <param name="value">The positive raw to invert.</param>
    /// <param name="fractionBitsIn">The operand's fraction bit count.</param>
    /// <param name="fractionBitsOut">The result's fraction bit count.</param>
    /// <returns>Whether the exact rounded reciprocal fits the signed 64-bit raw, and that reciprocal.</returns>
    public static (bool Fits, BigInteger Exact) ScaledReciprocal(long value, int fractionBitsIn, int fractionBitsOut) {
        var exact = RoundRationalTiesToEven(
            numerator: (BigInteger.One << (fractionBitsIn + fractionBitsOut)),
            denominator: value
        );

        return (((exact >= long.MinValue) && (exact <= long.MaxValue)), exact);
    }
    /// <summary>The exact rational <c>numerator / denominator</c> rounded UP — the reference every directed-up law
    /// states its bound against.</summary>
    /// <param name="numerator">The exact numerator.</param>
    /// <param name="denominator">The exact denominator, which must be strictly positive.</param>
    /// <returns>The least integer at or above the exact quotient.</returns>
    /// <remarks>Written as a floor plus a remainder test rather than as <c>(n + d − 1)/d</c>, which is the shape a
    /// fixed-width kernel reaches for and the shape that overflows; the subject instead divides and carries the
    /// quotient up when anything was discarded.</remarks>
    public static BigInteger CeilingRational(BigInteger numerator, BigInteger denominator) {
        var quotient = FloorQuotient(denominator: denominator, numerator: numerator);

        return (((quotient * denominator) == numerator) ? quotient : (quotient + BigInteger.One));
    }
    /// <summary>The least integer at or above the square root of a non-negative exact value, by a BRACKETED INTEGER
    /// SEARCH whose predicate is one exact squaring — no square root is ever taken.</summary>
    /// <param name="value">The radicand, which must be non-negative.</param>
    /// <returns>The ceiling of the exact square root.</returns>
    /// <remarks>Deliberately not <see cref="IntegerSquareRoot"/> with a repair on top: that one descends by Newton's
    /// method from a bit-length seed, and a ceiling built on its floor would inherit that descent. The answer here is
    /// the least <c>t</c> with <c>t² ≥ value</c>, found by doubling to a bracket and bisecting it — the same shape
    /// <see cref="NearestIntegerRoot"/> uses against a different predicate.</remarks>
    public static BigInteger CeilingIntegerRoot(BigInteger value) {
        bool AtLeast(BigInteger candidate) => ((candidate * candidate) >= value);

        if (value.Sign <= 0) { return BigInteger.Zero; }

        var low = BigInteger.Zero;
        var high = BigInteger.One;

        while (!AtLeast(candidate: high)) {
            low = high;
            high <<= 1;
        }

        while ((high - low) > BigInteger.One) {
            var middle = ((low + high) >> 1);

            if (AtLeast(candidate: middle)) { high = middle; } else { low = middle; }
        }

        return high;
    }
    /// <summary>The exact symmetric 2×2 matrix-times-vector product, each component ONE ties-to-even rounding of the
    /// exact value at the requested scale.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="vX">The vector's first component.</param>
    /// <param name="vY">The vector's second component.</param>
    /// <param name="fractionBitsMatrix">The matrix entries' fraction bit count.</param>
    /// <param name="fractionBitsVector">The vector components' fraction bit count.</param>
    /// <param name="fractionBitsOut">The result components' fraction bit count.</param>
    /// <param name="x">The first component on success; zero on refusal.</param>
    /// <param name="y">The second component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when either component leaves the signed 64-bit range.</returns>
    public static bool TryApplySymmetric2(
        long a,
        long b,
        long d,
        long vX,
        long vY,
        int fractionBitsMatrix,
        int fractionBitsVector,
        int fractionBitsOut,
        out long x,
        out long y
    ) {
        var shift = ((((long)fractionBitsOut) - fractionBitsMatrix) - fractionBitsVector);
        var okX = TryMixedScaleSum(exact: ExactMixedScale(product: ((((BigInteger)a) * vX) + (((BigInteger)b) * vY)), shift: shift), raw: out var rx);
        var okY = TryMixedScaleSum(exact: ExactMixedScale(product: ((((BigInteger)b) * vX) + (((BigInteger)d) * vY)), shift: shift), raw: out var ry);

        if (!okX || !okY) {
            x = 0L;
            y = 0L;
            return false;
        }

        x = rx;
        y = ry;
        return true;
    }
    /// <summary>The exact symmetric 3×3 matrix-times-vector product. See <see cref="TryApplySymmetric2"/>.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="c">The (0,2) = (2,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="e">The (1,2) = (2,1) entry.</param>
    /// <param name="f">The (2,2) entry.</param>
    /// <param name="vX">The vector's first component.</param>
    /// <param name="vY">The vector's second component.</param>
    /// <param name="vZ">The vector's third component.</param>
    /// <param name="fractionBitsMatrix">The matrix entries' fraction bit count.</param>
    /// <param name="fractionBitsVector">The vector components' fraction bit count.</param>
    /// <param name="fractionBitsOut">The result components' fraction bit count.</param>
    /// <param name="x">The first component on success; zero on refusal.</param>
    /// <param name="y">The second component on success; zero on refusal.</param>
    /// <param name="z">The third component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when any component leaves the signed 64-bit range.</returns>
    public static bool TryApplySymmetric3(
        long a,
        long b,
        long c,
        long d,
        long e,
        long f,
        long vX,
        long vY,
        long vZ,
        int fractionBitsMatrix,
        int fractionBitsVector,
        int fractionBitsOut,
        out long x,
        out long y,
        out long z
    ) {
        var shift = ((((long)fractionBitsOut) - fractionBitsMatrix) - fractionBitsVector);
        var okX = TryMixedScaleSum(exact: ExactMixedScale(product: (((((BigInteger)a) * vX) + (((BigInteger)b) * vY)) + (((BigInteger)c) * vZ)), shift: shift), raw: out var rx);
        var okY = TryMixedScaleSum(exact: ExactMixedScale(product: (((((BigInteger)b) * vX) + (((BigInteger)d) * vY)) + (((BigInteger)e) * vZ)), shift: shift), raw: out var ry);
        var okZ = TryMixedScaleSum(exact: ExactMixedScale(product: (((((BigInteger)c) * vX) + (((BigInteger)e) * vY)) + (((BigInteger)f) * vZ)), shift: shift), raw: out var rz);

        if (!okX || !okY || !okZ) {
            x = 0L;
            y = 0L;
            z = 0L;
            return false;
        }

        x = rx;
        y = ry;
        z = rz;
        return true;
    }

    // The scaled rational π the mass-property subject substitutes for the transcendental, read from the subject's own
    // declaration rather than transcribed as digits — a transcription would let the two drift while every law stayed
    // green. This is the ONE value the mass-property oracles share with the subject; that the constant IS the
    // correctly rounded π is a separate law, decided against this module's own Machin enclosure.
    private static readonly BigInteger PiNumerator = FixedQ4816.PiQ61;
    private static readonly BigInteger PiDenominator = (BigInteger.One << FixedQ4816.PiQ61FractionBitCount);

    /// <summary>The exact volumes of the four solid primitives, each ONE ties-to-even rounding at the requested
    /// scale.</summary>
    /// <param name="shape">Which primitive: <c>0</c> sphere, <c>1</c> box, <c>2</c> cylinder, <c>3</c> capsule.</param>
    /// <param name="first">The sphere's or cylinder's or capsule's radius, or the box's X half-extent.</param>
    /// <param name="second">The cylinder's height, the capsule's hemisphere-centre distance, or the box's Y half-extent.</param>
    /// <param name="third">The box's Z half-extent; ignored by the other shapes.</param>
    /// <param name="fractionBitsLength">The lengths' fraction bit count.</param>
    /// <param name="fractionBitsVolume">The volume's fraction bit count.</param>
    /// <param name="volume">The volume raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the rounded volume leaves the signed 64-bit range.</returns>
    /// <remarks>Every volume is assembled from the geometric definition — a box from its FULL extents rather than
    /// three doubled half-extents folded into a leading eight, a capsule as a cylinder PLUS a whole sphere rather than
    /// as one collapsed fraction — so a mis-folded constant in the subject cannot be mirrored here.</remarks>
    public static bool TryPrimitiveVolume(int shape, long first, long second, long third, int fractionBitsLength, int fractionBitsVolume, out long volume) {
        var lengthScale = (BigInteger.One << (3 * fractionBitsLength));

        var (numerator, denominator) = shape switch {
            0 => (((4 * PiNumerator) * BigInteger.Pow(exponent: 3, value: first)), ((3 * PiDenominator) * lengthScale)),
            1 => ((((2 * ((BigInteger)first)) * (2 * ((BigInteger)second))) * (2 * ((BigInteger)third))), lengthScale),
            2 => ((((PiNumerator * first) * first) * second), (PiDenominator * lengthScale)),
            _ => ((((((3 * PiNumerator) * first) * first) * second) + ((4 * PiNumerator) * BigInteger.Pow(exponent: 3, value: first))), ((3 * PiDenominator) * lengthScale)),
        };

        return TryRoundRatio(denominator: denominator, numerator: numerator, raw: out volume, shift: fractionBitsVolume);
    }
    /// <summary>The exact mass and inertia of a solid sphere about its centre.</summary>
    /// <param name="density">The density raw.</param>
    /// <param name="fractionBitsDensity">The density's fraction bit count.</param>
    /// <param name="radius">The radius raw.</param>
    /// <param name="fractionBitsLength">The radius's fraction bit count.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="fractionBitsInertia">The inertia's fraction bit count.</param>
    /// <param name="mass">The mass raw on success; zero on refusal.</param>
    /// <param name="inertia">The inertia raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when either rounded result leaves the signed 64-bit range.</returns>
    /// <remarks>The inertia is taken as <c>(2/5)·M·r²</c> against the exact rational mass, where the subject collapses
    /// the whole chain into <c>(8/15)·ρ·π·r⁵</c>; the two routes cannot mis-fold the same constant.</remarks>
    public static bool TrySphereBody(
        long density,
        int fractionBitsDensity,
        long radius,
        int fractionBitsLength,
        int fractionBitsMass,
        int fractionBitsInertia,
        out long mass,
        out long inertia
    ) {
        var (massNumerator, massDenominator) = ScaledMass(
            volumeNumerator: ((4 * PiNumerator) * BigInteger.Pow(exponent: 3, value: radius)),
            volumeDenominator: ((3 * PiDenominator) << (3 * fractionBitsLength)),
            density: density,
            fractionBitsDensity: fractionBitsDensity
        );
        var okMass = TryRoundRatio(denominator: massDenominator, numerator: massNumerator, raw: out var roundedMass, shift: fractionBitsMass);
        var okInertia = TryRoundRatio(
            denominator: ((5 * massDenominator) << (2 * fractionBitsLength)),
            numerator: (((2 * massNumerator) * radius) * radius),
            raw: out var roundedInertia,
            shift: fractionBitsInertia
        );

        if (!okMass || !okInertia) {
            mass = 0L;
            inertia = 0L;
            return false;
        }

        mass = roundedMass;
        inertia = roundedInertia;
        return true;
    }
    /// <summary>The exact mass and diagonal inertia of a solid box about its centre, from its half-extents.</summary>
    /// <param name="density">The density raw.</param>
    /// <param name="fractionBitsDensity">The density's fraction bit count.</param>
    /// <param name="halfX">The X half-extent raw.</param>
    /// <param name="halfY">The Y half-extent raw.</param>
    /// <param name="halfZ">The Z half-extent raw.</param>
    /// <param name="fractionBitsLength">The half-extents' fraction bit count.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="fractionBitsInertia">The inertia's fraction bit count.</param>
    /// <param name="mass">The mass raw on success; zero on refusal.</param>
    /// <param name="ixx">The <c>(0,0)</c> inertia raw on success; zero on refusal.</param>
    /// <param name="iyy">The <c>(1,1)</c> inertia raw on success; zero on refusal.</param>
    /// <param name="izz">The <c>(2,2)</c> inertia raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when any rounded result leaves the signed 64-bit range.</returns>
    /// <remarks>Everything is stated in FULL extents — <c>V = Lx·Ly·Lz</c> and <c>I_xx = (M/12)(Ly² + Lz²)</c>, the
    /// textbook forms — where the subject works in half-extents with the factors of two and twelve already folded to
    /// eight and three. A mis-folded halving is exactly what that difference catches.</remarks>
    public static bool TryBoxBody(
        long density,
        int fractionBitsDensity,
        long halfX,
        long halfY,
        long halfZ,
        int fractionBitsLength,
        int fractionBitsMass,
        int fractionBitsInertia,
        out long mass,
        out long ixx,
        out long iyy,
        out long izz
    ) {
        BigInteger lx = (2 * ((BigInteger)halfX)), ly = (2 * ((BigInteger)halfY)), lz = (2 * ((BigInteger)halfZ));

        var (massNumerator, massDenominator) = ScaledMass(
            volumeNumerator: ((lx * ly) * lz),
            volumeDenominator: (BigInteger.One << (3 * fractionBitsLength)),
            density: density,
            fractionBitsDensity: fractionBitsDensity
        );
        var inertiaDenominator = ((12 * massDenominator) << (2 * fractionBitsLength));
        var okMass = TryRoundRatio(denominator: massDenominator, numerator: massNumerator, raw: out var roundedMass, shift: fractionBitsMass);
        var okXX = TryRoundRatio(denominator: inertiaDenominator, numerator: (massNumerator * ((ly * ly) + (lz * lz))), raw: out var roundedXX, shift: fractionBitsInertia);
        var okYY = TryRoundRatio(denominator: inertiaDenominator, numerator: (massNumerator * ((lx * lx) + (lz * lz))), raw: out var roundedYY, shift: fractionBitsInertia);
        var okZZ = TryRoundRatio(denominator: inertiaDenominator, numerator: (massNumerator * ((lx * lx) + (ly * ly))), raw: out var roundedZZ, shift: fractionBitsInertia);

        if (!okMass || !okXX || !okYY || !okZZ) {
            mass = 0L;
            ixx = 0L;
            iyy = 0L;
            izz = 0L;
            return false;
        }

        mass = roundedMass;
        ixx = roundedXX;
        iyy = roundedYY;
        izz = roundedZZ;
        return true;
    }
    /// <summary>The exact mass and the two distinct inertia moments of a solid cylinder about its centre.</summary>
    /// <param name="density">The density raw.</param>
    /// <param name="fractionBitsDensity">The density's fraction bit count.</param>
    /// <param name="radius">The radius raw.</param>
    /// <param name="height">The height raw.</param>
    /// <param name="fractionBitsLength">The lengths' fraction bit count.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="fractionBitsInertia">The inertia's fraction bit count.</param>
    /// <param name="mass">The mass raw on success; zero on refusal.</param>
    /// <param name="axial">The moment about the axis on success; zero on refusal.</param>
    /// <param name="perpendicular">The moment about either transverse axis on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when any rounded result leaves the signed 64-bit range.</returns>
    public static bool TryCylinderBody(
        long density,
        int fractionBitsDensity,
        long radius,
        long height,
        int fractionBitsLength,
        int fractionBitsMass,
        int fractionBitsInertia,
        out long mass,
        out long axial,
        out long perpendicular
    ) {
        var (massNumerator, massDenominator) = ScaledMass(
            density: density,
            fractionBitsDensity: fractionBitsDensity,
            volumeDenominator: (PiDenominator << (3 * fractionBitsLength)),
            volumeNumerator: (((PiNumerator * radius) * radius) * height)
        );
        var squaredLength = (BigInteger.One << (2 * fractionBitsLength));
        var okMass = TryRoundRatio(denominator: massDenominator, numerator: massNumerator, raw: out var roundedMass, shift: fractionBitsMass);
        var okAxial = TryRoundRatio(
            denominator: ((2 * massDenominator) * squaredLength),
            numerator: ((massNumerator * radius) * radius),
            raw: out var roundedAxial,
            shift: fractionBitsInertia
        );
        var okPerpendicular = TryRoundRatio(
            denominator: ((12 * massDenominator) * squaredLength),
            numerator: (massNumerator * (((3 * ((BigInteger)radius)) * radius) + (((BigInteger)height) * height))),
            raw: out var roundedPerpendicular,
            shift: fractionBitsInertia
        );

        if (!okMass || !okAxial || !okPerpendicular) {
            mass = 0L;
            axial = 0L;
            perpendicular = 0L;
            return false;
        }

        mass = roundedMass;
        axial = roundedAxial;
        perpendicular = roundedPerpendicular;
        return true;
    }
    /// <summary>The exact mass and the two distinct inertia moments of a solid capsule about its centre, assembled
    /// from its PARTS rather than from a closed form.</summary>
    /// <param name="density">The density raw.</param>
    /// <param name="fractionBitsDensity">The density's fraction bit count.</param>
    /// <param name="radius">The radius raw.</param>
    /// <param name="centerDistance">The distance between the hemisphere centres.</param>
    /// <param name="fractionBitsLength">The lengths' fraction bit count.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="fractionBitsInertia">The inertia's fraction bit count.</param>
    /// <param name="mass">The mass raw on success; zero on refusal.</param>
    /// <param name="axial">The moment about the axis on success; zero on refusal.</param>
    /// <param name="perpendicular">The moment about either transverse axis on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when any rounded result leaves the signed 64-bit range.</returns>
    /// <remarks>THE INDEPENDENT DERIVATION, and the reason this oracle earns its place. A hemisphere's moment about the
    /// centre of its own FLAT FACE is <c>(2/5)·m·r²</c> — half a whole sphere's mass carrying half its moment about the
    /// same diameter — and its centroid sits <c>3r/8</c> from that face, so the parallel-axis theorem gives its
    /// centroidal moment as <c>(2/5)m r² − m(3r/8)²</c> and a second transfer carries it out to the capsule's own
    /// centre, a distance <c>h/2 + 3r/8</c> away. The coefficient <c>83/320</c> the subject states NEVER APPEARS here;
    /// agreement is what proves it, together with the identity <c>83/320 + (3/8)² = 2/5</c> that makes the capsule
    /// collapse onto the sphere at <c>h = 0</c>.</remarks>
    public static bool TryCapsuleBody(
        long density,
        int fractionBitsDensity,
        long radius,
        long centerDistance,
        int fractionBitsLength,
        int fractionBitsMass,
        int fractionBitsInertia,
        out long mass,
        out long axial,
        out long perpendicular
    ) {
        BigInteger r = radius, h = centerDistance;
        var lengthCube = (BigInteger.One << (3 * fractionBitsLength));
        var squaredLength = (BigInteger.One << (2 * fractionBitsLength));

        // The two parts, each as an exact rational mass over its own denominator, then put over a common one.
        var (cylinderMass, cylinderDenominator) = ScaledMass(
            density: density,
            fractionBitsDensity: fractionBitsDensity,
            volumeDenominator: (PiDenominator * lengthCube),
            volumeNumerator: (((PiNumerator * r) * r) * h)
        );
        var (sphereMass, sphereDenominator) = ScaledMass(
            volumeNumerator: ((4 * PiNumerator) * BigInteger.Pow(exponent: 3, value: r)),
            volumeDenominator: ((3 * PiDenominator) * lengthCube),
            density: density,
            fractionBitsDensity: fractionBitsDensity
        );
        var commonDenominator = (cylinderDenominator * sphereDenominator);
        var cylinder = (cylinderMass * sphereDenominator);
        var sphere = (sphereMass * cylinderDenominator);

        var okMass = TryRoundRatio(denominator: commonDenominator, numerator: (cylinder + sphere), raw: out var roundedMass, shift: fractionBitsMass);

        // Axial: the cylinder's ½·m·r² plus BOTH hemispheres' (2/5)·m·r² — a hemisphere carries the whole sphere's
        // axial coefficient because the axis is its own symmetry axis.
        var okAxial = TryRoundRatio(
            denominator: ((10 * commonDenominator) * squaredLength),
            numerator: ((((5 * cylinder) + (4 * sphere)) * r) * r),
            raw: out var roundedAxial,
            shift: fractionBitsInertia
        );

        // Perpendicular: the cylinder's (m/12)(3r² + h²), plus each hemisphere's own centroidal moment
        // (2/5)m_h·r² − m_h(3r/8)² carried out to the capsule centre by (h/2 + 3r/8)². Over the common denominator
        // 960, with both hemispheres folded into the sphere mass and each of the three coefficients kept SEPARATE:
        // 2/5 is 384/960, (3/8)² is 135/960, and 1/64 is 15/960. The subject never writes any of them — it carries
        // the single folded 83/320, which is exactly what 384 − 135 = 249 reconstructs.
        var offset = ((4 * h) + (3 * r));
        var hemispherePart = ((((384 * r) * r) - ((135 * r) * r)) + ((15 * offset) * offset));
        var okPerpendicular = TryRoundRatio(
            denominator: ((960 * commonDenominator) * squaredLength),
            numerator: (((80 * cylinder) * (((3 * r) * r) + (h * h))) + (sphere * hemispherePart)),
            raw: out var roundedPerpendicular,
            shift: fractionBitsInertia
        );

        if (!okMass || !okAxial || !okPerpendicular) {
            mass = 0L;
            axial = 0L;
            perpendicular = 0L;
            return false;
        }

        mass = roundedMass;
        axial = roundedAxial;
        perpendicular = roundedPerpendicular;
        return true;
    }
    /// <summary>The exact parallel-axis transfer of a symmetric inertia tensor, each entry one ties-to-even
    /// rounding.</summary>
    /// <param name="entries">The six distinct entries in the order <c>xx, yy, zz, xy, xz, yz</c>.</param>
    /// <param name="fractionBitsInertia">The entries' fraction bit count.</param>
    /// <param name="mass">The body's mass raw.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="offsets">The displacement's three components.</param>
    /// <param name="fractionBitsLength">The displacement's fraction bit count.</param>
    /// <param name="transferred">The six transferred entries, in the same order.</param>
    /// <returns><see langword="false"/> when any transferred entry leaves the signed 64-bit range.</returns>
    /// <remarks>Stated in the general tensor form <c>I' = I + m(|d|²·δ − d⊗d)</c> with <c>|d|²</c> formed once as
    /// <c>dx² + dy² + dz²</c>, where the subject writes each diagonal entry's own pre-expanded pair — a mis-assigned
    /// axis in that expansion is what the difference catches. This is a TRANSCRIPTION of the same theorem, not an
    /// independent derivation of it, and the declaration says so.</remarks>
    public static bool TryTranslateInertia(
        ReadOnlySpan<long> entries,
        int fractionBitsInertia,
        long mass,
        int fractionBitsMass,
        ReadOnlySpan<long> offsets,
        int fractionBitsLength,
        Span<long> transferred
    ) {
        BigInteger dx = offsets[0], dy = offsets[1], dz = offsets[2];
        var squared = (((dx * dx) + (dy * dy)) + (dz * dz));
        var transferShift = (fractionBitsMass + (2 * fractionBitsLength));
        var denominator = (BigInteger.One << transferShift);
        Span<BigInteger> terms = [
            (squared - (dx * dx)),
            (squared - (dy * dy)),
            (squared - (dz * dz)),
            -(dx * dy),
            -(dx * dz),
            -(dy * dz),
        ];
        var complete = true;

        for (var index = 0; (index < 6); ++index) {
            complete &= TryRoundRatio(
                numerator: ((((BigInteger)entries[index]) << transferShift) + ((((BigInteger)mass) * terms[index]) << fractionBitsInertia)),
                denominator: denominator,
                shift: 0,
                raw: out var entry
            );
            transferred[index] = entry;
        }

        if (!complete) { transferred.Clear(); }

        return complete;
    }
    /// <summary>The exact compound accumulation: the summed mass, the composite centre of mass, and the inertia tensor
    /// about that centre, each one ties-to-even rounding.</summary>
    /// <param name="parts">The parts.</param>
    /// <param name="fractionBitsMass">The masses' fraction bit count.</param>
    /// <param name="fractionBitsLength">The centres' fraction bit count.</param>
    /// <param name="fractionBitsInertia">The inertia entries' fraction bit count.</param>
    /// <param name="center">The composite centre's three components.</param>
    /// <param name="tensor">The composite tensor's six entries, in the order <c>xx, yy, zz, xy, xz, yz</c>.</param>
    /// <param name="mass">The composite mass raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when any result leaves the signed 64-bit range.</returns>
    /// <remarks>ORIGIN-FIRST, the opposite accumulation order from the subject: every part's tensor is carried OUT to
    /// the shared origin, the parts are summed there, and the total is carried back IN to the composite centre by one
    /// reverse transfer — where the subject transfers each part directly to the centre. The two routes agree only if
    /// the transfer and the centre are both right.</remarks>
    public static bool TryCompound(
        ReadOnlySpan<FixedMassProperties.CompoundPart> parts,
        int fractionBitsMass,
        int fractionBitsLength,
        int fractionBitsInertia,
        Span<long> center,
        Span<long> tensor,
        out long mass
    ) {
        var totalMass = BigInteger.Zero;
        Span<BigInteger> moment = [BigInteger.Zero, BigInteger.Zero, BigInteger.Zero];
        Span<BigInteger> atOrigin = [BigInteger.Zero, BigInteger.Zero, BigInteger.Zero, BigInteger.Zero, BigInteger.Zero, BigInteger.Zero];
        var transferShift = (fractionBitsMass + (2 * fractionBitsLength));
        var transferScale = (BigInteger.One << transferShift);
        var inertiaScale = (BigInteger.One << fractionBitsInertia);

        foreach (var part in parts) {
            BigInteger partMass = part.Mass, cx = part.CenterX, cy = part.CenterY, cz = part.CenterZ;
            var squared = (((cx * cx) + (cy * cy)) + (cz * cz));

            totalMass += partMass;
            moment[0] += (partMass * cx);
            moment[1] += (partMass * cy);
            moment[2] += (partMass * cz);

            // Each entry carried OUT to the shared origin, over the common denominator 2^(fractionBitsMass +
            // 2·fractionBitsLength) at the inertia raw scale.
            atOrigin[0] += ((((BigInteger)part.Ixx) * transferScale) + ((partMass * (squared - (cx * cx))) * inertiaScale));
            atOrigin[1] += ((((BigInteger)part.Iyy) * transferScale) + ((partMass * (squared - (cy * cy))) * inertiaScale));
            atOrigin[2] += ((((BigInteger)part.Izz) * transferScale) + ((partMass * (squared - (cz * cz))) * inertiaScale));
            atOrigin[3] += ((((BigInteger)part.Ixy) * transferScale) - (((partMass * cx) * cy) * inertiaScale));
            atOrigin[4] += ((((BigInteger)part.Ixz) * transferScale) - (((partMass * cx) * cz) * inertiaScale));
            atOrigin[5] += ((((BigInteger)part.Iyz) * transferScale) - (((partMass * cy) * cz) * inertiaScale));
        }

        var complete = TryRoundRatio(numerator: totalMass, denominator: BigInteger.One, shift: 0, raw: out mass);

        for (var axis = 0; (axis < 3); ++axis) {
            complete &= TryRoundRatio(numerator: moment[axis], denominator: totalMass, shift: 0, raw: out var component);
            center[axis] = component;
        }

        // The reverse transfer, from the origin back IN to the exact composite centre C = moment/totalMass. Everything
        // is put over totalMass so C's own denominator never rounds.
        var centerSquared = (((moment[0] * moment[0]) + (moment[1] * moment[1])) + (moment[2] * moment[2]));
        Span<BigInteger> back = [
            (centerSquared - (moment[0] * moment[0])),
            (centerSquared - (moment[1] * moment[1])),
            (centerSquared - (moment[2] * moment[2])),
            -(moment[0] * moment[1]),
            -(moment[0] * moment[2]),
            -(moment[1] * moment[2]),
        ];

        for (var index = 0; (index < 6); ++index) {
            complete &= TryRoundRatio(
                numerator: ((atOrigin[index] * totalMass) - (back[index] * inertiaScale)),
                denominator: (totalMass * transferScale),
                shift: 0,
                raw: out var entry
            );
            tensor[index] = entry;
        }

        if (!complete) {
            center.Clear();
            tensor.Clear();
            mass = 0L;
        }

        return complete;
    }

    // The exact rounded value of an integer product scaled by a signed power of two — the one place the mixed-scale
    // references decide their single rounding, against a power-of-two denominator rather than a discarded-bit test.
    private static BigInteger ExactMixedScale(BigInteger product, long shift) =>
        ((shift >= 0L)
            ? (product << ((int)shift))
            : RoundRationalTiesToEven(numerator: product, denominator: (BigInteger.One << ((int)-shift))));
    private static bool TryMixedScaleSum(BigInteger exact, out long raw) {
        if ((exact < long.MinValue) || (exact > long.MaxValue)) {
            raw = 0L;
            return false;
        }

        raw = ((long)exact);
        return true;
    }
    // A shape's mass as an exact rational: its volume times the density, with the density's own scale folded into the
    // denominator.
    private static (BigInteger Numerator, BigInteger Denominator) ScaledMass(BigInteger volumeNumerator, BigInteger volumeDenominator, long density, int fractionBitsDensity) =>
        ((volumeNumerator * density), (volumeDenominator << fractionBitsDensity));
    // The 2^(2^-i) ladder by repeated integer square roots of two, in one direction.
    private static BigInteger[] BuildLadder(bool ceiling) {
        var scale = (BigInteger.One << SeriesBitCount);
        var ladder = new BigInteger[(LadderDepth + 1)];

        ladder[0] = (scale << 1);

        for (var level = 1; (level <= LadderDepth); ++level) {
            var squared = (ladder[(level - 1)] * scale);
            var root = IntegerSquareRoot(value: squared);

            ladder[level] = ((ceiling && ((root * root) != squared)) ? (root + BigInteger.One) : root);
        }

        return ladder;
    }
}
