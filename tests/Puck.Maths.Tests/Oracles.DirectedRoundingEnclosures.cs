using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    // ---- the signed Q48.16 carrier: exact reference arithmetic, and directed-rounding enclosures for the
    // transcendentals, whose kernels are polynomial approximations with no correctly-rounded raw to be compared against ----

    /// <summary>An exact enclosure of a real value on a stated fixed-point grid: the true value's scaled form lies in
    /// <c>[Low, High]</c> BY CONSTRUCTION — every intermediate is truncated toward negative infinity for
    /// <see cref="Low"/> and toward positive infinity for <see cref="High"/>, so the pair is a proof obligation the code
    /// discharges rather than an error estimate. A transcendental has no single correctly-rounded raw to compare
    /// against, so the enclosure is what an oracle can honestly offer: the law states the subject lies within the
    /// DOCUMENTED envelope of it.</summary>
    /// <param name="Low">The greatest scaled integer proved to be at or below the true value.</param>
    /// <param name="High">The least scaled integer proved to be at or above the true value.</param>
    internal readonly record struct Enclosure(BigInteger Low, BigInteger High);

    /// <summary>The guard bits every transcendental enclosure carries below the Q48.16 grid: the returned pair is
    /// scaled by <c>2^(16 + GuardBitCount)</c>, so a sub-ULP envelope is expressible as an integer comparison.</summary>
    public const int GuardBitCount = 32;

    // The working fraction bits the logarithm and exponential oracles carry.
    private const int SeriesBitCount = 160;
    // The working fraction bits the arctangent series carries. Smaller than SeriesBitCount because the arctangent runs
    // per sampled operand pair rather than once at type initialization, and eighty bits of headroom below the guard
    // scale already make the enclosure's own width invisible against a sub-ULP envelope.
    private const int ArcTangentBitCount = 128;
    // The working fraction bits the circular reduction carries. Far larger than the others because a full-range Q48.16
    // angle is reduced against 2π with up to forty-five bits of cancellation before any series runs.
    private const int AngleBitCount = 384;
    // The working fraction bits the sine/cosine Taylor series carries AFTER that reduction, where no cancellation is
    // left to absorb.
    private const int TrigBitCount = 96;
    // The emitted fraction bits of the repeated-squaring logarithm.
    private const int LogFractionBitCount = 56;
    // The depth of the 2^(2^-i) square-root ladder, which bounds the exponent scale EncloseExp2 accepts.
    private const int LadderDepth = 48;
    // The Taylor terms the reduced sine and cosine series carry; the remainder after thirty terms is below 2^-176 while
    // the working scale is 2^-96.
    private const int TrigTermCount = 30;

    // The ladder factors 2^(2^-i) at the working scale, built ONCE by repeated integer square roots of two — floored
    // for the lower ladder and ceilinged for the upper one, so a product over a fraction's set bits is an enclosure by
    // construction. Operand-independent, so building them per call would be pure waste.
    private static readonly BigInteger[] LowLadder = BuildLadder(ceiling: false);
    private static readonly BigInteger[] HighLadder = BuildLadder(ceiling: true);
    // The circle constant at each working scale, derived once by Machin's formula from this module's own arctangent
    // series. Nothing in the trigonometric oracles rests on a transcribed digit string.
    private static readonly Enclosure ArcTangentPi = MachinPi(bitCount: ArcTangentBitCount);
    private static readonly Enclosure AnglePi = MachinPi(bitCount: AngleBitCount);

    /// <summary>An enclosure of <c>π·2^bitCount</c>, derived by Machin's formula <c>π = 16·atan(1/5) − 4·atan(1/239)</c>
    /// evaluated by this module's own alternating arctangent series.</summary>
    /// <param name="bitCount">The scale the enclosure is returned at.</param>
    /// <returns>The enclosure.</returns>
    /// <remarks>The published forty-digit expansion is asserted against this once, as a structural leg of
    /// <c>scalar.sincos-vs-series</c>, purely to catch a transposed formula — it is a cross-check on the derivation,
    /// never the source of the value.</remarks>
    public static Enclosure Pi(int bitCount) =>
        ((bitCount == ArcTangentBitCount)
            ? ArcTangentPi
            : ((bitCount == AngleBitCount) ? AnglePi : MachinPi(bitCount: bitCount)));
    /// <summary>Rounds the exact rational <c>numerator·2^shift / denominator</c> to the nearest raw, ties to even, then
    /// wraps to the signed 64-bit carrier — the reference for a fixed-point division.</summary>
    /// <param name="numerator">The dividend.</param>
    /// <param name="denominator">The divisor, which must be non-zero.</param>
    /// <param name="shift">The fixed-point scale the quotient is taken at.</param>
    /// <returns>The rounded, wrapped raw.</returns>
    /// <remarks>Shares nothing with the subject: that one splits the magnitude into a 128-by-64 hardware quotient with
    /// an <c>r</c> versus <c>d − r</c> comparison and re-applies the combined sign to a 64-bit-truncated magnitude;
    /// this takes one exact <see cref="BigInteger.DivRem(BigInteger,BigInteger,out BigInteger)"/>, compares <c>2r</c>
    /// against <c>d</c> — the formulation the carrier cannot use, because <c>2r</c> would overflow it — and wraps the
    /// exact signed value once at the end.</remarks>
    public static long RoundDyadicRatio(BigInteger numerator, BigInteger denominator, int shift) =>
        WrapToRaw(value: RoundRationalTiesToEven(denominator: denominator, numerator: (numerator << shift)));
    /// <summary>The exact rounded fixed-point product as an UNWRAPPED integer — what the checked multiplication must
    /// return, and what its range verdict is decided by.</summary>
    /// <param name="a">The multiplicand's raw.</param>
    /// <param name="b">The multiplier's raw.</param>
    /// <returns>The rounded product, unwrapped.</returns>
    public static BigInteger ExactRoundedProduct(long a, long b) =>
        RoundRationalTiesToEven(numerator: (((BigInteger)a) * b), denominator: (BigInteger.One << 16));
    /// <summary>The exact rounded fixed-point quotient as an UNWRAPPED integer — the checked division's counterpart to
    /// <see cref="ExactRoundedProduct"/>.</summary>
    /// <param name="a">The dividend's raw.</param>
    /// <param name="b">The divisor's raw, which must be non-zero.</param>
    /// <returns>The rounded quotient, unwrapped.</returns>
    public static BigInteger ExactRoundedRatio(long a, long b) =>
        RoundRationalTiesToEven(numerator: (((BigInteger)a) << 16), denominator: new BigInteger(value: b));
    /// <summary>The exact integer square root <c>⌊√value⌋</c>, by a bit-length seed and Newton descent in
    /// <see cref="BigInteger"/>, settled by the exact predicate <c>r² ≤ value &lt; (r+1)²</c>.</summary>
    /// <param name="value">The radicand; a non-positive value roots to zero.</param>
    /// <returns>The exact floor of the square root.</returns>
    /// <remarks>Deliberately a different route from the subject, which seeds from a hardware or <see cref="double"/>
    /// square root and settles by trial multiplication in a fixed-width carrier. Puck.Maths' own
    /// <c>BigIntegerFunctions.SquareRoot</c> is NOT used: an oracle that called it would be checking the tree against
    /// itself.</remarks>
    public static BigInteger IntegerSquareRoot(BigInteger value) {
        if (value.Sign <= 0) {
            return BigInteger.Zero;
        }

        var root = (BigInteger.One << ((int)((value.GetBitLength() + 1L) / 2L)));

        while (true) {
            var next = ((root + (value / root)) >> 1);

            if (next >= root) { break; }

            root = next;
        }

        while ((root * root) > value) { root -= BigInteger.One; }

        while (((root + BigInteger.One) * (root + BigInteger.One)) <= value) { root += BigInteger.One; }

        return root;
    }
    /// <summary>The nearest integer to the square root of a non-negative exact value, by a BRACKETED INTEGER SEARCH
    /// whose predicate is one exact squaring — no square root is ever taken.</summary>
    /// <param name="value">The radicand, which must be non-negative.</param>
    /// <returns>The nearest integer root.</returns>
    /// <remarks>The answer is the largest <c>t</c> with <c>(2t − 1)² ≤ 4·value</c> (with zero admitted outright), because
    /// <c>t</c> is nearest exactly when <c>(t − ½)² ≤ value &lt; (t + ½)²</c>. No halfway case exists: <c>4·value</c> is even
    /// and <c>(2t + 1)²</c> odd, so the two can never be equal — which is the same fact the subject's ±1 settle relies on,
    /// reached here from the inequality rather than from an integer square root plus a remainder compare. Deliberately
    /// NOT <see cref="IntegerSquareRoot"/> with a repair on top: that one seeds from a bit length and descends by
    /// Newton's method, and a nearest root built on its floor would inherit the descent.</remarks>
    public static BigInteger NearestIntegerRoot(BigInteger value) {
        var quadrupled = (value << 2);

        bool AtMost(BigInteger candidate) {
            if (candidate.IsZero) { return true; }

            var odd = ((candidate << 1) - BigInteger.One);

            return ((odd * odd) <= quadrupled);
        }

        var low = BigInteger.Zero;
        var high = BigInteger.One;

        while (AtMost(candidate: high)) {
            low = high;
            high <<= 1;
        }

        while ((high - low) > BigInteger.One) {
            var middle = ((low + high) >> 1);

            if (AtMost(candidate: middle)) { low = middle; } else { high = middle; }
        }

        return low;
    }
    /// <summary>The reference complex quotient over the fixed-point carrier — <c>left·conj(right)/|right|²</c>, each
    /// component ONE ties-to-even rounding of the exact rational at Q16, wrapped to the carrier.</summary>
    /// <param name="ar">The dividend's real raw.</param>
    /// <param name="ai">The dividend's imaginary raw.</param>
    /// <param name="br">The divisor's real raw.</param>
    /// <param name="bi">The divisor's imaginary raw; the divisor must not be the additive identity.</param>
    /// <returns>The quotient's components as raws.</returns>
    /// <remarks>PATH-INDEPENDENT by construction: the subject reaches this value two ways — a narrow lane routed through
    /// the carrier's 128-by-64 divide-and-repair and a full-width lane through a bit-by-bit restoring division — and this
    /// forms neither, so agreement is also the proof of the source's "exact-equivalent fast path" claim rather than an
    /// assumption of it.</remarks>
    public static (long Real, long Imaginary) ComplexQuotient(long ar, long ai, long br, long bi) {
        var denominator = ((((BigInteger)br) * br) + (((BigInteger)bi) * bi));

        return (
            RoundDyadicRatio(denominator: denominator, numerator: ((((BigInteger)ar) * br) + (((BigInteger)ai) * bi)), shift: 16),
            RoundDyadicRatio(denominator: denominator, numerator: ((((BigInteger)ai) * br) - (((BigInteger)ar) * bi)), shift: 16)
        );
    }
    /// <summary>The reference split-complex quotient — <c>left·conj(right)/(c² − d²)</c>, each component ONE
    /// ties-to-even rounding at Q16.</summary>
    /// <param name="au">The dividend's scalar raw.</param>
    /// <param name="av">The dividend's split raw.</param>
    /// <param name="bu">The divisor's scalar raw.</param>
    /// <param name="bv">The divisor's split raw; the divisor must lie off the light cone.</param>
    /// <returns>The quotient's components as raws.</returns>
    /// <remarks>The denominator is INDEFINITE and may be negative; the sign of the quotient is the sign of the rational,
    /// which is the statement the subject's signed <c>DivideProductSum</c> overload makes and the definite complex case
    /// never reaches.</remarks>
    public static (long U, long V) SplitQuotient(long au, long av, long bu, long bv) {
        var denominator = ((((BigInteger)bu) * bu) - (((BigInteger)bv) * bv));

        return (
            RoundDyadicRatio(denominator: denominator, numerator: ((((BigInteger)au) * bu) - (((BigInteger)av) * bv)), shift: 16),
            RoundDyadicRatio(denominator: denominator, numerator: ((((BigInteger)av) * bu) - (((BigInteger)au) * bv)), shift: 16)
        );
    }
    /// <summary>The first lane at which a returned unit direction is farther than <paramref name="tolerance"/> raws from
    /// the EXACT Q16 unit direction of <paramref name="components"/>, or <c>−1</c> when every lane is within it.</summary>
    /// <param name="components">The exact input components. They are taken at arbitrary width rather than as raws
    /// because the geometric-product constructions judged here form exact Q32 sums that reach <c>2¹²⁷</c>, and only the
    /// DIRECTION of the tuple matters.</param>
    /// <param name="unit">The returned unit components, raw.</param>
    /// <param name="tolerance">The permitted deviation, in raws.</param>
    /// <returns>The offending lane index, or <c>−1</c>.</returns>
    /// <remarks>The ideal lane is <c>cᵢ·2¹⁶/√S</c> with <c>S = Σ cⱼ²</c>. Rather than form that irrational value, the two
    /// bounds are decided as SURD COMPARISONS <c>a·√S ≤ b</c>, each settled by reading the signs of <c>a</c> and <c>b</c>
    /// first and then squaring once — the same technique <see cref="PartialQuotients"/>' floor uses. Nothing here
    /// preconditions by a power of two, rounds a denominator, or divides: it shares no step with
    /// <c>FixedVectorMath.Normalize</c>, whose answer it judges. An all-zero input is defined to have an all-zero
    /// direction, which is NOT what the algebra types return at zero — each documents its own identity there, and each
    /// case states that pole structurally rather than routing it here.</remarks>
    public static int FirstNonUnitLane(ReadOnlySpan<BigInteger> components, ReadOnlySpan<long> unit, long tolerance) {
        var squaredSum = BigInteger.Zero;

        foreach (var component in components) {
            squaredSum += (component * component);
        }

        for (var lane = 0; (lane < components.Length); ++lane) {
            if (squaredSum.IsZero) {
                if (0L != unit[lane]) { return lane; }

                continue;
            }

            var scaled = (components[lane] << 16);
            var low = (new BigInteger(value: unit[lane]) - tolerance);
            var high = (new BigInteger(value: unit[lane]) + tolerance);

            // low·√S ≤ cᵢ·2¹⁶ ≤ high·√S, the second written as (−high)·√S ≤ −cᵢ·2¹⁶ so one comparison shape serves both.
            if (!SurdAtMost(bound: scaled, coefficient: low, radicand: squaredSum)) { return lane; }
            if (!SurdAtMost(bound: -scaled, coefficient: -high, radicand: squaredSum)) { return lane; }
        }

        return -1;
    }
    /// <summary>The reference product of the Cayley–Dickson tower at a given number of doublings, as a twisted group
    /// algebra over <c>(ℤ/2)^floors</c>: the target key is the exclusive-or of the operand keys and the charge is
    /// <see cref="CayleyDicksonCharge"/>. Each lane is ONE ties-to-even rounding of the whole exact charged sum.</summary>
    /// <param name="left">The multiplicand's lanes, raw, <c>2^floors</c> wide, in basis order <c>e₀ … e_{2^floors−1}</c>.</param>
    /// <param name="right">The multiplier's lanes, same width and order.</param>
    /// <param name="floors">The number of doublings; two for the quaternions.</param>
    /// <param name="shift">The rounding scale; two raw Q16 factors make the exact sum Q32, so this is sixteen.</param>
    /// <param name="result">The destination lanes.</param>
    /// <remarks>A hand-written Hamilton product forms no such basis: this walks the doubling recursion
    /// <c>(a, b)·(c, d) = (a·c − d̄·b, d·a + b·c̄)</c> down to basis vectors and reads the target key off an
    /// exclusive-or. The convention agrees with Hamilton — checked by hand at <c>e₁e₂ = +e₃</c>, <c>e₂e₁ = −e₃</c>,
    /// <c>e₁² = e₃² = −e₀</c> and <c>e₂e₃ = +e₁</c>.</remarks>
    public static void CayleyDicksonProduct(ReadOnlySpan<long> left, ReadOnlySpan<long> right, int floors, int shift, Span<long> result) =>
        TwistedGroupProduct(
            chargeSource: (first, second) => CayleyDicksonCharge(floors: floors, leftIndex: first, rightIndex: second),
            left: left,
            right: right,
            shift: shift,
            result: result
        );
    /// <summary>The reference dual product over a Cayley–Dickson carrier: the real block is the carrier product
    /// <c>a·c</c> and each dual lane is ONE ties-to-even rounding of the WHOLE exact sum <c>a·d + b·c</c> — the two
    /// carrier products fused ACROSS the dual seam rather than rounded separately and added.</summary>
    /// <param name="left">The multiplicand: the real block's lanes then the dual block's, each <c>2^floors</c> wide.</param>
    /// <param name="right">The multiplier, same layout.</param>
    /// <param name="floors">The number of doublings; two for dual quaternions.</param>
    /// <param name="shift">The rounding scale, sixteen.</param>
    /// <param name="result">The destination, same layout as the operands.</param>
    public static void DoublingDualProduct(ReadOnlySpan<long> left, ReadOnlySpan<long> right, int floors, int shift, Span<long> result) {
        var width = (1 << floors);

        for (var target = 0; (target < width); ++target) {
            var real = BigInteger.Zero;
            var dual = BigInteger.Zero;

            for (var first = 0; (first < width); ++first) {
                var second = first ^ target;
                var charge = CayleyDicksonCharge(floors: floors, leftIndex: first, rightIndex: second);
                var realTerm = (((BigInteger)left[first]) * right[second]);
                var dualTerm = ((((BigInteger)left[first]) * right[(width + second)]) + (((BigInteger)left[(width + first)]) * right[second]));

                real += ((charge > 0) ? realTerm : -realTerm);
                dual += ((charge > 0) ? dualTerm : -dualTerm);
            }

            result[target] = RoundDyadic(exact: real, shift: shift);
            result[(width + target)] = RoundDyadic(exact: dual, shift: shift);
        }
    }
    /// <summary>The reference dual product over a carrier that is NOT a house type: the generic path forms the carrier
    /// product three times and adds, so the dual part carries TWO roundings and one wrapping add — the honest
    /// alternative discipline the fused seams are claimed to beat.</summary>
    /// <param name="pRaw">The carrier relation's linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The carrier relation's constant coefficient, raw Q16.</param>
    /// <param name="left">The multiplicand: the real carrier's two components then the dual carrier's.</param>
    /// <param name="right">The multiplier, same layout.</param>
    /// <param name="result">The destination, same layout.</param>
    public static void DualOverQuadraticProduct(long pRaw, long qRaw, ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var real = QuadraticMultiply(pRaw: pRaw, qRaw: qRaw, u1: left[0], v1: left[1], u2: right[0], v2: right[1]);
        var crossed = QuadraticMultiply(pRaw: pRaw, qRaw: qRaw, u1: left[0], v1: left[1], u2: right[2], v2: right[3]);
        var seeded = QuadraticMultiply(pRaw: pRaw, qRaw: qRaw, u1: left[2], v1: left[3], u2: right[0], v2: right[1]);

        result[0] = real.U;
        result[1] = real.V;
        result[2] = WrapToRaw(value: (((BigInteger)crossed.U) + seeded.U));
        result[3] = WrapToRaw(value: (((BigInteger)crossed.V) + seeded.V));
    }
    /// <summary>The reference dot product of two lane vectors — ONE ties-to-even rounding of the exact product sum.</summary>
    /// <param name="left">The first vector's lanes, raw.</param>
    /// <param name="right">The second vector's lanes, raw.</param>
    /// <param name="shift">The rounding scale; two raw Q16 factors make the exact sum Q32, so this is sixteen.</param>
    /// <returns>The dot product as a raw.</returns>
    public static long LaneDotProduct(ReadOnlySpan<long> left, ReadOnlySpan<long> right, int shift) {
        var exact = BigInteger.Zero;

        for (var lane = 0; (lane < left.Length); ++lane) { exact += (((BigInteger)left[lane]) * right[lane]); }

        return RoundDyadic(exact: exact, shift: shift);
    }
    /// <summary>The reference rotation sandwich <c>v' = v + 2·u×(u×v + w·v)</c> over the fixed-point carrier: each of the
    /// two stages accumulates its exact product sum and rounds ONCE per component, and the final combination wraps.</summary>
    /// <param name="rotation">The rotor lanes <c>(x, y, z, w)</c>, raw.</param>
    /// <param name="vector">The vector lanes <c>(x, y, z)</c>, raw.</param>
    /// <param name="shift">The rounding scale, sixteen.</param>
    /// <param name="result">The three destination lanes.</param>
    /// <remarks>The TWO-STAGE SCHEDULE is the subject's documented contract, carried faithfully here — a rotation over a
    /// rounding carrier is chain-dependent, so the number and order of the roundings is part of the answer and no
    /// single-rounding oracle can stand beside it. What is re-derived independently is the ARITHMETIC of each stage: the
    /// exact cross and scaled sums in <see cref="BigInteger"/>, one <see cref="RoundDyadic"/> per component, sharing no
    /// code and no rounding kernel with the subject.</remarks>
    public static void QuaternionSandwich(ReadOnlySpan<long> rotation, ReadOnlySpan<long> vector, int shift, Span<long> result) {
        var (ux, uy, uz, w) = (((BigInteger)rotation[0]), ((BigInteger)rotation[1]), ((BigInteger)rotation[2]), ((BigInteger)rotation[3]));
        var (vx, vy, vz) = (((BigInteger)vector[0]), ((BigInteger)vector[1]), ((BigInteger)vector[2]));
        var tx = new BigInteger(value: RoundDyadic(exact: (((uy * vz) - (uz * vy)) + (w * vx)), shift: shift));
        var ty = new BigInteger(value: RoundDyadic(exact: (((uz * vx) - (ux * vz)) + (w * vy)), shift: shift));
        var tz = new BigInteger(value: RoundDyadic(exact: (((ux * vy) - (uy * vx)) + (w * vz)), shift: shift));
        var dx = new BigInteger(value: RoundDyadic(exact: ((uy * tz) - (uz * ty)), shift: shift));
        var dy = new BigInteger(value: RoundDyadic(exact: ((uz * tx) - (ux * tz)), shift: shift));
        var dz = new BigInteger(value: RoundDyadic(exact: ((ux * ty) - (uy * tx)), shift: shift));

        result[0] = WrapToRaw(value: (vx + (dx << 1)));
        result[1] = WrapToRaw(value: (vy + (dy << 1)));
        result[2] = WrapToRaw(value: (vz + (dz << 1)));
    }
    /// <summary>The reference canonicalization of one world axis: the exact integer split of a cell index and an offset
    /// into the canonical pair whose offset lies in <c>[−2^(cellRawLog2−1), 2^(cellRawLog2−1))</c>.</summary>
    /// <param name="cell">The initial cell index.</param>
    /// <param name="localRaw">The initial offset, in raw units.</param>
    /// <param name="cellRawLog2">The base-2 logarithm of one cell's raw span.</param>
    /// <returns>The canonical cell index and offset, both exact; the cell index is NOT reduced to any carrier, so the
    /// caller decides representability.</returns>
    /// <remarks>Derived from the definition rather than from a shift schedule: the carry is the exact rounded quotient
    /// <c>⌊(localRaw + 2^(cellRawLog2−1)) / 2^cellRawLog2⌋</c> — half-cells carry UP — taken with
    /// <see cref="FloorQuotient"/>, and the offset is the exact residue. Nothing here masks, shifts or wraps, so the
    /// reference never reproduces the subject's <c>carry &lt;&lt; 36</c> two's-complement congruence; it judges it.</remarks>
    public static (BigInteger Cell, BigInteger LocalRaw) CellSplit(BigInteger cell, BigInteger localRaw, int cellRawLog2) {
        var span = (BigInteger.One << cellRawLog2);
        var carry = FloorQuotient(denominator: span, numerator: (localRaw + (span >> 1)));

        return ((cell + carry), (localRaw - (carry * span)));
    }
    /// <summary>The exact displacement between two canonical world axes, in raw units:
    /// <c>(cell − originCell)·2^cellRawLog2 + (localRaw − originLocalRaw)</c>.</summary>
    /// <param name="cell">The target's cell index.</param>
    /// <param name="localRaw">The target's canonical offset, raw.</param>
    /// <param name="originCell">The origin's cell index.</param>
    /// <param name="originLocalRaw">The origin's canonical offset, raw.</param>
    /// <param name="cellRawLog2">The base-2 logarithm of one cell's raw span.</param>
    /// <returns>The exact displacement, arbitrary width, so the value is the mathematical one and the caller decides
    /// whether the carrier can hold it.</returns>
    /// <remarks>One expression, no branch. The subject reaches the same number through two paths selected by a
    /// conservative <c>|cellDelta| ≤ 2²⁶</c> gate and an overflow test, one of them relying on the canonical offsets
    /// differing by less than a cell; this knows nothing of either and therefore judges both.</remarks>
    public static BigInteger CellDelta(BigInteger cell, BigInteger localRaw, BigInteger originCell, BigInteger originLocalRaw, int cellRawLog2) =>
        (((cell - originCell) << cellRawLog2) + (localRaw - originLocalRaw));
    /// <summary>Replays a fixed schedule of rate steps against an exact rational ledger and returns each step's advanced
    /// quantity together with the remainder retained after it.</summary>
    /// <param name="rateRaws">Each step's per-second rate, raw.</param>
    /// <param name="elapsedTicks">Each step's tick count, parallel to <paramref name="rateRaws"/>.</param>
    /// <param name="ticksPerSecond">The positive time base.</param>
    /// <param name="initialRemainder">The remainder the ledger starts from.</param>
    /// <returns>Per step: the advanced raw quantity and the remainder after it.</returns>
    /// <remarks>The division is TRUNCATION TOWARD ZERO, derived from the definition rather than from a carrier's
    /// divide-and-remainder primitive: the quotient is the magnitude quotient with the numerator's sign re-applied, and
    /// the remainder is the numerator less quotient·denominator. Ties do not arise — nothing is rounded here, which is
    /// exactly the point: the discarded part is RETAINED rather than resolved, and the invariant
    /// <c>ticksPerSecond·Σ advanced + finalRemainder == Σ rate·ticks + initialRemainder</c> holds at every prefix. No
    /// value here is reduced to any carrier and no cast is checked, so the subject's overflow refusal is judged from
    /// outside its own boundary.</remarks>
    public static IReadOnlyList<(BigInteger Advanced, BigInteger Remainder)> RateIntegrationLedger(
        ReadOnlySpan<long> rateRaws,
        ReadOnlySpan<ulong> elapsedTicks,
        long ticksPerSecond,
        long initialRemainder
    ) {
        var denominator = new BigInteger(value: ticksPerSecond);
        var remainder = new BigInteger(value: initialRemainder);
        var steps = new List<(BigInteger Advanced, BigInteger Remainder)>(capacity: rateRaws.Length);

        for (var step = 0; (step < rateRaws.Length); ++step) {
            var numerator = ((new BigInteger(value: rateRaws[step]) * new BigInteger(value: elapsedTicks[step])) + remainder);
            var magnitude = BigInteger.Abs(value: numerator);
            var quotient = BigInteger.Divide(dividend: magnitude, divisor: denominator);

            if (numerator.Sign < 0) { quotient = -quotient; }

            remainder = (numerator - (quotient * denominator));

            steps.Add(item: (quotient, remainder));
        }

        return steps;
    }
    /// <summary>The reference translation of a rigid transform: <c>2·dual·conj(real)</c>, the Hamilton product's lanes
    /// each ONE ties-to-even rounding of its exact four-term sum, then doubled with a wrapping add and no second
    /// rounding — plus the scalar lane, which a unit transform leaves at zero and which the caller inspects.</summary>
    /// <param name="value">The eight lanes in doubling order: the real quaternion's <c>e₀…e₃</c> then the dual's.</param>
    /// <param name="shift">The rounding scale, sixteen.</param>
    /// <param name="result">Four destination lanes: the doubled vector lanes <c>e₁, e₂, e₃</c> and the UNDOUBLED scalar
    /// residual <c>e₀</c> of <c>dual·conj(real)</c>, which is the orthogonality witness.</param>
    /// <remarks>The product side is the doubling recursion, which shares nothing with a hand-written Hamilton product's
    /// sixteen signed products. The conjugation is an explicit arbitrary-width negation wrapped once, so the
    /// two's-complement fixed point at the signed minimum is reproduced by STATEMENT rather than inherited from an
    /// <c>unchecked</c> negation. The doubling is one exact left shift, where the subject writes a wrapping add.</remarks>
    public static void RigidTranslation(ReadOnlySpan<long> value, int shift, Span<long> result) {
        Span<long> conjugate = stackalloc long[4];
        Span<long> product = stackalloc long[4];

        conjugate[0] = value[0];
        conjugate[1] = WrapToRaw(value: -new BigInteger(value: value[1]));
        conjugate[2] = WrapToRaw(value: -new BigInteger(value: value[2]));
        conjugate[3] = WrapToRaw(value: -new BigInteger(value: value[3]));

        CayleyDicksonProduct(left: value[4..8], right: conjugate, floors: 2, shift: shift, result: product);

        result[0] = WrapToRaw(value: (new BigInteger(value: product[1]) << 1));
        result[1] = WrapToRaw(value: (new BigInteger(value: product[2]) << 1));
        result[2] = WrapToRaw(value: (new BigInteger(value: product[3]) << 1));
        result[3] = product[0];
    }
    /// <summary>The reference point action of a rigid transform: the two-stage rotation sandwich by the real quaternion,
    /// then the componentwise wrapping addition of <see cref="RigidTranslation"/>'s doubled lanes.</summary>
    /// <param name="value">The eight lanes in doubling order.</param>
    /// <param name="point">The three point lanes, raw.</param>
    /// <param name="shift">The rounding scale, sixteen.</param>
    /// <param name="result">The three destination lanes.</param>
    /// <remarks>The SCHEDULE — sandwich, then add a freshly formed translation — is the subject's documented
    /// composition and is carried faithfully here; what is re-derived independently is every step's arithmetic.</remarks>
    public static void RigidPointAction(ReadOnlySpan<long> value, ReadOnlySpan<long> point, int shift, Span<long> result) {
        Span<long> rotation = [value[1], value[2], value[3], value[0]];
        Span<long> rotated = stackalloc long[3];
        Span<long> translation = stackalloc long[4];

        QuaternionSandwich(result: rotated, rotation: rotation, shift: shift, vector: point);
        RigidTranslation(result: translation, shift: shift, value: value);

        for (var lane = 0; (lane < 3); ++lane) {
            result[lane] = WrapToRaw(value: (new BigInteger(value: rotated[lane]) + translation[lane]));
        }
    }

    // coefficient·√radicand ≤ bound, exactly: the signs are read off first so the single squaring never flips the
    // inequality. The radicand is non-negative by construction.
    private static bool SurdAtMost(BigInteger coefficient, BigInteger radicand, BigInteger bound) {
        if (coefficient.Sign <= 0) { return ((bound.Sign >= 0) || (((coefficient * coefficient) * radicand) >= (bound * bound))); }

        return ((bound.Sign > 0) && (((coefficient * coefficient) * radicand) <= (bound * bound)));
    }

    /// <summary>The exact decimal expansion of the SIGNED dyadic rational <c>value / 2^shift</c>, rendered
    /// sign-magnitude the way the signed fixed-point family renders — a leading minus, then the magnitude's
    /// expansion.</summary>
    /// <param name="value">The exact signed numerator.</param>
    /// <param name="shift">The scale: the denominator is <c>2^shift</c>.</param>
    /// <returns>The exact invariant-culture text.</returns>
    /// <remarks>The magnitude side is <see cref="ExactDyadicDecimal"/>, whose digits come from
    /// <c>n/2ˢ == (n·5ˢ)/10ˢ</c>, deliberately unlike any digit-at-a-time renderer.</remarks>
    public static string ExactDyadicDecimalSigned(BigInteger value, int shift) =>
        ((value.Sign < 0)
            ? ("-" + ExactDyadicDecimal(numerator: -value, shift: shift))
            : ExactDyadicDecimal(numerator: value, shift: shift));
    /// <summary>Quantizes the exact decimal literal <c>numerator / 10^decimalExponent</c> onto the <c>2^shift</c> grid:
    /// ONE ties-to-even rounding of the exact rational <c>numerator·2^shift / 10^decimalExponent</c>, plus the verdict
    /// of the ASYMMETRIC signed range <c>[−2⁶³, 2⁶³ − 1]</c>.</summary>
    /// <param name="numerator">The literal's digits as one exact integer, sign included.</param>
    /// <param name="decimalExponent">The number of decimal fraction digits those digits carry.</param>
    /// <param name="shift">The fixed-point scale.</param>
    /// <returns>Whether the quantized value fits the carrier, and that value when it does.</returns>
    /// <remarks>A deliberately different route from the subject's parser, which never forms the whole rational: that
    /// one keeps a seventeen-digit fraction prefix, divides by the reduced denominator <c>2·5¹⁷</c>, and repairs the tie
    /// with a sticky flag for the digits it discarded. The two agree because <c>2·5¹⁷</c> divides <c>10¹⁷</c>, which is
    /// a theorem about the subject rather than a shared step.</remarks>
    public static (bool InRange, long Raw) DecimalToRaw(BigInteger numerator, int decimalExponent, int shift) {
        var exact = RoundRationalTiesToEven(
            numerator: (numerator << shift),
            denominator: BigInteger.Pow(value: new BigInteger(value: 10), exponent: decimalExponent)
        );
        var inRange = ((exact >= long.MinValue) && (exact <= long.MaxValue));

        return (inRange, (inRange ? ((long)exact) : 0L));
    }
    /// <summary>An enclosure of <c>log₂(raw / 2¹⁶) · 2^(16 + guardBitCount)</c> for a positive raw.</summary>
    /// <param name="raw">The subject's raw, which must be positive.</param>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The enclosure.</returns>
    /// <remarks>Route: the integer part is the bit length (exact). The fraction is produced by REPEATED SQUARING — the
    /// mantissa in <c>[1, 2)</c> is held exactly as <c>raw &lt;&lt; (SeriesBitCount − ⌊log₂ raw⌋)</c>, then squared bit
    /// by bit, emitting a one and halving whenever the square reaches two. The two chains square with opposite
    /// truncation, so the pair is an enclosure; the gap at most quadruples per step from an EXACT start, which at a
    /// hundred and sixty working bits leaves it below the emitted grid throughout. There is no table and no polynomial
    /// anywhere in this route — the subject's 128-entry reciprocal table and quartic residual are reproduced by
    /// nothing here.</remarks>
    public static Enclosure EncloseLog2(long raw, int guardBitCount) {
        var value = new BigInteger(value: raw);
        var bitLength = ((int)value.GetBitLength());
        var integerPart = new BigInteger(value: ((bitLength - 1) - 16));
        var two = (BigInteger.One << (SeriesBitCount + 1));
        var lowState = (value << (SeriesBitCount - (bitLength - 1)));
        var highState = lowState;
        var lowFraction = BigInteger.Zero;
        var highFraction = BigInteger.Zero;

        for (var bit = 1; (bit <= LogFractionBitCount); ++bit) {
            lowState = ((lowState * lowState) >> SeriesBitCount);
            highState = CeilingShiftRight(shift: SeriesBitCount, value: (highState * highState));

            if (lowState >= two) {
                lowFraction += (BigInteger.One << (LogFractionBitCount - bit));
                lowState >>= 1;
            }

            if (highState >= two) {
                highFraction += (BigInteger.One << (LogFractionBitCount - bit));
                highState = CeilingShiftRight(shift: 1, value: highState);
            }
        }

        var scaled = (integerPart << LogFractionBitCount);

        return Rescale(
            value: new Enclosure(Low: (scaled + lowFraction), High: ((scaled + highFraction) + BigInteger.One)),
            fromBitCount: LogFractionBitCount,
            toBitCount: (16 + guardBitCount)
        );
    }
    /// <summary>An enclosure of <c>2^(scaledExponent / 2^exponentBitCount) · 2^(16 + guardBitCount)</c>.</summary>
    /// <param name="scaledExponent">The exponent, scaled by <c>2^exponentBitCount</c>.</param>
    /// <param name="exponentBitCount">The exponent's scale; it may not exceed the ladder's depth.</param>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The enclosure.</returns>
    /// <remarks>Route: split the exponent into <c>k + u</c> with <c>u ∈ [0, 1)</c>, then form <c>2^u</c> as the product
    /// of the ladder factors <c>2^(2⁻ⁱ)</c> over u's set bits. The ladder is built once by REPEATED INTEGER SQUARE
    /// ROOTS of two, floored for the lower chain and ceilinged for the upper one, so the whole construction is
    /// exact-integer and shares nothing with the subject's 128-entry table and quartic residual.</remarks>
    public static Enclosure EncloseExp2(BigInteger scaledExponent, int exponentBitCount, int guardBitCount) {
        var wholePart = (scaledExponent >> exponentBitCount);
        var fraction = (scaledExponent - (wholePart << exponentBitCount));
        var low = (BigInteger.One << SeriesBitCount);
        var high = low;

        for (var level = 1; (level <= exponentBitCount); ++level) {
            if (!(((fraction >> (exponentBitCount - level)) & BigInteger.One)).IsZero) {
                low = ((low * LowLadder[level]) >> SeriesBitCount);
                high = CeilingShiftRight(value: (high * HighLadder[level]), shift: SeriesBitCount);
            }
        }

        return Rescale(
            value: new Enclosure(High: high, Low: low),
            fromBitCount: SeriesBitCount,
            toBitCount: ((((int)wholePart) + 16) + guardBitCount)
        );
    }
    /// <summary>An enclosure of <c>atan2(y, x)·2^(16 + guardBitCount)</c> over raw Q48.16 operands. The ratio is
    /// scale-invariant, so the raw operands go straight in.</summary>
    /// <param name="yRaw">The ordinate's raw.</param>
    /// <param name="xRaw">The abscissa's raw.</param>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The enclosure.</returns>
    /// <remarks>The quadrant split is the textbook definition (by the signs of <paramref name="xRaw"/> and
    /// <paramref name="yRaw"/>, with the axes named explicitly), NOT the subject's min/max octant fold; the VALUE comes
    /// from the alternating arctangent series rather than the subject's interval table. What the two sides do share is
    /// the mathematical case analysis itself — there is only one atan2 — so the leg names the series as the independent
    /// part.</remarks>
    public static Enclosure EncloseAtan2(long yRaw, long xRaw, int guardBitCount) {
        if ((0L == yRaw) && (0L == xRaw)) {
            return new(Low: BigInteger.Zero, High: BigInteger.Zero);
        }

        var ordinate = BigInteger.Abs(value: new BigInteger(value: yRaw));
        var abscissa = BigInteger.Abs(value: new BigInteger(value: xRaw));
        var circle = Pi(bitCount: ArcTangentBitCount);
        Enclosure principal;

        if (xRaw > 0L) {
            principal = EncloseArcTangent(bitCount: ArcTangentBitCount, denominator: abscissa, numerator: ordinate);
        } else if (0L == xRaw) {
            principal = new(Low: (circle.Low >> 1), High: CeilingShiftRight(value: circle.High, shift: 1));
        } else {
            var inner = EncloseArcTangent(bitCount: ArcTangentBitCount, denominator: abscissa, numerator: ordinate);

            principal = new(Low: (circle.Low - inner.High), High: (circle.High - inner.Low));
        }

        return Rescale(
            value: ((yRaw < 0L) ? new Enclosure(Low: -principal.High, High: -principal.Low) : principal),
            fromBitCount: ArcTangentBitCount,
            toBitCount: (16 + guardBitCount)
        );
    }
    /// <summary>An enclosure of <c>(sin θ, cos θ)·2^(16 + guardBitCount)</c> for <c>θ = raw / 2¹⁶</c> radians.</summary>
    /// <param name="raw">The angle's raw.</param>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The two enclosures.</returns>
    /// <remarks>Route: reduce IN RADIANS against <see cref="Pi"/> — <c>n = round(θ / 2π)</c>, residual
    /// <c>r = θ − n·2π ∈ [−π, π]</c> carried at three hundred and eighty-four working bits so the up-to-forty-five-bit
    /// cancellation of a full-range angle is absorbed — then the alternating Taylor series for sine and cosine, whose
    /// remainder after thirty terms is bounded by <c>|r|^61/61!</c>. The subject reduces in TURNS against a single Q64
    /// reciprocal constant and evaluates a seven-term Q60 polynomial after a half-turn fold; neither the reduction
    /// domain, the constant, nor the polynomial is shared.</remarks>
    public static (Enclosure Sin, Enclosure Cos) EncloseSinCos(long raw, int guardBitCount) {
        var circle = Pi(bitCount: AngleBitCount);
        var turn = new Enclosure(Low: (circle.Low << 1), High: (circle.High << 1));
        var theta = (new BigInteger(value: raw) << (AngleBitCount - 16));
        var turns = RoundRationalTiesToEven(numerator: theta, denominator: turn.Low);
        var residual = Residual(theta: theta, turn: turn, turns: turns);

        // The reduction constant is known to within a handful of units at this scale, so the rounded turn count is the
        // nearest one except at an unreachable knife edge; the normalisation makes the residual's bound a fact of the
        // code rather than an argument about that edge.
        while (residual.Low > circle.High) {
            turns += BigInteger.One;
            residual = Residual(theta: theta, turn: turn, turns: turns);
        }

        while (residual.High < -circle.High) {
            turns -= BigInteger.One;
            residual = Residual(theta: theta, turn: turn, turns: turns);
        }

        var narrowing = (AngleBitCount - TrigBitCount);
        var angle = (residual.Low >> narrowing);
        var slack = (((residual.High - residual.Low) >> narrowing) + new BigInteger(value: 1026));
        var scale = (BigInteger.One << TrigBitCount);
        var square = ((angle * angle) >> TrigBitCount);
        var sineTerm = angle;
        var sine = angle;
        var cosineTerm = scale;
        var cosine = scale;

        for (var term = 1; (term <= TrigTermCount); ++term) {
            sineTerm = -((sineTerm * square) / (scale * new BigInteger(value: ((2 * term) * ((2 * term) + 1)))));
            cosineTerm = -((cosineTerm * square) / (scale * new BigInteger(value: (((2 * term) - 1) * (2 * term)))));
            sine += sineTerm;
            cosine += cosineTerm;
        }

        return (
            Rescale(value: new Enclosure(High: (sine + slack), Low: (sine - slack)), fromBitCount: TrigBitCount, toBitCount: (16 + guardBitCount)),
            Rescale(value: new Enclosure(High: (cosine + slack), Low: (cosine - slack)), fromBitCount: TrigBitCount, toBitCount: (16 + guardBitCount))
        );
    }
    /// <summary>Rescales an enclosure between two fixed-point scales with DIRECTED rounding, so the widened or narrowed
    /// pair still brackets the same real value.</summary>
    /// <param name="value">The enclosure.</param>
    /// <param name="fromBitCount">The scale it is stated at.</param>
    /// <param name="toBitCount">The scale it is wanted at.</param>
    /// <returns>The rescaled enclosure.</returns>
    public static Enclosure Rescale(Enclosure value, int fromBitCount, int toBitCount) {
        if (fromBitCount == toBitCount) {
            return value;
        }

        if (fromBitCount > toBitCount) {
            var narrowing = (fromBitCount - toBitCount);

            return new(Low: (value.Low >> narrowing), High: CeilingShiftRight(value: value.High, shift: narrowing));
        }

        var widening = (toBitCount - fromBitCount);

        return new(Low: (value.Low << widening), High: (value.High << widening));
    }

    // The quotient rounded toward POSITIVE infinity, which is what an upper bound must use wherever the lower bound
    // shifts right.
    private static BigInteger CeilingShiftRight(BigInteger value, int shift) =>
        (-((-value) >> shift));
    // The residual θ − n·2π as an enclosure, with the turn product taken in the direction each bound needs.
    private static Enclosure Residual(BigInteger theta, Enclosure turn, BigInteger turns) {
        var productLow = ((turns.Sign >= 0) ? (turns * turn.Low) : (turns * turn.High));
        var productHigh = ((turns.Sign >= 0) ? (turns * turn.High) : (turns * turn.Low));

        return new(High: (theta - productLow), Low: (theta - productHigh));
    }
    // π = 16·atan(1/5) − 4·atan(1/239), evaluated by the series alone: the reduction branch of EncloseArcTangent needs
    // π, and this is where that circularity is cut — both Machin arguments are already below a half.
    private static Enclosure MachinPi(int bitCount) {
        var first = ArcTangentSeries(numerator: BigInteger.One, denominator: new BigInteger(value: 5), bitCount: bitCount);
        var second = ArcTangentSeries(numerator: BigInteger.One, denominator: new BigInteger(value: 239), bitCount: bitCount);

        return new(Low: ((16 * first.Low) - (4 * second.High)), High: ((16 * first.High) - (4 * second.Low)));
    }
    // atan(numerator/denominator)·2^bitCount, enclosed, for a non-negative numerator and a positive denominator. The
    // two exact reductions atan(z) = π/2 − atan(1/z) and atan(z) = π/4 + atan((z−1)/(z+1)) bring every argument onto
    // [0, ½], where the alternating series converges at one bit per two terms or better. NO table, NO per-interval
    // cubic, NO fixed-width truncation: a different derivation from the subject in every part.
    private static Enclosure EncloseArcTangent(BigInteger numerator, BigInteger denominator, int bitCount) {
        if (numerator > denominator) {
            var reciprocal = EncloseArcTangent(bitCount: bitCount, denominator: numerator, numerator: denominator);
            var circle = Pi(bitCount: bitCount);

            return new(Low: ((circle.Low >> 1) - reciprocal.High), High: (CeilingShiftRight(value: circle.High, shift: 1) - reciprocal.Low));
        }

        if ((numerator << 1) > denominator) {
            var folded = EncloseArcTangent(bitCount: bitCount, denominator: (denominator + numerator), numerator: (denominator - numerator));
            var circle = Pi(bitCount: bitCount);

            return new(Low: ((circle.Low >> 2) - folded.High), High: (CeilingShiftRight(value: circle.High, shift: 2) - folded.Low));
        }

        return ArcTangentSeries(bitCount: bitCount, denominator: denominator, numerator: numerator);
    }
    // The alternating series atan(z) = Σ (−1)ᵏ z^(2k+1)/(2k+1) at scale 2^bitCount, for 0 ≤ z ≤ ½. The powers are
    // carried at the working scale rather than as exact rationals, so nothing grows past a few hundred bits; every
    // truncation is bounded by a handful of units and the slack below absorbs the lot along with the tail.
    private static Enclosure ArcTangentSeries(BigInteger numerator, BigInteger denominator, int bitCount) {
        if (numerator.IsZero) {
            return new(Low: BigInteger.Zero, High: BigInteger.Zero);
        }

        var count = ArcTangentTermCount(bitCount: bitCount, denominator: denominator, numerator: numerator);
        var argument = ((numerator << bitCount) / denominator);
        var square = ((argument * argument) >> bitCount);
        var power = argument;
        var low = BigInteger.Zero;
        var high = BigInteger.Zero;

        for (var term = 0; (term < count); ++term) {
            var quotient = (power / new BigInteger(value: ((2 * term) + 1)));

            if (0 == (term & 1)) {
                low += quotient;
                high += (quotient + BigInteger.One);
            } else {
                low -= (quotient + BigInteger.One);
                high -= quotient;
            }

            power = ((power * square) >> bitCount);
        }

        var slack = new BigInteger(value: ((8 * count) + 16));

        return new(High: (high + slack), Low: (low - slack));
    }
    // The terms the series needs for a tail below 2^-(bitCount+8): each term costs at least one guaranteed bit per
    // factor of the argument's reciprocal, bounded below by the operands' bit-length difference.
    private static int ArcTangentTermCount(BigInteger numerator, BigInteger denominator, int bitCount) {
        var ratioBits = (((int)(denominator.GetBitLength() - numerator.GetBitLength())) - 1);

        if (ratioBits < 1) {
            ratioBits = 1;
        }

        return (((bitCount + 16) / (2 * ratioBits)) + 2);
    }
    // The quotient rounded toward POSITIVE infinity for a non-negative numerator and a positive denominator —
    // the ceiling counterpart BigInteger's own truncating `/` does not give, needed by the Gaussian-tail enclosure
    // below wherever a bound must round away from the true value rather than toward it.
    private static BigInteger CeilingDivideNonNegative(BigInteger numerator, BigInteger positiveDenominator) {
        var quotient = BigInteger.DivRem(dividend: numerator, divisor: positiveDenominator, remainder: out var remainder);

        return ((remainder > BigInteger.Zero) ? (quotient + BigInteger.One) : quotient);
    }

    // The working precision and term count the Gaussian-tail enclosure's e^4.5 series below carries. 4.5^41/41! is
    // astronomically below any bit this module ever asks for (Stirling puts it under 2^-100), so forty terms at two
    // hundred fifty-six working bits leaves the geometric remainder bound, not term-by-term rounding, as the
    // dominant — and still utterly negligible — source of width.
    private const int GaussianTailWorkingBitCount = 256;
    private const int GaussianTailTermCount = 40;

    /// <summary>An enclosure of <c>P(|Z|&gt;3)·2^(16+guardBitCount)</c> for a standard normal <c>Z</c> — the
    /// two-sided Gaussian tail beyond three sigma.</summary>
    /// <param name="guardBitCount">The guard bits below Q48.16 the result carries.</param>
    /// <returns>The enclosure.</returns>
    /// <remarks>
    /// Route: Gordon's classical inequality, for <c>x &gt; 0</c>: <c>x·φ(x)/(x²+1) &lt; Q(x) &lt; φ(x)/x</c>, where
    /// <c>Q(x) = P(Z&gt;x)</c> and <c>φ(x) = e^(−x²/2)/√(2π)</c> is the standard normal density. At <c>x = 3</c> this
    /// is <c>(3/10)·φ(3) &lt; Q(3) &lt; φ(3)/3</c> — simple enough to state and check by hand, unlike the tighter
    /// continued fraction <c>Q(x)/φ(x) = 1/(x+) 1/(x+) 2/(x+) 3/(x+) …</c> that pins <c>Q</c> arbitrarily closely; the
    /// classical bound's ~10% relative width at <c>x = 3</c> still lands inside the existing empirical tolerance
    /// band, which is what a reference reachable at reasonable effort needs to do, not more.
    /// <para>
    /// <c>φ(3) = e^(−4.5)/√(2π)</c> is built from two independently-derived pieces, neither touching a <c>Puck.Maths</c>
    /// kernel: <c>e^4.5</c> from its OWN Taylor series (every term positive, so the partial sum is a lower bound and
    /// a geometric tail bound — valid because the term ratio <c>4.5/(n+1)</c> is safely below one past this series'
    /// forty terms — gives the upper one), reciprocated for <c>e^(−4.5)</c>; and <c>√(2π)</c> from <see cref="Pi"/>
    /// (itself Machin's formula, not a transcribed digit string) via <see cref="IntegerSquareRoot"/> with directed
    /// rounding at each bound.
    /// </para>
    /// </remarks>
    public static Enclosure EncloseGaussianTailBeyondThreeSigma(int guardBitCount) {
        var scale = (BigInteger.One << GaussianTailWorkingBitCount);
        // 4.5 = 9/2 is an exact dyadic fraction, so xScaled = 4.5·scale is an exact integer for any working bit
        // count ≥ 1.
        var xScaled = (new BigInteger(value: 9) << (GaussianTailWorkingBitCount - 1));
        var termLow = scale;
        var termHigh = scale;
        var sumLow = scale;
        var sumHigh = scale;

        for (var term = 1; (term <= GaussianTailTermCount); ++term) {
            var stepDenominator = (new BigInteger(value: term) * scale);

            termLow = ((termLow * xScaled) / stepDenominator);
            termHigh = CeilingDivideNonNegative(numerator: (termHigh * xScaled), positiveDenominator: stepDenominator);
            sumLow += termLow;
            sumHigh += termHigh;
        }

        // The tail Σ_{k>N} term_k is bounded by term_N · r/(1−r) with r = x/(N+1) — the largest ratio any later term
        // ever carries, since x/k strictly decreases as k grows past N+1. At x=9/2, N=40: r/(1−r) = x/((N+1)−x) =
        // (9/2)/(73/2) = 9/73, an exact rational applied directly to the already-scaled upper term.
        var remainderBound = CeilingDivideNonNegative(numerator: (termHigh * 9), positiveDenominator: 73);
        var eEnclosure = new Enclosure(High: (sumHigh + remainderBound), Low: sumLow);
        var scaleSquared = (BigInteger.One << (2 * GaussianTailWorkingBitCount));
        // e^(−4.5) = 1/e^4.5: reciprocating an enclosure swaps and inverts its bounds, each rounded away from the
        // true value.
        var negativeExponentialLow = (scaleSquared / eEnclosure.High);
        var negativeExponentialHigh = CeilingDivideNonNegative(numerator: scaleSquared, positiveDenominator: eEnclosure.Low);
        var piEnclosure = Pi(bitCount: GaussianTailWorkingBitCount);
        var twoPiEnclosure = new Enclosure(Low: (piEnclosure.Low * 2), High: (piEnclosure.High * 2));
        // √(2π) at the SAME working scale: S = √(2π)·scale satisfies S² = 2π·scale², so the radicand is the 2π
        // enclosure shifted up by one more working-bit-count factor. IntegerSquareRoot floors, which is already a
        // safe LOWER bound for the low radicand; +1 makes the high side a safe upper bound.
        var sqrtTwoPiLow = IntegerSquareRoot(value: (twoPiEnclosure.Low << GaussianTailWorkingBitCount));
        var sqrtTwoPiHigh = (IntegerSquareRoot(value: (twoPiEnclosure.High << GaussianTailWorkingBitCount)) + BigInteger.One);
        // φ(3) = e^(−4.5)/√(2π), both operands already at scale 2^GaussianTailWorkingBitCount, so the scale factor
        // introduced by the division is corrected by one more multiply by `scale`.
        var densityLow = ((negativeExponentialLow * scale) / sqrtTwoPiHigh);
        var densityHigh = CeilingDivideNonNegative(numerator: (negativeExponentialHigh * scale), positiveDenominator: sqrtTwoPiLow);
        // Gordon's bounds, doubled for the two-sided tail: Q(3) ∈ ((3/10)·φ(3), φ(3)/3).
        var tailLow = ((2 * (3 * densityLow)) / 10);
        var tailHigh = CeilingDivideNonNegative(numerator: (2 * densityHigh), positiveDenominator: 3);

        return Rescale(
            value: new Enclosure(High: tailHigh, Low: tailLow),
            fromBitCount: GaussianTailWorkingBitCount,
            toBitCount: (16 + guardBitCount)
        );
    }
}
