using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- FixedComplex ----

    /// <summary>Proves <see cref="FixedComplex"/>'s additive group is EXACT at every swept pair: the positional
    /// constructor round-trips both components, the sum and difference are the wrapped exact componentwise integers, the
    /// additive identity is <see langword="default"/> and neutral on both sides, and negation is subtraction from
    /// it.</summary>
    /// <param name="left">The multiplicand's components, raw.</param>
    /// <param name="right">The multiplier's components, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ComplexAdditiveGroupExact(long[] left, long[] right) {
        var a = new FixedComplex(
            Real: Raw(value: left[0]),
            Imaginary: Raw(value: left[1])
        );
        var b = new FixedComplex(
            Real: Raw(value: right[0]),
            Imaginary: Raw(value: right[1])
        );

        if (a.Real.Value != left[0]) { return $"the constructor moved the real component {left[0]}"; }
        if (a.Imaginary.Value != left[1]) { return $"the constructor moved the imaginary component {left[1]}"; }

        var sum = (a + b);
        var difference = (a - b);

        if (sum.Real.Value != Oracles.WrapToRaw(value: (((BigInteger)left[0]) + right[0]))) { return $"the real sum of {left[0]} and {right[0]} is {sum.Real.Value}"; }
        if (sum.Imaginary.Value != Oracles.WrapToRaw(value: (((BigInteger)left[1]) + right[1]))) { return $"the imaginary sum of {left[1]} and {right[1]} is {sum.Imaginary.Value}"; }
        if (difference.Real.Value != Oracles.WrapToRaw(value: (((BigInteger)left[0]) - right[0]))) { return $"the real difference of {left[0]} and {right[0]} is {difference.Real.Value}"; }
        if (difference.Imaginary.Value != Oracles.WrapToRaw(value: (((BigInteger)left[1]) - right[1]))) { return $"the imaginary difference of {left[1]} and {right[1]} is {difference.Imaginary.Value}"; }
        if (default(FixedComplex) != FixedComplex.AdditiveIdentity) { return "the default value is not the additive identity"; }
        if ((a + FixedComplex.AdditiveIdentity) != a) { return "the additive identity is not a right identity"; }
        if ((FixedComplex.AdditiveIdentity + a) != a) { return "the additive identity is not a left identity"; }
        if ((a - FixedComplex.AdditiveIdentity) != a) { return "the additive identity is not a right identity for subtraction"; }
        if ((a - a) != FixedComplex.AdditiveIdentity) { return "an element less itself is not the additive identity"; }
        if ((-a) != (FixedComplex.AdditiveIdentity - a)) { return "negation disagrees with subtraction from the additive identity"; }

        return null;
    }
    /// <summary>Proves the presentation seam onto <see cref="Complex"/> at every swept element: each lane is one
    /// round-to-nearest-ties-to-even of its raw followed by an exact <c>2⁻¹⁶</c> scale, compared on exact bit patterns
    /// against an oracle that assembles the IEEE-754 encoding from the format; and the two lanes are carried in the
    /// declared order, so a transposed real and imaginary part fails on PLACEMENT before any value is compared.</summary>
    /// <param name="left">The first element's components, raw.</param>
    /// <param name="right">The second element's components, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ComplexPresentationSeam(long[] left, long[] right) {
        static ulong ProjectedBits(long raw) =>
            Oracles.NearestBinary64Bits(
                value: new BigInteger(value: raw),
                shift: FixedQ4816.FractionBitCount
            );

        static string? Check(long realRaw, long imaginaryRaw) {
            var presented = new FixedComplex(
                Real: Raw(value: realRaw),
                Imaginary: Raw(value: imaginaryRaw)
            ).ToComplex();

            if (BitConverter.DoubleToUInt64Bits(value: presented.Real) != ProjectedBits(raw: realRaw)) { return $"the real lane of ({realRaw}, {imaginaryRaw}) presented as {BitConverter.DoubleToUInt64Bits(value: presented.Real):X16}"; }
            if (BitConverter.DoubleToUInt64Bits(value: presented.Imaginary) != ProjectedBits(raw: imaginaryRaw)) { return $"the imaginary lane of ({realRaw}, {imaginaryRaw}) presented as {BitConverter.DoubleToUInt64Bits(value: presented.Imaginary):X16}"; }

            return null;
        }

        if (Check(
            realRaw: left[0],
            imaginaryRaw: left[1]
        ) is { } first) { return first; }
        if (Check(
            realRaw: right[0],
            imaginaryRaw: right[1]
        ) is { } second) { return second; }

        var sentinelBits = ProjectedBits(raw: ComplexPresentationSentinel);

        foreach (var raw in ComplexPresentationLadder) {
            var bits = ProjectedBits(raw: raw);
            var atReal = new FixedComplex(
                Real: Raw(value: raw),
                Imaginary: Raw(value: ComplexPresentationSentinel)
            ).ToComplex();
            var atImaginary = new FixedComplex(
                Real: Raw(value: ComplexPresentationSentinel),
                Imaginary: Raw(value: raw)
            ).ToComplex();

            if (BitConverter.DoubleToUInt64Bits(value: atReal.Real) != bits) { return $"the raw {raw} did not read back at the real lane"; }
            if (BitConverter.DoubleToUInt64Bits(value: atImaginary.Imaginary) != bits) { return $"the raw {raw} did not read back at the imaginary lane"; }
            if (BitConverter.DoubleToUInt64Bits(value: atReal.Imaginary) != sentinelBits) { return $"the sentinel moved to the imaginary lane beside the raw {raw}"; }
            if (BitConverter.DoubleToUInt64Bits(value: atImaginary.Real) != sentinelBits) { return $"the sentinel moved to the real lane beside the raw {raw}"; }
        }

        return null;
    }

    // The placement ladder and its sentinel. Every ladder raw differs from the sentinel, so a transposed pair of lanes
    // fails on every row rather than passing on the diagonal: both carrier extremes, both units, the single-raw quantum
    // and zero, against a sentinel that is none of them.
    private static readonly long[] ComplexPresentationLadder = [
        long.MinValue, -65536L, -1L, 0L, 1L, 65536L, long.MaxValue,
    ];

    private const long ComplexPresentationSentinel = 424242L;

    /// <summary>Maps a sampled complex divisor onto one the operation defines: the additive identity divides nothing.
    /// Substituted identically in subject and oracle; the excluded point is the documented throw site and belongs to
    /// <c>complex.div-refusal-and-unit</c>, not to a value law.</summary>
    private static (long U, long V) ComplexDivisor(long u, long v) =>
        (((0L == u) && (0L == v))
            ? (OneRaw, 0L)
            : (u, v)
        );

    /// <summary>The subject <see cref="FixedComplex"/> divide.</summary>
    public static (long U, long V) ComplexDivide(long u1, long v1, long u2, long v2) {
        var (u, v) = ComplexDivisor(
            u: u2,
            v: v2
        );
        var quotient = (new FixedComplex(
            Real: Raw(value: u1),
            Imaginary: Raw(value: v1)
        ) / new FixedComplex(
            Real: Raw(value: u),
            Imaginary: Raw(value: v)
        ));

        return (quotient.Real.Value, quotient.Imaginary.Value);
    }
    /// <summary>The oracle <see cref="FixedComplex"/> divide — one ties-to-even rounding of each exact rational.</summary>
    public static (long U, long V) ComplexDivideOracle(long u1, long v1, long u2, long v2) {
        var (u, v) = ComplexDivisor(
            u: u2,
            v: v2
        );
        var quotient = Oracles.ComplexQuotient(
            ai: v1,
            ar: u1,
            bi: v,
            br: u
        );

        return (quotient.Real, quotient.Imaginary);
    }
    /// <summary>Proves the complex quotient's refusal and its unit: a zero divisor is refused from BOTH code paths, the
    /// multiplicative identity is an exact right divisor over a raw ladder, and a hand-derived quotient ladder — whose
    /// last three rows straddle the narrow gate — lands bit-for-bit.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ComplexDivRefusalAndUnit() {
        if (!Throws<DivideByZeroException>(action: () => _ = (new FixedComplex(
            Real: Raw(value: OneRaw),
            Imaginary: Raw(value: OneRaw)
        ) / FixedComplex.AdditiveIdentity))) {
            return "the narrow path did not refuse a zero divisor";
        }

        if (!Throws<DivideByZeroException>(action: () => _ = (new FixedComplex(
            Real: FixedQ4816.MaxValue,
            Imaginary: FixedQ4816.MaxValue
        ) / FixedComplex.AdditiveIdentity))) {
            return "the full-width path did not refuse a zero divisor";
        }

        if (FixedComplex.MultiplicativeIdentity != new FixedComplex(
            Real: FixedQ4816.One,
            Imaginary: FixedQ4816.Zero
        )) { return "the multiplicative identity is not (One, Zero)"; }

        foreach (var raw in ComplexUnitLadder) {
            var value = new FixedComplex(
                Real: Raw(value: raw),
                Imaginary: Raw(value: unchecked(-raw))
            );

            if ((value / FixedComplex.MultiplicativeIdentity) != value) { return $"the multiplicative identity is not an exact right divisor at raw {raw}"; }
        }

        foreach (var (ar, ai, br, bi, expectedReal, expectedImaginary) in ComplexQuotientLadder) {
            var quotient = (new FixedComplex(
                Real: Raw(value: ar),
                Imaginary: Raw(value: ai)
            ) / new FixedComplex(
                Real: Raw(value: br),
                Imaginary: Raw(value: bi)
            ));

            if (
                (quotient.Real.Value != expectedReal) ||
                (quotient.Imaginary.Value != expectedImaginary)
            ) {
                return $"({ar},{ai})/({br},{bi}) is ({quotient.Real.Value},{quotient.Imaginary.Value}), expected ({expectedReal},{expectedImaginary})";
            }
        }

        return null;
    }
    /// <summary>Proves the two magnitude readers against exact arbitrary-width arithmetic: the magnitude is the nearest
    /// integer root of the exact two-square sum, the squared magnitude is one ties-to-even Q16 rounding of it, the
    /// refusal predicate is exactly "the answer leaves the carrier", and each saturating reader agrees with its
    /// <c>Try</c> sibling.</summary>
    /// <param name="left">The first sampled element's components, raw.</param>
    /// <param name="right">The second sampled element's components, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ComplexMagnitudeExact(long[] left, long[] right) {
        static string? Check(long u, long v) {
            var value = new FixedComplex(
                Real: Raw(value: u),
                Imaginary: Raw(value: v)
            );
            var exact = ((((BigInteger)u) * u) + (((BigInteger)v) * v));
            var expectedRoot = Oracles.NearestIntegerRoot(value: exact);
            var expectedSquared = Oracles.RoundToEvenUnits(
                magnitude: exact,
                shift: FixedQ4816.FractionBitCount
            );
            var rootFits = (expectedRoot <= long.MaxValue);
            var squaredFits = (expectedSquared <= long.MaxValue);

            if (value.TryMagnitude(magnitude: out var magnitude) != rootFits) { return $"TryMagnitude reported the wrong verdict at ({u},{v})"; }
            if (
                rootFits &&
                (magnitude.Value != expectedRoot)
            ) { return $"the magnitude of ({u},{v}) is {magnitude.Value}, expected {expectedRoot}"; }
            if (value.TryMagnitudeSquared(squaredMagnitude: out var squared) != squaredFits) { return $"TryMagnitudeSquared reported the wrong verdict at ({u},{v})"; }
            if (
                squaredFits &&
                (squared.Value != expectedSquared)
            ) { return $"the squared magnitude of ({u},{v}) is {squared.Value}, expected {expectedSquared}"; }
            if (value.Magnitude != (rootFits
                ? magnitude
                : FixedQ4816.MaxValue)) { return $"the saturating magnitude of ({u},{v}) disagrees with its Try sibling"; }
            if (value.MagnitudeSquared != (squaredFits
                ? squared
                : FixedQ4816.MaxValue)) { return $"the saturating squared magnitude of ({u},{v}) disagrees with its Try sibling"; }

            return null;
        }

        return (Check(
            u: left[0],
            v: left[1]
        ) ?? Check(
            u: right[0],
            v: right[1]
        ));
    }
    /// <summary>Proves the complex unit direction lands within one raw of the EXACT Q16 direction at every swept
    /// element, that the additive identity answers with the multiplicative identity, that no non-zero element
    /// normalizes to zero, and that normalization commutes with negation wherever the negation names a different
    /// element.</summary>
    /// <param name="left">The first sampled element's components, raw.</param>
    /// <param name="right">The second sampled element's components, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ComplexNormalizeUnitDirection(long[] left, long[] right) {
        static string? Check(long u, long v) {
            var value = new FixedComplex(
                Real: Raw(value: u),
                Imaginary: Raw(value: v)
            );
            var unit = value.Normalize();

            if (
                (0L == u) &&
                (0L == v)
            ) {
                return ((unit == FixedComplex.MultiplicativeIdentity)
                    ? null
                    : "the additive identity did not normalize to the multiplicative identity"
                );
            }

            var lane = Oracles.FirstNonUnitLane(
                components: [new BigInteger(value: u), new BigInteger(value: v)],
                unit: [unit.Real.Value, unit.Imaginary.Value],
                tolerance: 1L
            );

            if (lane >= 0) { return $"lane {lane} of the unit direction of ({u},{v}) is farther than one raw from the exact direction"; }
            if (
                (0L == unit.Real.Value) &&
                (0L == unit.Imaginary.Value)
            ) { return $"the non-zero element ({u},{v}) normalized to the additive identity"; }

            // At the two's-complement minimum the negation is its own fixed point, so the commutation is stated where
            // the negation actually names a different element.
            if (
                (long.MinValue != u) &&
                (long.MinValue != v) &&
                ((-value).Normalize() != (-unit))
            ) {
                return $"normalization does not commute with negation at ({u},{v})";
            }

            return null;
        }

        return (Check(
            u: left[0],
            v: left[1]
        ) ?? Check(
            u: right[0],
            v: right[1]
        ));
    }
    /// <summary>Proves <see cref="FixedComplex.FromTo"/> is the unit direction of the exact geometric product, that both
    /// degenerate poles are exact, and that a common power-of-two rescaling of either operand leaves the answer
    /// bit-identical.</summary>
    /// <param name="left">The start direction's components, raw.</param>
    /// <param name="right">The end direction's components, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ComplexFromToDirection(long[] left, long[] right) {
        var from = new FixedVector2(
            X: Raw(value: left[0]),
            Y: Raw(value: left[1])
        );
        var to = new FixedVector2(
            X: Raw(value: right[0]),
            Y: Raw(value: right[1])
        );
        var rotation = FixedComplex.FromTo(
            from: from,
            to: to
        );
        var dot = ((((BigInteger)left[0]) * right[0]) + (((BigInteger)left[1]) * right[1]));
        var wedge = ((((BigInteger)left[0]) * right[1]) - (((BigInteger)left[1]) * right[0]));

        if (
            dot.IsZero &&
            wedge.IsZero
        ) {
            if (rotation != FixedComplex.MultiplicativeIdentity) { return "a vanishing geometric product did not return the multiplicative identity"; }
        } else {
            var lane = Oracles.FirstNonUnitLane(
                components: [dot, wedge],
                unit: [rotation.Real.Value, rotation.Imaginary.Value],
                tolerance: 2L
            );

            if (lane >= 0) { return $"lane {lane} of the rotation is farther than two raws from the exact direction of the geometric product ({dot}, {wedge})"; }
        }

        if (FixedComplex.FromTo(
            from: default,
            to: to
        ) != FixedComplex.MultiplicativeIdentity) { return "a zero start direction did not return the multiplicative identity"; }
        if (FixedComplex.FromTo(
            from: from,
            to: default
        ) != FixedComplex.MultiplicativeIdentity) { return "a zero end direction did not return the multiplicative identity"; }

        foreach (var (fx, fy, tx, ty) in ComplexAntiparallelLadder) {
            var half = FixedComplex.FromTo(
                from: new(
                    X: Raw(value: fx),
                    Y: Raw(value: fy)
                ),
                to: new(
                    X: Raw(value: tx),
                    Y: Raw(value: ty)
                )
            );

            if (half != new FixedComplex(
                Real: FixedQ4816.NegativeOne,
                Imaginary: FixedQ4816.Zero
            )) {
                return $"the antiparallel pair ({fx},{fy}) → ({tx},{ty}) returned ({half.Real.Value},{half.Imaginary.Value}) rather than the exact half turn";
            }
        }

        if (
            WithinScaleGuard(
            bound: (1L << 20),
            values: left
        ) &&
            WithinScaleGuard(
            bound: (1L << 20),
            values: right
        )
        ) {
            foreach (var (leftShift, rightShift) in ScaleFreedomShifts) {
                var scaled = FixedComplex.FromTo(
                    from: new(
                        X: Raw(value: (left[0] << leftShift)),
                        Y: Raw(value: (left[1] << leftShift))
                    ),
                    to: new(
                        X: Raw(value: (right[0] << rightShift)),
                        Y: Raw(value: (right[1] << rightShift))
                    )
                );

                if (scaled != rotation) { return $"scaling by 2^{leftShift} and 2^{rightShift} moved the rotation"; }
            }
        }

        return null;
    }
    /// <summary>Proves the planar transcendental seam on hand-derived constant ladders: the eighteen-row angle ladder,
    /// the sixteen-row argument ladder, the exact poles at both ends of the seam, the REALIZED closed raw range, and the
    /// two points where the rotation semantics are exact.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ComplexAngleSeam() {
        if (FixedComplex.FromAngle(angle: FixedQ4816.Zero) != FixedComplex.MultiplicativeIdentity) { return "FromAngle(Zero) is not the multiplicative identity"; }

        foreach (var (angleRaw, expectedReal, expectedImaginary) in ComplexAngleLadder) {
            var value = FixedComplex.FromAngle(angle: Raw(value: angleRaw));

            if (Math.Abs(value: (value.Real.Value - expectedReal)) > ComplexAngleTolerance) { return $"FromAngle({angleRaw}).Real is {value.Real.Value}, expected {expectedReal}"; }
            if (Math.Abs(value: (value.Imaginary.Value - expectedImaginary)) > ComplexAngleTolerance) { return $"FromAngle({angleRaw}).Imaginary is {value.Imaginary.Value}, expected {expectedImaginary}"; }
        }

        if (FixedComplex.AdditiveIdentity.Argument != FixedQ4816.Zero) { return "the additive identity's argument is not zero"; }
        if (FixedComplex.MultiplicativeIdentity.Argument != FixedQ4816.Zero) { return "the multiplicative identity's argument is not zero"; }

        foreach (var (realRaw, imaginaryRaw, expected) in ComplexArgumentLadder) {
            var argument = new FixedComplex(
                Real: Raw(value: realRaw),
                Imaginary: Raw(value: imaginaryRaw)
            ).Argument.Value;

            if (Math.Abs(value: (argument - expected)) > ComplexAngleTolerance) { return $"the argument of ({realRaw},{imaginaryRaw}) is {argument}, expected {expected}"; }
            if (
                (argument < -PiRaw) ||
                (argument > PiRaw)
            ) { return $"the argument of ({realRaw},{imaginaryRaw}) is {argument}, outside the realized range"; }
        }

        foreach (var (realRaw, imaginaryRaw) in ComplexRangeLadder) {
            var argument = new FixedComplex(
                Real: Raw(value: realRaw),
                Imaginary: Raw(value: imaginaryRaw)
            ).Argument.Value;

            if (
                (argument < -PiRaw) ||
                (argument > PiRaw)
            ) { return $"the argument of ({realRaw},{imaginaryRaw}) is {argument}, outside the realized range"; }
        }

        if (new FixedComplex(
            Real: Raw(value: -(1L << 20)),
            Imaginary: Raw(value: -1L)
        ).Argument.Value != -PiRaw) {
            return "the negative endpoint of the realized range is not attained at (−2²⁰, −1)";
        }

        foreach (var raw in ComplexUnitLadder) {
            var vector = new FixedVector2(
                X: Raw(value: raw),
                Y: Raw(value: unchecked(-raw))
            );

            if (FixedComplex.MultiplicativeIdentity.Rotate(vector: vector) != vector) { return $"the multiplicative identity did not rotate ({raw},{unchecked(-raw)}) to itself"; }
        }

        var quarterTurn = FixedComplex.FromAngle(angle: Raw(value: 102944L)).Rotate(vector: new FixedVector2(
            X: FixedQ4816.One,
            Y: FixedQ4816.Zero
        ));

        if (
            (quarterTurn.X != FixedQ4816.Zero) ||
            (quarterTurn.Y != FixedQ4816.One)
        ) { return $"the quarter turn sent (One, Zero) to ({quarterTurn.X.Value},{quarterTurn.Y.Value})"; }

        return null;
    }
    /// <summary>The subject <see cref="FixedComplex.Rotate"/>, rotor components first and vector components second.</summary>
    public static (long U, long V) ComplexRotate(long u1, long v1, long u2, long v2) {
        var image = new FixedComplex(
            Real: Raw(value: u1),
            Imaginary: Raw(value: v1)
        ).Rotate(vector: new FixedVector2(
            X: Raw(value: u2),
            Y: Raw(value: v2)
        ));

        return (image.X.Value, image.Y.Value);
    }

    // The right-identity ladder: both carrier extremes and their neighbourhoods, the narrow product gate either side,
    // the whole unit, and zero.
    private static readonly long[] ComplexUnitLadder = [
        long.MinValue, (long.MinValue + 1L), -(1L << 47), -((1L << 31) + 1L), -(1L << 31), -65536L, -1L,
        0L, 1L, 65536L, (1L << 31), ((1L << 31) + 1L), (1L << 47), (long.MaxValue - 1L), long.MaxValue,
    ];
    // Exactly antiparallel direction pairs: the negation, a Pythagorean pair scaled by two, and a diagonal scaled by
    // seven. Each returns the exact half turn because a single non-zero component always normalizes to exactly ±2¹⁶.
    private static readonly (long FromX, long FromY, long ToX, long ToY)[] ComplexAntiparallelLadder = [
        (65536L, 0L, -65536L, 0L),
        (0L, 65536L, 0L, -65536L),
        (3L, 4L, -6L, -8L),
        (1L, 1L, -7L, -7L),
        ((1L << 40), (1L << 39), -(1L << 41), -(1L << 40)),
    ];
    // Near-axis and extreme operand pairs whose realized argument must stay inside the closed raw range.
    private static readonly (long Real, long Imaginary)[] ComplexRangeLadder = [
        (long.MinValue, -1L), (long.MinValue, 1L), (long.MinValue, long.MinValue), (long.MinValue, long.MaxValue),
        (-(1L << 20), -1L), (-(1L << 40), -1L), (-1L, -1L), (long.MaxValue, long.MinValue), (0L, 0L),
    ];

    // FixedQ4816.SinCos and FixedQ4816.Atan2 each carry a documented 0.51 raw ULP envelope; two raws is that rounded up.
    private const long ComplexAngleTolerance = 2L;

    // T1 — the angle ladder. Hand-derived from the real cosine and sine of the value each ANGLE RAW denotes (so the
    // argument's own quantization is not counted as error): the four quadrant boundaries, the π/6, π/4 and π/3 family,
    // both signs, one and four turns of wrap-around, and ±1 radian.
    private static readonly (long AngleRaw, long ExpectedReal, long ExpectedImaginary)[] ComplexAngleLadder = [
        (0L, 65536L, 0L),
        (34314L, 56756L, 32768L),
        (51472L, 46341L, 46341L),
        (68628L, 32769L, 56755L),
        (102944L, 0L, 65536L),
        (137257L, -32767L, 56756L),
        (154416L, -46341L, 46341L),
        (171573L, -56756L, 32768L),
        (205887L, -65536L, 0L),
        (-34314L, 56756L, -32768L),
        (-102944L, 0L, -65536L),
        (-154416L, -46341L, -46341L),
        (-205887L, -65536L, 0L),
        (463247L, 46341L, 46341L),
        (-480404L, 32768L, -56756L),
        (1681414L, 56756L, 32768L),
        (65536L, 35409L, 55147L),
        (-65536L, 35409L, -55147L),
    ];
    // T2 — the argument ladder. Hand-derived from the real two-argument arctangent of the values the raws denote: all
    // four axes, all four diagonals, both π/6 rays, a Pythagorean point, and three near-axis points.
    private static readonly (long Real, long Imaginary, long Expected)[] ComplexArgumentLadder = [
        (65536L, 0L, 0L),
        (0L, 65536L, 102944L),
        (-65536L, 0L, 205887L),
        (0L, -65536L, -102944L),
        (65536L, 65536L, 51472L),
        (-65536L, 65536L, 154416L),
        (-65536L, -65536L, -154416L),
        (65536L, -65536L, -51472L),
        (113512L, 65536L, 34314L),
        (65536L, 113512L, 68629L),
        (-113512L, 65536L, 171573L),
        (3L, 4L, 60771L),
        (-4L, 3L, 163715L),
        (1000000L, 1L, 0L),
        (1L, 1000000L, 102944L),
        (-1L, -1000000L, -102944L),
    ];
    // The quotient ladder, hand-derived from (ac+bd, bc−ad)/(c²+d²) evaluated in exact rationals and rounded once. The
    // first four rows take the narrow long path; the last three straddle the 2³¹ gate and take the full-width one.
    private static readonly (long Ar, long Ai, long Br, long Bi, long ExpectedReal, long ExpectedImaginary)[] ComplexQuotientLadder = [
        (0L, 65536L, 0L, 65536L, 65536L, 0L),
        (65536L, 0L, 0L, 65536L, 0L, -65536L),
        (65536L, 65536L, 65536L, -65536L, 0L, 65536L),
        (196608L, 262144L, 131072L, -65536L, 26214L, 144179L),
        ((1L << 31), 0L, 65536L, 0L, (1L << 31), 0L),
        (((1L << 31) + 1L), ((1L << 31) - 1L), ((1L << 31) + 1L), 0L, 65536L, 65536L),
        (long.MaxValue, 1L, (1L << 31), (1L << 31), (1L << 47), -(1L << 47)),
    ];

    // ---- FixedSplit ----

    /// <summary>Proves <see cref="FixedSplit"/>'s additive group is EXACT at every swept pair, on the mirror of the
    /// complex statement, and that the multiplicative identity is a two-sided identity for the split product with no
    /// rounding anywhere.</summary>
    /// <param name="left">The multiplicand's components, raw.</param>
    /// <param name="right">The multiplier's components, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SplitAdditiveGroupExact(long[] left, long[] right) {
        var a = new FixedSplit(
            U: Raw(value: left[0]),
            V: Raw(value: left[1])
        );
        var b = new FixedSplit(
            U: Raw(value: right[0]),
            V: Raw(value: right[1])
        );

        if (a.U.Value != left[0]) { return $"the constructor moved the scalar component {left[0]}"; }
        if (a.V.Value != left[1]) { return $"the constructor moved the split component {left[1]}"; }

        var sum = (a + b);
        var difference = (a - b);

        if (sum.U.Value != Oracles.WrapToRaw(value: (((BigInteger)left[0]) + right[0]))) { return $"the scalar sum of {left[0]} and {right[0]} is {sum.U.Value}"; }
        if (sum.V.Value != Oracles.WrapToRaw(value: (((BigInteger)left[1]) + right[1]))) { return $"the split sum of {left[1]} and {right[1]} is {sum.V.Value}"; }
        if (difference.U.Value != Oracles.WrapToRaw(value: (((BigInteger)left[0]) - right[0]))) { return $"the scalar difference of {left[0]} and {right[0]} is {difference.U.Value}"; }
        if (difference.V.Value != Oracles.WrapToRaw(value: (((BigInteger)left[1]) - right[1]))) { return $"the split difference of {left[1]} and {right[1]} is {difference.V.Value}"; }
        if (default(FixedSplit) != FixedSplit.AdditiveIdentity) { return "the default value is not the additive identity"; }
        if ((a + FixedSplit.AdditiveIdentity) != a) { return "the additive identity is not a right identity"; }
        if ((FixedSplit.AdditiveIdentity + a) != a) { return "the additive identity is not a left identity"; }
        if ((a - a) != FixedSplit.AdditiveIdentity) { return "an element less itself is not the additive identity"; }
        if ((-a) != (FixedSplit.AdditiveIdentity - a)) { return "negation disagrees with subtraction from the additive identity"; }
        if (FixedSplit.MultiplicativeIdentity != new FixedSplit(
            U: FixedQ4816.One,
            V: FixedQ4816.Zero
        )) { return "the multiplicative identity is not (One, Zero)"; }
        if ((a * FixedSplit.MultiplicativeIdentity) != a) { return "the multiplicative identity is not an exact right identity"; }
        if ((FixedSplit.MultiplicativeIdentity * a) != a) { return "the multiplicative identity is not an exact left identity"; }

        return null;
    }

    /// <summary>Maps a sampled split divisor off the LIGHT CONE, which is where the algebra's zero divisors live: any
    /// element with |u| == |v| — the zero element included — is substituted by the multiplicative identity. Substituted
    /// identically in subject and oracle.</summary>
    private static (long U, long V) SplitDivisor(long u, long v) =>
        (((u == v) || (u == unchecked(-v)))
            ? (OneRaw, 0L)
            : (u, v)
        );

    /// <summary>The subject <see cref="FixedSplit"/> divide.</summary>
    public static (long U, long V) SplitDivide(long u1, long v1, long u2, long v2) {
        var (u, v) = SplitDivisor(
            u: u2,
            v: v2
        );
        var quotient = (new FixedSplit(
            U: Raw(value: u1),
            V: Raw(value: v1)
        ) / new FixedSplit(
            U: Raw(value: u),
            V: Raw(value: v)
        ));

        return (quotient.U.Value, quotient.V.Value);
    }
    /// <summary>The oracle <see cref="FixedSplit"/> divide — one ties-to-even rounding of each exact rational over the
    /// INDEFINITE quadratic form.</summary>
    public static (long U, long V) SplitDivideOracle(long u1, long v1, long u2, long v2) {
        var (u, v) = SplitDivisor(
            u: u2,
            v: v2
        );

        return Oracles.SplitQuotient(
            au: u1,
            av: v1,
            bu: u,
            bv: v
        );
    }
    /// <summary>Proves the split algebra's invertibility predicate is exactly <c>u² ≠ v²</c>, that it is exactly the
    /// predicate <see cref="FixedSplit.op_Division"/> refuses on, that the ring's own zero-divisor witness is zero, and
    /// that the multiplicative identity divides and transforms exactly.</summary>
    /// <param name="left">The first sampled element's components, raw.</param>
    /// <param name="right">The second sampled element's components, raw, also read as the swept vector.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SplitUnitAndDivision(long[] left, long[] right) {
        static string? Check(long u, long v) {
            var value = new FixedSplit(
                U: Raw(value: u),
                V: Raw(value: v)
            );
            var unit = ((((BigInteger)u) * u) != (((BigInteger)v) * v));

            if (value.IsUnit != unit) { return $"IsUnit reported {value.IsUnit} at ({u},{v}), where u² ≠ v² is {unit}"; }
            if ((value / FixedSplit.MultiplicativeIdentity) != value) { return $"dividing ({u},{v}) by the multiplicative identity moved it"; }

            var refused = Throws<DivideByZeroException>(action: () => _ = (FixedSplit.MultiplicativeIdentity / value));

            if (refused == unit) {
                return $"dividing by ({u},{v}) {(refused
                ? "was"
                : "was not")} refused, but IsUnit reports {unit}";
            }

            return null;
        }

        if (Check(
            u: left[0],
            v: left[1]
        ) is { } first) { return first; }
        if (Check(
            u: right[0],
            v: right[1]
        ) is { } second) { return second; }

        var one = FixedSplit.MultiplicativeIdentity;
        var j = new FixedSplit(
            U: FixedQ4816.Zero,
            V: FixedQ4816.One
        );

        if (((one + j) * (one - j)) != FixedSplit.AdditiveIdentity) { return "the zero-divisor witness (1 + j)(1 − j) is not the additive identity"; }
        if ((one + j) == FixedSplit.AdditiveIdentity) { return "the factor (1 + j) is itself zero"; }
        if ((one - j) == FixedSplit.AdditiveIdentity) { return "the factor (1 − j) is itself zero"; }

        var vector = new FixedVector2(
            X: Raw(value: right[0]),
            Y: Raw(value: right[1])
        );

        if (one.Transform(vector: vector) != vector) { return $"the multiplicative identity did not transform ({right[0]},{right[1]}) to itself"; }

        var doubled = new FixedSplit(
            U: Raw(value: (OneRaw << 1)),
            V: FixedQ4816.Zero
        ).Transform(vector: vector);

        if (doubled.X.Value != Oracles.WrapToRaw(value: (((BigInteger)right[0]) << 1))) { return $"the exact squeeze (2, 0) did not double {right[0]}"; }
        if (doubled.Y.Value != Oracles.WrapToRaw(value: (((BigInteger)right[1]) << 1))) { return $"the exact squeeze (2, 0) did not double {right[1]}"; }

        return null;
    }
    /// <summary>The subject <see cref="FixedSplit.Transform"/>, squeeze components first and vector components second.</summary>
    public static (long U, long V) SplitTransform(long u1, long v1, long u2, long v2) {
        var image = new FixedSplit(
            U: Raw(value: u1),
            V: Raw(value: v1)
        ).Transform(vector: new FixedVector2(
            X: Raw(value: u2),
            Y: Raw(value: v2)
        ));

        return (image.X.Value, image.Y.Value);
    }
    /// <summary>Proves the hyperbolic seam on a hand-derived twelve-row rapidity ladder — the exact zero pole, the
    /// representable <c>ln 2</c> anchor, the sign contract, and the invariant norm within its own derived tolerance —
    /// at the light-cone boundary the member's doc quotes, where raw 726822 is the first exact <c>U == V</c>
    /// collision and one raw below is still a unit — and on a six-row saturation ladder pinning the top of the
    /// carrier, where the pair must reach <see cref="FixedQ4816.MaxValue"/> with the sine carrying the rapidity's
    /// sign.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SplitRapidityLadderClaim() {
        if (FixedSplit.FromRapidity(rapidity: FixedQ4816.Zero) != FixedSplit.MultiplicativeIdentity) { return "FromRapidity(Zero) is not the multiplicative identity"; }

        foreach (var (rapidityRaw, expectedU, expectedV, tolerance) in SplitRapidityLadder) {
            var value = FixedSplit.FromRapidity(rapidity: Raw(value: rapidityRaw));

            if (Math.Abs(value: (value.U.Value - expectedU)) > tolerance) { return $"FromRapidity({rapidityRaw}).U is {value.U.Value}, expected {expectedU} within {tolerance}"; }
            if (Math.Abs(value: (value.V.Value - expectedV)) > tolerance) { return $"FromRapidity({rapidityRaw}).V is {value.V.Value}, expected {expectedV} within {tolerance}"; }
            if (value.U.Value <= 0L) { return $"FromRapidity({rapidityRaw}).U is {value.U.Value}, not positive"; }
            if (Math.Sign(value: value.V.Value) != Math.Sign(value: rapidityRaw)) { return $"FromRapidity({rapidityRaw}).V is {value.V.Value}, whose sign is not the rapidity's"; }

            // cosh² − sinh² is one identically over the reals, so quantizing cosh and sinh alone already moves the norm:
            // the tolerance is 2·|U|·(the row's own tolerance)/2¹⁶, rounded up, and is derived rather than fitted.
            var normTolerance = ((((2L * expectedU) * tolerance) + (OneRaw - 1L)) / OneRaw);

            if (Math.Abs(value: (value.Norm.Value - OneRaw)) > normTolerance) { return $"the norm of FromRapidity({rapidityRaw}) is {value.Norm.Value}, expected {OneRaw} within {normTolerance}"; }
        }

        // The light-cone boundary, pinned where the member's doc quotes it: raw 726822 is the first rapidity whose
        // backward exponential 2^(−s−1) rounds to Zero (s > 16, φ > 16·ln 2 ≈ 11.0904), so the pair collides
        // bit-for-bit onto the cone and stays there; one raw below, the pair is still a unit. The negative mirror
        // lands on the opposite diagonal, the collided pair has no inverse, and its conjugate annihilates it.
        var lastUnit = FixedSplit.FromRapidity(rapidity: Raw(value: 726821L));
        var firstCone = FixedSplit.FromRapidity(rapidity: Raw(value: 726822L));
        var mirrorCone = FixedSplit.FromRapidity(rapidity: Raw(value: -726822L));

        if (
            !lastUnit.IsUnit ||
            (lastUnit.U.Value == lastUnit.V.Value)
        ) { return "FromRapidity(726821) is not a unit off the light cone"; }
        if (
            (firstCone.U.Value != firstCone.V.Value) ||
            firstCone.IsUnit
        ) { return "FromRapidity(726822) did not collide onto the light cone"; }
        if (
            (mirrorCone.U.Value != unchecked(-mirrorCone.V.Value)) ||
            mirrorCone.IsUnit
        ) { return "FromRapidity(-726822) did not collide onto the opposite diagonal"; }
        if (!Throws<DivideByZeroException>(action: () => _ = (FixedSplit.MultiplicativeIdentity / firstCone))) { return "division by the collided pair was not refused"; }
        if ((firstCone * firstCone.Conjugate()) != FixedSplit.AdditiveIdentity) { return "the collided pair times its conjugate is not the zero element"; }

        // The saturation ladder deliberately skips the norm identity: past the point where the backward exponential
        // rounds to zero, cosh and sinh coincide at Q16 and cosh² − sinh² is representationally zero, not one.
        foreach (var (rapidityRaw, expectedU, expectedV, tolerance) in SplitRapiditySaturationLadder) {
            var value = FixedSplit.FromRapidity(rapidity: Raw(value: rapidityRaw));

            if (Math.Abs(value: (value.U.Value - expectedU)) > tolerance) { return $"FromRapidity({rapidityRaw}).U is {value.U.Value}, expected {expectedU} within {tolerance}"; }
            if (Math.Abs(value: (value.V.Value - expectedV)) > tolerance) { return $"FromRapidity({rapidityRaw}).V is {value.V.Value}, expected {expectedV} within {tolerance}"; }
            if (value.U.Value <= 0L) { return $"FromRapidity({rapidityRaw}).U is {value.U.Value}, not positive"; }
            if (Math.Sign(value: value.V.Value) != Math.Sign(value: rapidityRaw)) { return $"FromRapidity({rapidityRaw}).V is {value.V.Value}, whose sign is not the rapidity's"; }
        }

        return null;
    }

    // T3 — the rapidity ladder. Hand-derived from the real hyperbolic cosine and sine of the value each RAPIDITY RAW
    // denotes. The per-row tolerance is derived: an argument error of at most 0.462·|φ| + 0.5 raws (the Log2E
    // quantization plus the scaled product's rounding) is a relative error of ln2/2¹⁶ per raw in the exponential,
    // plus Exp2's own bound at these magnitudes — half a ULP from its closing narrowing and a relative term that is
    // negligible this far below 2²⁰, 0.51 ULP per term — rounded up to a power of two; the halving is folded into
    // the exponent (2^(s−1) + 2^(−s−1)), so no halving rounding exists and the sum is exact. The ln 2 row is the
    // anchor: cosh(ln 2) is exactly 5/4 and sinh(ln 2) exactly 3/4, both on the Q16 grid.
    private static readonly (long RapidityRaw, long ExpectedU, long ExpectedV, long Tolerance)[] SplitRapidityLadder = [
        (0L, 65536L, 0L, 0L),
        (45426L, 81920L, 49152L, 2L),
        (16384L, 67595L, 16555L, 4L),
        (32768L, 73900L, 34151L, 4L),
        (65536L, 101127L, 77018L, 8L),
        (-65536L, 101127L, -77018L, 8L),
        (98304L, 154168L, 139544L, 8L),
        (131072L, 246559L, 237690L, 16L),
        (-131072L, 246559L, -237690L, 16L),
        (196608L, 659794L, 656531L, 48L),
        (262144L, 1789672L, 1788472L, 160L),
        (-262144L, 1789672L, -1788472L, 160L),
    ];
    // T3b — the saturation ladder. The rapidity-33 row is hand-derived at 200-bit precision (cosh and sinh both
    // round to the same raw: the backward exponential is far below half an ULP at this magnitude) with the T3
    // tolerance derivation evaluated at φ = 33 and rounded up to 2⁵¹. The remaining rows are EXACT: past a scaled
    // exponent of 48 the pre-halved terms are pinned to MaxValue and Zero with no rounding left anywhere — including
    // at 6393185575658021189, the first raw where an un-widened wrapping scale once flipped the sine's sign, and at
    // both carrier extremes.
    private static readonly (long RapidityRaw, long ExpectedU, long ExpectedV, long Tolerance)[] SplitRapiditySaturationLadder = [
        ((33L << 16), 7033440822424897606L, 7033440822424897606L, (1L << 51)),
        ((34L << 16), long.MaxValue, long.MaxValue, 0L),
        (-(34L << 16), long.MaxValue, -long.MaxValue, 0L),
        (6393185575658021189L, long.MaxValue, long.MaxValue, 0L),
        (long.MaxValue, long.MaxValue, long.MaxValue, 0L),
        (long.MinValue, long.MaxValue, -long.MaxValue, 0L),
    ];

    // ---- FixedDual ----

    /// <summary>Proves <see cref="FixedDual{TValue}"/>'s additive group over the house scalar is EXACT at every swept
    /// pair, on the mirror of the complex statement, and that the multiplicative identity is a two-sided identity for
    /// the fused dual product with no rounding anywhere.</summary>
    /// <param name="left">The multiplicand's components, raw.</param>
    /// <param name="right">The multiplier's components, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? DualAdditiveGroupExact(long[] left, long[] right) {
        var a = new FixedDual<FixedQ4816>(
            Real: Raw(value: left[0]),
            Dual: Raw(value: left[1])
        );
        var b = new FixedDual<FixedQ4816>(
            Real: Raw(value: right[0]),
            Dual: Raw(value: right[1])
        );

        if (a.Real.Value != left[0]) { return $"the constructor moved the real part {left[0]}"; }
        if (a.Dual.Value != left[1]) { return $"the constructor moved the dual part {left[1]}"; }

        var sum = (a + b);
        var difference = (a - b);

        if (sum.Real.Value != Oracles.WrapToRaw(value: (((BigInteger)left[0]) + right[0]))) { return $"the real sum of {left[0]} and {right[0]} is {sum.Real.Value}"; }
        if (sum.Dual.Value != Oracles.WrapToRaw(value: (((BigInteger)left[1]) + right[1]))) { return $"the dual sum of {left[1]} and {right[1]} is {sum.Dual.Value}"; }
        if (difference.Real.Value != Oracles.WrapToRaw(value: (((BigInteger)left[0]) - right[0]))) { return $"the real difference of {left[0]} and {right[0]} is {difference.Real.Value}"; }
        if (difference.Dual.Value != Oracles.WrapToRaw(value: (((BigInteger)left[1]) - right[1]))) { return $"the dual difference of {left[1]} and {right[1]} is {difference.Dual.Value}"; }
        if (default(FixedDual<FixedQ4816>) != FixedDual<FixedQ4816>.AdditiveIdentity) { return "the default value is not the additive identity"; }
        if ((a + FixedDual<FixedQ4816>.AdditiveIdentity) != a) { return "the additive identity is not a right identity"; }
        if ((FixedDual<FixedQ4816>.AdditiveIdentity + a) != a) { return "the additive identity is not a left identity"; }
        if ((a - a) != FixedDual<FixedQ4816>.AdditiveIdentity) { return "an element less itself is not the additive identity"; }
        if ((-a) != (FixedDual<FixedQ4816>.AdditiveIdentity - a)) { return "negation disagrees with subtraction from the additive identity"; }
        if (FixedDual<FixedQ4816>.MultiplicativeIdentity != new FixedDual<FixedQ4816>(
            Real: FixedQ4816.One,
            Dual: FixedQ4816.Zero
        )) { return "the multiplicative identity is not (One, Zero)"; }
        if ((a * FixedDual<FixedQ4816>.MultiplicativeIdentity) != a) { return "the multiplicative identity is not an exact right identity"; }
        if ((FixedDual<FixedQ4816>.MultiplicativeIdentity * a) != a) { return "the multiplicative identity is not an exact left identity"; }

        return null;
    }
    /// <summary>Proves the two forward-mode seeds and the defining property of the differentiation variable: the seeds
    /// differ in exactly one component, the dual part of <c>Variable(x)·Variable(x)</c> is the EXACT derivative 2x
    /// wrapped, the two one-sided product-rule halves land separately, and a constant carries no sensitivity.</summary>
    /// <param name="left">The first sampled value, raw in its first lane.</param>
    /// <param name="right">The second sampled value, raw in its first lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? DualSeedsAndIdentities(long[] left, long[] right) {
        static string? Check(long raw) {
            var constant = FixedDual.Constant(value: Raw(value: raw));
            var variable = FixedDual.Variable(value: Raw(value: raw));

            if (constant.Real.Value != raw) { return $"Constant moved the value {raw}"; }
            if (constant.Dual != FixedQ4816.AdditiveIdentity) { return $"Constant({raw}) has a non-zero dual part"; }
            if (variable.Real.Value != raw) { return $"Variable moved the value {raw}"; }
            if (variable.Dual != FixedQ4816.MultiplicativeIdentity) { return $"Variable({raw}) does not carry the unit dual part"; }

            return null;
        }

        if (Check(raw: left[0]) is { } first) { return first; }
        if (Check(raw: right[0]) is { } second) { return second; }
        if (FixedDual.Constant(value: FixedQ4816.One) != FixedDual<FixedQ4816>.MultiplicativeIdentity) { return "Constant(One) is not the multiplicative identity"; }

        var x = Raw(value: left[0]);
        var y = Raw(value: right[0]);

        if ((FixedDual.Variable(value: x) * FixedDual.Variable(value: x)).Dual.Value != Oracles.WrapToRaw(value: (((BigInteger)left[0]) << 1))) {
            return $"the dual part of Variable({left[0]})² is not the exact derivative 2x";
        }

        if ((FixedDual.Constant(value: x) * FixedDual.Variable(value: y)).Dual.Value != left[0]) { return $"the dual part of Constant({left[0]})·Variable({right[0]}) is not {left[0]}"; }
        if ((FixedDual.Variable(value: x) * FixedDual.Constant(value: y)).Dual.Value != right[0]) { return $"the dual part of Variable({left[0]})·Constant({right[0]}) is not {right[0]}"; }
        if ((FixedDual.Constant(value: x) * FixedDual.Constant(value: y)).Dual != FixedQ4816.AdditiveIdentity) { return $"the product of two constants carries a sensitivity"; }

        return null;
    }

    /// <summary>Maps a sampled dual divisor onto one the quotient rule defines: a zero REAL part divides nothing,
    /// whatever the dual part carries. Substituted identically in subject and oracle.</summary>
    private static (long U, long V) DualDivisor(long u, long v) =>
        ((0L == u)
            ? (OneRaw, v)
            : (u, v)
        );

    /// <summary>The subject <see cref="FixedDual.Divide"/> at the house scalar carrier.</summary>
    public static (long U, long V) DualDivide(long u1, long v1, long u2, long v2) {
        var (u, v) = DualDivisor(
            u: u2,
            v: v2
        );
        var quotient = FixedDual.Divide(
            left: new FixedDual<FixedQ4816>(
                Real: Raw(value: u1),
                Dual: Raw(value: v1)
            ),
            right: new FixedDual<FixedQ4816>(
                Real: Raw(value: u),
                Dual: Raw(value: v)
            )
        );

        return (quotient.Real.Value, quotient.Dual.Value);
    }
    /// <summary>The oracle dual quotient — the real part one ties-to-even rounding of <c>a·2¹⁶/c</c>, the dual part one
    /// ties-to-even rounding of <c>(bc − ad)·2¹⁶/c²</c>.</summary>
    public static (long U, long V) DualDivideOracle(long u1, long v1, long u2, long v2) {
        var (u, v) = DualDivisor(
            u: u2,
            v: v2
        );

        return (
            Oracles.RoundDyadicRatio(
            numerator: new BigInteger(value: u1),
            denominator: new BigInteger(value: u),
            shift: FixedQ4816.FractionBitCount
        ),
            Oracles.RoundDyadicRatio(
            denominator: (((BigInteger)u) * u),
            numerator: ((((BigInteger)v1) * u) - (((BigInteger)u1) * v)),
            shift: FixedQ4816.FractionBitCount
        )
        );
    }
    /// <summary>Proves the dual quotient's two refusals — the house seam's, which fires BEFORE the dual denominator is
    /// squared, and the generic fallback's, which is the carrier's own — and a hand-derived quotient-rule ladder.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? DualDivideRefusals() {
        foreach (var dual in ((ReadOnlySpan<long>)[0L, OneRaw, long.MinValue, long.MaxValue])) {
            var divisor = new FixedDual<FixedQ4816>(
                Real: FixedQ4816.Zero,
                Dual: Raw(value: dual)
            );

            if (!Throws<DivideByZeroException>(action: () => _ = FixedDual.Divide(
                left: FixedDual<FixedQ4816>.MultiplicativeIdentity,
                right: divisor
            ))) {
                return $"a zero real divisor carrying the dual part {dual} was not refused";
            }
        }

        var lightCone = new FixedDual<FixedSplit>(
            Real: new FixedSplit(
                U: FixedQ4816.One,
                V: FixedQ4816.One
            ),
            Dual: FixedSplit.AdditiveIdentity
        );

        if (!Throws<DivideByZeroException>(action: () => _ = FixedDual.Divide(
            left: FixedDual<FixedSplit>.MultiplicativeIdentity,
            right: lightCone
        ))) {
            return "the generic branch did not refuse a divisor whose real part lies on the split algebra's light cone";
        }

        foreach (var (a, b, c, d, expectedReal, expectedDual) in DualQuotientLadder) {
            var quotient = FixedDual.Divide(
                left: new FixedDual<FixedQ4816>(
                    Real: Raw(value: a),
                    Dual: Raw(value: b)
                ),
                right: new FixedDual<FixedQ4816>(
                    Real: Raw(value: c),
                    Dual: Raw(value: d)
                )
            );

            if (
                (quotient.Real.Value != expectedReal) ||
                (quotient.Dual.Value != expectedDual)
            ) {
                return $"({a},{b})/({c},{d}) is ({quotient.Real.Value},{quotient.Dual.Value}), expected ({expectedReal},{expectedDual})";
            }
        }

        return null;
    }
    /// <summary>Proves the three transcendental lifts: each real part is the scalar kernel's own answer, each dual part
    /// is ONE rounding of the exact chain-rule expression, the two documented refusals hold, and the square root's lift
    /// never divides by zero.</summary>
    /// <param name="left">The operand's real and dual parts, raw; the real part is folded positive.</param>
    /// <param name="right">The swept angle's real and dual parts, raw; the real part also drives the refusal ladder.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? DualTranscendentalLifts(long[] left, long[] right) {
        var realRaw = PositiveRaw(raw: left[0]);
        var dualRaw = left[1];
        var value = new FixedDual<FixedQ4816>(
            Real: Raw(value: realRaw),
            Dual: Raw(value: dualRaw)
        );
        var logarithm = FixedDual.Log2(value: value);

        if (logarithm.Real != FixedQ4816.Log2(value: Raw(value: realRaw))) { return $"the real part of Log2({realRaw}) is not the scalar logarithm"; }

        var expectedLogarithmDual = Oracles.RoundDyadicRatio(
            numerator: (((BigInteger)dualRaw) * Log2ERaw),
            denominator: new BigInteger(value: realRaw),
            shift: 0
        );

        if (logarithm.Dual.Value != expectedLogarithmDual) { return $"the dual part of Log2({realRaw}, {dualRaw}) is {logarithm.Dual.Value}, expected {expectedLogarithmDual}"; }

        var root = FixedQ4816.Sqrt(value: Raw(value: realRaw));
        var rooted = FixedDual.Sqrt(value: value);

        if (rooted.Real != root) { return $"the real part of Sqrt({realRaw}) is not the scalar root"; }
        if (root.Value < 256L) { return $"the root of the positive raw {realRaw} is {root.Value}, below the 256 floor the lift relies on"; }
        if ((root * Raw(value: (OneRaw << 1))).Value != (2L * root.Value)) { return $"the doubling of the root {root.Value} is not exact"; }

        // The lift divides by the root at Q32 — ⌊√(a·2⁴⁸)⌋, sixteen bits finer than the returned Q16 root — so the
        // expectation is ONE rounding of b·2³²/(2·R₃₂), formed here from the independent integer root.
        var wideRoot = Oracles.IntegerSquareRoot(value: (new BigInteger(value: realRaw) << (3 * FixedQ4816.FractionBitCount)));
        var expectedRootDual = Oracles.RoundDyadicRatio(
            numerator: new BigInteger(value: dualRaw),
            denominator: (2 * wideRoot),
            shift: (2 * FixedQ4816.FractionBitCount)
        );

        if (rooted.Dual.Value != expectedRootDual) { return $"the dual part of Sqrt({realRaw}, {dualRaw}) is {rooted.Dual.Value}, expected {expectedRootDual}"; }

        var (sin, cos) = FixedQ4816.SinCos(angle: Raw(value: right[0]));
        var (liftedSin, liftedCos) = FixedDual.SinCos(angle: new FixedDual<FixedQ4816>(
            Real: Raw(value: right[0]),
            Dual: Raw(value: right[1])
        ));

        if (liftedSin.Real != sin) { return $"the real part of the lifted sine at {right[0]} is not the scalar sine"; }
        if (liftedCos.Real != cos) { return $"the real part of the lifted cosine at {right[0]} is not the scalar cosine"; }

        var expectedSinDual = Oracles.RoundDyadic(
            exact: (((BigInteger)right[1]) * cos.Value),
            shift: FixedQ4816.FractionBitCount
        );
        var expectedCosDual = Oracles.WrapToRaw(value: -new BigInteger(value: Oracles.RoundDyadic(
            exact: (((BigInteger)right[1]) * sin.Value),
            shift: FixedQ4816.FractionBitCount
        )));

        if (liftedSin.Dual.Value != expectedSinDual) { return $"the dual part of the lifted sine at ({right[0]},{right[1]}) is {liftedSin.Dual.Value}, expected {expectedSinDual}"; }
        if (liftedCos.Dual.Value != expectedCosDual) { return $"the dual part of the lifted cosine at ({right[0]},{right[1]}) is {liftedCos.Dual.Value}, expected {expectedCosDual}"; }

        var refusedRaw = NonPositiveRaw(raw: right[0]);
        var refused = new FixedDual<FixedQ4816>(
            Real: Raw(value: refusedRaw),
            Dual: Raw(value: dualRaw)
        );

        if (FixedDual.Log2(value: refused) != new FixedDual<FixedQ4816>(
            Real: FixedQ4816.MinValue,
            Dual: FixedQ4816.Zero
        )) { return $"Log2 did not refuse the non-positive real part {refusedRaw}"; }
        if (FixedDual.Sqrt(value: refused) != FixedDual<FixedQ4816>.AdditiveIdentity) { return $"Sqrt did not refuse the non-positive real part {refusedRaw}"; }

        return null;
    }

    private static FixedDual<FixedQuaternion> DualQuaternionOf(ReadOnlySpan<long> lanes) =>
        new(
            Real: new FixedQuaternion(
                X: Raw(value: lanes[0]),
                Y: Raw(value: lanes[1]),
                Z: Raw(value: lanes[2]),
                W: Raw(value: lanes[3])
            ),
            Dual: new FixedQuaternion(
                X: Raw(value: lanes[4]),
                Y: Raw(value: lanes[5]),
                Z: Raw(value: lanes[6]),
                W: Raw(value: lanes[7])
            )
        );

    /// <summary>The subject <see cref="FixedDual{TValue}"/> product at the <see cref="FixedQuaternion"/> carrier — the
    /// production dual quaternion — as an eight-lane vector operation, the real block's lanes first.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void DualQuaternionMultiplyLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var product = (DualQuaternionOf(lanes: left) * DualQuaternionOf(lanes: right));

        WriteQuaternionLanes(
            value: product.Real,
            result: result[..4]
        );
        WriteQuaternionLanes(
            value: product.Dual,
            result: result[4..]
        );
    }
    /// <summary>The shared-nothing oracle for the dual quaternion product — the doubling recursion's charged sums with
    /// ONE rounding per lane, read through the declared lane permutation.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void DualQuaternionMultiplyOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> leftDoubling = stackalloc long[8];
        Span<long> rightDoubling = stackalloc long[8];
        Span<long> product = stackalloc long[8];

        QuaternionToDoublingLanes(
            quaternion: left[..4],
            doubling: leftDoubling[..4]
        );
        QuaternionToDoublingLanes(
            quaternion: left[4..],
            doubling: leftDoubling[4..]
        );
        QuaternionToDoublingLanes(
            quaternion: right[..4],
            doubling: rightDoubling[..4]
        );
        QuaternionToDoublingLanes(
            quaternion: right[4..],
            doubling: rightDoubling[4..]
        );
        Oracles.DoublingDualProduct(
            floors: 2,
            left: leftDoubling,
            result: product,
            right: rightDoubling,
            shift: FixedQ4816.FractionBitCount
        );
        DoublingToQuaternionLanes(
            doubling: product[..4],
            quaternion: result[..4]
        );
        DoublingToQuaternionLanes(
            doubling: product[4..],
            quaternion: result[4..]
        );
    }

    private static FixedDual<FixedSplit> DualSplitOf(ReadOnlySpan<long> lanes) =>
        new(
            Real: new FixedSplit(
                U: Raw(value: lanes[0]),
                V: Raw(value: lanes[1])
            ),
            Dual: new FixedSplit(
                U: Raw(value: lanes[2]),
                V: Raw(value: lanes[3])
            )
        );

    /// <summary>The subject <see cref="FixedDual{TValue}"/> product at the <see cref="FixedSplit"/> carrier — neither
    /// house type, so the GENERIC three-multiply fallback runs — as a four-lane vector operation.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void DualSplitMultiplyLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var product = (DualSplitOf(lanes: left) * DualSplitOf(lanes: right));

        result[0] = product.Real.U.Value;
        result[1] = product.Real.V.Value;
        result[2] = product.Dual.U.Value;
        result[3] = product.Dual.V.Value;
    }
    /// <summary>The shared-nothing oracle for the generic dual product over the split relation — two independent
    /// quadratic products summed through the carrier's wrapping add, the TWO-rounding discipline re-derived exactly.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void DualSplitMultiplyOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        Oracles.DualOverQuadraticProduct(
            left: left,
            pRaw: 0L,
            qRaw: OneRaw,
            result: result,
            right: right
        );

    // The quotient-rule ladder, hand-derived from (a/c, (bc − ad)/c²) evaluated in exact rationals and rounded once:
    // Variable(x)/Constant(c), Constant(k)/Variable(x), and three rows with both parts live.
    private static readonly (long A, long B, long C, long D, long ExpectedReal, long ExpectedDual)[] DualQuotientLadder = [
        (196608L, 65536L, 131072L, 0L, 98304L, 32768L),
        (327680L, 0L, 196608L, 65536L, 109227L, -36409L),
        (65536L, 65536L, 65536L, 65536L, 65536L, 0L),
        (-458752L, 131072L, 327680L, -65536L, -91750L, 7864L),
        (100000L, 33333L, 77777L, 12345L, 84261L, 14713L),
    ];

    // ---- FixedQuaternion ----

    private static FixedQuaternion QuaternionOf(ReadOnlySpan<long> lanes) =>
        new(
            X: Raw(value: lanes[0]),
            Y: Raw(value: lanes[1]),
            Z: Raw(value: lanes[2]),
            W: Raw(value: lanes[3])
        );
    private static void WriteQuaternionLanes(FixedQuaternion value, Span<long> result) {
        result[0] = value.X.Value;
        result[1] = value.Y.Value;
        result[2] = value.Z.Value;
        result[3] = value.W.Value;
    }

    /// <summary>The subject Hamilton product as a four-lane vector operation.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void QuaternionMultiplyLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        WriteQuaternionLanes(
            value: (QuaternionOf(lanes: left) * QuaternionOf(lanes: right)),
            result: result
        );
    /// <summary>The shared-nothing oracle for the Hamilton product — the Cayley–Dickson doubling recursion walked to
    /// basis vectors as a twisted group algebra, read through the declared lane permutation.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void QuaternionMultiplyOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> leftDoubling = stackalloc long[4];
        Span<long> rightDoubling = stackalloc long[4];
        Span<long> product = stackalloc long[4];

        QuaternionToDoublingLanes(
            doubling: leftDoubling,
            quaternion: left
        );
        QuaternionToDoublingLanes(
            doubling: rightDoubling,
            quaternion: right
        );
        Oracles.CayleyDicksonProduct(
            floors: 2,
            left: leftDoubling,
            result: product,
            right: rightDoubling,
            shift: FixedQ4816.FractionBitCount
        );
        DoublingToQuaternionLanes(
            doubling: product,
            quaternion: result
        );
    }
    /// <summary>Proves the quaternion dot product is ONE rounding of the exact four-product sum, that it is symmetric,
    /// and that dotting against the identity projects the scalar lane exactly.</summary>
    /// <param name="left">The first quaternion's lanes, raw.</param>
    /// <param name="right">The second quaternion's lanes, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionDotExact(long[] left, long[] right) {
        var a = QuaternionOf(lanes: left);
        var b = QuaternionOf(lanes: right);
        var expected = Oracles.LaneDotProduct(
            left: left,
            right: right,
            shift: FixedQ4816.FractionBitCount
        );

        if (FixedQuaternion.Dot(
            left: a,
            right: b
        ).Value != expected) {
            return $"the dot product is {FixedQuaternion.Dot(
            left: a,
            right: b
        ).Value}, expected {expected}";
        }
        if (FixedQuaternion.Dot(
            left: b,
            right: a
        ).Value != expected) { return "the dot product is not symmetric in its operands"; }
        if (FixedQuaternion.Dot(
            left: a,
            right: FixedQuaternion.Identity
        ).Value != left[3]) {
            return $"dotting against the identity gave {FixedQuaternion.Dot(
            left: a,
            right: FixedQuaternion.Identity
        ).Value}, not the scalar lane {left[3]}";
        }

        return null;
    }
    /// <summary>Proves the scalar product is four independent one-rounding carrier multiplies, and pins its three exact
    /// scalars: one is the identity map, zero the additive identity, and negative one the group's negation.</summary>
    /// <param name="left">The quaternion's lanes, raw.</param>
    /// <param name="right">The scalar in its first lane, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionScaleExact(long[] left, long[] right) {
        var value = QuaternionOf(lanes: left);
        var scalar = right[0];
        Span<long> lanes = stackalloc long[4];

        WriteQuaternionLanes(
            value: (value * Raw(value: scalar)),
            result: lanes
        );

        for (var lane = 0; (lane < 4); ++lane) {
            var expected = Oracles.RoundDyadic(
                exact: (((BigInteger)left[lane]) * scalar),
                shift: FixedQ4816.FractionBitCount
            );

            if (lanes[lane] != expected) { return $"lane {lane} scaled by {scalar} is {lanes[lane]}, expected {expected}"; }
        }

        if ((value * FixedQ4816.One) != value) { return "scaling by one is not the identity map"; }
        if ((value * FixedQ4816.Zero) != FixedQuaternion.AdditiveIdentity) { return "scaling by zero is not the additive identity"; }
        if ((value * FixedQ4816.NegativeOne) != (-value)) { return "scaling by negative one disagrees with the group's negation"; }

        return null;
    }
    /// <summary>Proves <see cref="FixedQuaternion"/>'s additive group is EXACT at every swept pair, that the positional
    /// constructor round-trips all four readers in the declared order, that the identity is read through two names, and
    /// that it is a two-sided multiplicative identity with no rounding anywhere.</summary>
    /// <param name="left">The multiplicand's lanes, raw.</param>
    /// <param name="right">The multiplier's lanes, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionAdditiveGroupExact(long[] left, long[] right) {
        var a = QuaternionOf(lanes: left);
        var b = QuaternionOf(lanes: right);
        Span<long> readers = stackalloc long[4];

        WriteQuaternionLanes(
            result: readers,
            value: a
        );

        for (var lane = 0; (lane < 4); ++lane) {
            if (readers[lane] != left[lane]) { return $"the constructor moved lane {lane} from {left[lane]} to {readers[lane]}"; }
        }

        Span<long> sum = stackalloc long[4];
        Span<long> difference = stackalloc long[4];

        WriteQuaternionLanes(
            result: sum,
            value: (a + b)
        );
        WriteQuaternionLanes(
            result: difference,
            value: (a - b)
        );

        for (var lane = 0; (lane < 4); ++lane) {
            if (sum[lane] != Oracles.WrapToRaw(value: (((BigInteger)left[lane]) + right[lane]))) { return $"lane {lane} of the sum is {sum[lane]}"; }
            if (difference[lane] != Oracles.WrapToRaw(value: (((BigInteger)left[lane]) - right[lane]))) { return $"lane {lane} of the difference is {difference[lane]}"; }
        }

        if (default(FixedQuaternion) != FixedQuaternion.AdditiveIdentity) { return "the default value is not the additive identity"; }
        if (FixedQuaternion.Identity != new FixedQuaternion(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero,
            W: FixedQ4816.One
        )) { return "the identity is not (Zero, Zero, Zero, One)"; }
        if (FixedQuaternion.MultiplicativeIdentity != FixedQuaternion.Identity) { return "the multiplicative identity and the identity are different values"; }
        if ((a + FixedQuaternion.AdditiveIdentity) != a) { return "the additive identity is not a right identity"; }
        if ((FixedQuaternion.AdditiveIdentity + a) != a) { return "the additive identity is not a left identity"; }
        if ((a - a) != FixedQuaternion.AdditiveIdentity) { return "an element less itself is not the additive identity"; }
        if ((-a) != (FixedQuaternion.AdditiveIdentity - a)) { return "negation disagrees with subtraction from the additive identity"; }
        if ((a * FixedQuaternion.Identity) != a) { return "the identity is not an exact right multiplicative identity"; }
        if ((FixedQuaternion.Identity * a) != a) { return "the identity is not an exact left multiplicative identity"; }

        return null;
    }
    /// <summary>Proves conjugation is the ANTI-automorphism — <c>conj(a·b) == conj(b)·conj(a)</c>, operands reversed —
    /// exactly on the bounded sublattice, that the reversal is load-bearing at a hand-listed non-commuting pair, that
    /// conjugation is an involution over the full raw range and fixes exactly the scalars, and that <c>q·conj(q)</c> is
    /// the norm form.</summary>
    /// <param name="left">The multiplicand's lanes, raw, on the sublattice.</param>
    /// <param name="right">The multiplier's lanes, raw, on the sublattice.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionConjugateAntiautomorphism(long[] left, long[] right) {
        var a = QuaternionOf(lanes: left);
        var b = QuaternionOf(lanes: right);

        if ((a * b).Conjugate() != (b.Conjugate() * a.Conjugate())) { return "conj(a·b) is not conj(b)·conj(a)"; }
        if (a.Conjugate().Conjugate() != a) { return "conjugation is not an involution"; }
        if ((a.Conjugate() == a) != ((0L == left[0]) && (0L == left[1]) && (0L == left[2]))) { return "conjugation does not fix exactly the scalars"; }

        var normForm = (a * a.Conjugate());
        var squares = ((((((BigInteger)left[0]) * left[0]) + (((BigInteger)left[1]) * left[1])) + (((BigInteger)left[2]) * left[2])) + (((BigInteger)left[3]) * left[3]));

        if (
            (normForm.X.Value != 0L) ||
            (normForm.Y.Value != 0L) ||
            (normForm.Z.Value != 0L)
        ) { return "q·conj(q) has a non-zero vector part"; }
        if (normForm.W.Value != Oracles.RoundDyadic(
            exact: squares,
            shift: FixedQ4816.FractionBitCount
        )) { return $"the scalar lane of q·conj(q) is {normForm.W.Value}, not the exact four-square sum"; }

        // The reversal is measured, not assumed: the planar form fails at a hand-listed pair that does not commute.
        var i = new FixedQuaternion(
            X: FixedQ4816.One,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero,
            W: FixedQ4816.Zero
        );
        var j = new FixedQuaternion(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.One,
            Z: FixedQ4816.Zero,
            W: FixedQ4816.Zero
        );

        if ((i * j).Conjugate() == (i.Conjugate() * j.Conjugate())) { return "the planar form conj(a)·conj(b) agreed at the non-commuting witness (i, j)"; }
        if ((i * j).Conjugate() != (j.Conjugate() * i.Conjugate())) { return "the reversed form failed at the non-commuting witness (i, j)"; }

        foreach (var raw in ((ReadOnlySpan<long>)[long.MinValue, (long.MinValue + 1L), long.MaxValue, 0L])) {
            var extreme = new FixedQuaternion(
                X: Raw(value: raw),
                Y: Raw(value: raw),
                Z: Raw(value: raw),
                W: Raw(value: raw)
            );

            if (extreme.Conjugate().Conjugate() != extreme) { return $"conjugation is not an involution at the extreme raw {raw}"; }
        }

        return null;
    }
    /// <summary>Proves the two norm readers against exact arbitrary-width arithmetic — the FOUR-square case, where the
    /// sum genuinely reaches <c>2¹²⁸</c> — and pins the all-<see cref="long.MinValue"/> carry witness outright.</summary>
    /// <param name="left">The first quaternion's lanes, raw.</param>
    /// <param name="right">The second quaternion's lanes, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionNormExact(long[] left, long[] right) {
        static string? Check(ReadOnlySpan<long> lanes) {
            var value = QuaternionOf(lanes: lanes);
            var exact = BigInteger.Zero;

            foreach (var lane in lanes) { exact += (((BigInteger)lane) * lane); }

            var overflowed = (exact >= FourSquareCarry);
            var expectedRoot = Oracles.NearestIntegerRoot(value: exact);
            var expectedSquared = Oracles.RoundToEvenUnits(
                magnitude: exact,
                shift: FixedQ4816.FractionBitCount
            );
            var rootFits = (!overflowed && (expectedRoot <= long.MaxValue));
            var squaredFits = (!overflowed && (expectedSquared <= long.MaxValue));

            if (value.TryLength(length: out var length) != rootFits) { return $"TryLength reported the wrong verdict at [{lanes[0]},{lanes[1]},{lanes[2]},{lanes[3]}]"; }
            if (
                rootFits &&
                (length.Value != expectedRoot)
            ) { return $"the length is {length.Value}, expected {expectedRoot}"; }
            if (value.TryLengthSquared(squaredLength: out var squaredLength) != squaredFits) { return $"TryLengthSquared reported the wrong verdict at [{lanes[0]},{lanes[1]},{lanes[2]},{lanes[3]}]"; }
            if (
                squaredFits &&
                (squaredLength.Value != expectedSquared)
            ) { return $"the squared length is {squaredLength.Value}, expected {expectedSquared}"; }
            if (value.Length != (rootFits
                ? length
                : FixedQ4816.MaxValue)) { return "the saturating length disagrees with its Try sibling"; }
            if (value.LengthSquared != (squaredFits
                ? squaredLength
                : FixedQ4816.MaxValue)) { return "the saturating squared length disagrees with its Try sibling"; }

            return null;
        }

        if (Check(lanes: left) is { } first) { return first; }
        if (Check(lanes: right) is { } second) { return second; }

        var carry = new FixedQuaternion(
            X: FixedQ4816.MinValue,
            Y: FixedQ4816.MinValue,
            Z: FixedQ4816.MinValue,
            W: FixedQ4816.MinValue
        );

        if (carry.TryLength(length: out _)) { return "the all-MinValue quaternion's length did not refuse"; }
        if (carry.TryLengthSquared(squaredLength: out _)) { return "the all-MinValue quaternion's squared length did not refuse"; }
        if (carry.Length != FixedQ4816.MaxValue) { return "the all-MinValue quaternion's saturating length is not MaxValue"; }
        if (carry.LengthSquared != FixedQ4816.MaxValue) { return "the all-MinValue quaternion's saturating squared length is not MaxValue"; }

        return null;
    }
    /// <summary>Proves the inverse is one ties-to-even rounding per lane of the exact rational <c>∓qᵢ·2³²/S</c>, pins the
    /// two documented poles, and proves the overflowing four-square arm through the SAME per-lane oracle — the
    /// kernel's early-out answers the correctly rounded inverse, the zero quaternion, not a sentinel.</summary>
    /// <param name="left">The quaternion's lanes, raw.</param>
    /// <param name="right">The second sampled quaternion's lanes, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionInverseExact(long[] left, long[] right) {
        static string? Check(ReadOnlySpan<long> lanes) {
            var value = QuaternionOf(lanes: lanes);
            var inverse = value.Inverse();
            var exact = BigInteger.Zero;

            foreach (var lane in lanes) { exact += (((BigInteger)lane) * lane); }

            if (exact.IsZero) {
                return ((inverse == FixedQuaternion.Identity)
                ? null
                : "the zero quaternion did not invert to the identity"
            );
            }

            // No early-out at an overflowing four-square sum: the oracle divides in BigInteger, where S ≥ 2¹²⁸ makes
            // every |∓qᵢ|·2³² / S at most 2⁻³³ — far below the half-raw rounding threshold — so the ordinary lane loop
            // proves the kernel's shortcut answer, the zero quaternion, IS the correctly rounded inverse lane for lane.

            Span<long> inverted = stackalloc long[4];

            WriteQuaternionLanes(
                result: inverted,
                value: inverse
            );

            for (var lane = 0; (lane < 4); ++lane) {
                var numerator = ((3 == lane)
                    ? new BigInteger(value: lanes[lane])
                    : -new BigInteger(value: lanes[lane])
                );
                var expected = Oracles.RoundDyadicRatio(
                    denominator: exact,
                    numerator: numerator,
                    shift: (FixedQ4816.FractionBitCount * 2)
                );

                if (inverted[lane] != expected) { return $"lane {lane} of the inverse is {inverted[lane]}, expected {expected}"; }
                if (
                    (inverted[lane] != 0L) &&
                    (Math.Sign(value: inverted[lane]) != ((3 == lane)
                    ? Math.Sign(value: lanes[lane])
                    : -Math.Sign(value: lanes[lane])))
                ) {
                    return $"lane {lane} of the inverse carries the wrong sign";
                }
            }

            return null;
        }

        if (Check(lanes: left) is { } first) { return first; }
        if (Check(lanes: right) is { } second) { return second; }
        if (FixedQuaternion.AdditiveIdentity.Inverse() != FixedQuaternion.Identity) { return "the zero quaternion did not invert to the identity"; }
        if (FixedQuaternion.Identity.Inverse() != FixedQuaternion.Identity) { return "the identity did not invert to itself exactly"; }

        var carry = new FixedQuaternion(
            X: FixedQ4816.MinValue,
            Y: FixedQ4816.MinValue,
            Z: FixedQ4816.MinValue,
            W: FixedQ4816.MinValue
        );

        if (carry.Inverse() != default) { return "the all-MinValue witness did not return the zero sentinel"; }

        return null;
    }
    /// <summary>Proves the quaternion unit direction lands within one raw of the EXACT Q16 direction at every swept
    /// element, that the zero quaternion answers with the identity, that no non-zero quaternion normalizes to zero, and
    /// that normalization commutes with negation wherever the negation names a different element.</summary>
    /// <param name="left">The first quaternion's lanes, raw.</param>
    /// <param name="right">The second quaternion's lanes, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionNormalizeUnitDirection(long[] left, long[] right) {
        static string? Check(long[] lanes) {
            var value = QuaternionOf(lanes: lanes);
            var unit = value.Normalize();

            if ((0L == (lanes[0] | lanes[1] | lanes[2] | lanes[3]))) {
                return ((unit == FixedQuaternion.Identity)
                    ? null
                    : "the zero quaternion did not normalize to the identity"
                );
            }

            Span<long> unitLanes = stackalloc long[4];

            WriteQuaternionLanes(
                result: unitLanes,
                value: unit
            );

            var components = new BigInteger[4];
            var returned = new long[4];

            for (var lane = 0; (lane < 4); ++lane) {
                components[lane] = new BigInteger(value: lanes[lane]);
                returned[lane] = unitLanes[lane];
            }

            var offending = Oracles.FirstNonUnitLane(
                components: components,
                tolerance: 1L,
                unit: returned
            );

            if (offending >= 0) { return $"lane {offending} of the unit direction is farther than one raw from the exact direction"; }
            if (0L == (returned[0] | returned[1] | returned[2] | returned[3])) { return "a non-zero quaternion normalized to the additive identity"; }

            var negatable = true;

            foreach (var lane in lanes) { negatable &= (long.MinValue != lane); }

            if (
                negatable &&
                ((-value).Normalize() != (-unit))
            ) { return "normalization does not commute with negation"; }

            return null;
        }

        return (Check(lanes: left) ?? Check(lanes: right));
    }
    /// <summary>Proves the rotation sandwich stage for stage against exact arbitrary-width arithmetic, that
    /// <see cref="FixedQuaternion.RotateInverse"/> is the conjugate's rotation, that both unit poles are exact, that the
    /// hand-derived geometric ladder lands, and that the inverse really inverts.</summary>
    /// <param name="left">The rotor's lanes, raw.</param>
    /// <param name="right">The vector's lanes in its first three positions, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionRotateExact(long[] left, long[] right) {
        var rotor = QuaternionOf(lanes: left);
        var vector = new FixedVector3(
            X: Raw(value: right[0]),
            Y: Raw(value: right[1]),
            Z: Raw(value: right[2])
        );
        var image = rotor.Rotate(vector: vector);
        Span<long> expected = stackalloc long[3];

        Oracles.QuaternionSandwich(
            rotation: left,
            vector: ((ReadOnlySpan<long>)right)[..3],
            shift: FixedQ4816.FractionBitCount,
            result: expected
        );

        if (image.X.Value != expected[0]) { return $"the rotated X lane is {image.X.Value}, expected {expected[0]}"; }
        if (image.Y.Value != expected[1]) { return $"the rotated Y lane is {image.Y.Value}, expected {expected[1]}"; }
        if (image.Z.Value != expected[2]) { return $"the rotated Z lane is {image.Z.Value}, expected {expected[2]}"; }
        if (rotor.RotateInverse(vector: vector) != rotor.Conjugate().Rotate(vector: vector)) { return "RotateInverse is not the conjugate's rotation"; }
        if (FixedQuaternion.Identity.Rotate(vector: vector) != vector) { return "the identity did not rotate the vector to itself"; }
        if ((-FixedQuaternion.Identity).Rotate(vector: vector) != vector) { return "the identity's negation did not rotate the vector to itself"; }

        foreach (var (rotorLanes, vectorLanes, imageLanes) in QuaternionRotateLadder) {
            var ladderRotor = QuaternionOf(lanes: rotorLanes);
            var ladderVector = new FixedVector3(
                X: Raw(value: vectorLanes[0]),
                Y: Raw(value: vectorLanes[1]),
                Z: Raw(value: vectorLanes[2])
            );
            var ladderImage = ladderRotor.Rotate(vector: ladderVector);

            if (Math.Abs(value: (ladderImage.X.Value - imageLanes[0])) > QuaternionRotateTolerance) { return $"the ladder image's X lane is {ladderImage.X.Value}, expected {imageLanes[0]}"; }
            if (Math.Abs(value: (ladderImage.Y.Value - imageLanes[1])) > QuaternionRotateTolerance) { return $"the ladder image's Y lane is {ladderImage.Y.Value}, expected {imageLanes[1]}"; }
            if (Math.Abs(value: (ladderImage.Z.Value - imageLanes[2])) > QuaternionRotateTolerance) { return $"the ladder image's Z lane is {ladderImage.Z.Value}, expected {imageLanes[2]}"; }

            var restored = ladderRotor.RotateInverse(vector: ladderImage);

            if (Math.Abs(value: (restored.X.Value - vectorLanes[0])) > QuaternionRoundTripTolerance) { return $"the round trip's X lane is {restored.X.Value}, expected {vectorLanes[0]}"; }
            if (Math.Abs(value: (restored.Y.Value - vectorLanes[1])) > QuaternionRoundTripTolerance) { return $"the round trip's Y lane is {restored.Y.Value}, expected {vectorLanes[1]}"; }
            if (Math.Abs(value: (restored.Z.Value - vectorLanes[2])) > QuaternionRoundTripTolerance) { return $"the round trip's Z lane is {restored.Z.Value}, expected {vectorLanes[2]}"; }
        }

        return null;
    }
    /// <summary>Proves the axis-angle constructor on a hand-derived nine-row ladder taken at the ROUNDED half-angle raw
    /// the kernel forms, that a zero angle is the identity bit-for-bit, and that the vector part is placed lane for lane
    /// along the axis.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionAxisAngleLadderClaim() {
        Span<long> lanes = stackalloc long[4];

        foreach (var (axis, angleRaw, expected, tolerance) in QuaternionAxisAngleLadder) {
            var value = FixedQuaternion.FromAxisAngle(
                axis: new FixedVector3(
                    X: Raw(value: axis[0]),
                    Y: Raw(value: axis[1]),
                    Z: Raw(value: axis[2])
                ),
                angle: Raw(value: angleRaw)
            );

            WriteQuaternionLanes(
                result: lanes,
                value: value
            );

            for (var lane = 0; (lane < 4); ++lane) {
                if (Math.Abs(value: (lanes[lane] - expected[lane])) > tolerance) { return $"lane {lane} of FromAxisAngle([{axis[0]},{axis[1]},{axis[2]}], {angleRaw}) is {lanes[lane]}, expected {expected[lane]} within {tolerance}"; }
            }

            // The vector part sits along the axis lane for lane: a zero axis lane meets a zero result lane.
            for (var lane = 0; (lane < 3); ++lane) {
                if (
                    (0L == axis[lane]) &&
                    (0L != lanes[lane])
                ) { return $"lane {lane} of the vector part is non-zero where the axis lane is zero"; }
            }

            if (FixedQuaternion.FromAxisAngle(
                axis: new FixedVector3(
                    X: Raw(value: axis[0]),
                    Y: Raw(value: axis[1]),
                    Z: Raw(value: axis[2])
                ),
                angle: FixedQ4816.Zero
            ) != FixedQuaternion.Identity) {
                return $"a zero angle about [{axis[0]},{axis[1]},{axis[2]}] is not the identity";
            }
        }

        return null;
    }
    /// <summary>Proves the exponential and logarithm seam on hand-derived ladders, its three exact poles, the round trip
    /// the doc claims (sign included), and that a bivector beyond a full turn wraps through the turn-domain reduction
    /// rather than saturating.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionExpLogSeam() {
        if (FixedQuaternion.Exp(bivector: FixedVector3.Zero) != FixedQuaternion.Identity) { return "the zero bivector did not exponentiate to the identity"; }
        if (FixedQuaternion.Identity.Log() != FixedVector3.Zero) { return "the identity did not log to the zero bivector"; }
        if ((-FixedQuaternion.Identity).Log() != FixedVector3.Zero) { return "the identity's negation did not log to the zero bivector"; }

        Span<long> lanes = stackalloc long[4];

        foreach (var (bivector, expected) in QuaternionExpLadder) {
            var value = FixedQuaternion.Exp(bivector: new FixedVector3(
                X: Raw(value: bivector[0]),
                Y: Raw(value: bivector[1]),
                Z: Raw(value: bivector[2])
            ));

            WriteQuaternionLanes(
                result: lanes,
                value: value
            );

            for (var lane = 0; (lane < 4); ++lane) {
                if (Math.Abs(value: (lanes[lane] - expected[lane])) > QuaternionExpTolerance) { return $"lane {lane} of Exp([{bivector[0]},{bivector[1]},{bivector[2]}]) is {lanes[lane]}, expected {expected[lane]}"; }
            }
        }

        foreach (var (quaternion, expected) in QuaternionLogLadder) {
            var value = QuaternionOf(lanes: quaternion).Log();

            if (Math.Abs(value: (value.X.Value - expected[0])) > QuaternionLogTolerance) { return $"the X lane of the logarithm is {value.X.Value}, expected {expected[0]}"; }
            if (Math.Abs(value: (value.Y.Value - expected[1])) > QuaternionLogTolerance) { return $"the Y lane of the logarithm is {value.Y.Value}, expected {expected[1]}"; }
            if (Math.Abs(value: (value.Z.Value - expected[2])) > QuaternionLogTolerance) { return $"the Z lane of the logarithm is {value.Z.Value}, expected {expected[2]}"; }

            // Exp(q.Log()) recovers q — the SIGN survives — except at the vector-free W < 0 pole the doc excludes.
            if (
                (0L != (quaternion[0] | quaternion[1] | quaternion[2])) ||
                (quaternion[3] >= 0L)
            ) {
                WriteQuaternionLanes(
                    value: FixedQuaternion.Exp(bivector: value),
                    result: lanes
                );

                for (var lane = 0; (lane < 4); ++lane) {
                    if (Math.Abs(value: (lanes[lane] - quaternion[lane])) > QuaternionExpLogRoundTripTolerance) { return $"lane {lane} of Exp(Log(q)) is {lanes[lane]}, expected {quaternion[lane]}"; }
                }
            }
        }

        // A bivector beyond a full turn wraps: 2π raw is 411775, so the quarter turn plus one full turn is the same
        // rotation, and a bivector far beyond the carrier's angular range still returns a unit rotation.
        var quarter = FixedQuaternion.Exp(bivector: new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: Raw(value: 51472L)
        ));
        var wrapped = FixedQuaternion.Exp(bivector: new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: Raw(value: (51472L + 411775L))
        ));

        if (Math.Abs(value: (wrapped.Z.Value - quarter.Z.Value)) > QuaternionExpTolerance) { return $"the wrapped bivector's Z lane is {wrapped.Z.Value}, expected {quarter.Z.Value}"; }
        if (Math.Abs(value: (wrapped.W.Value - quarter.W.Value)) > QuaternionExpTolerance) { return $"the wrapped bivector's W lane is {wrapped.W.Value}, expected {quarter.W.Value}"; }

        var enormous = FixedQuaternion.Exp(bivector: new FixedVector3(
            X: FixedQ4816.MaxValue,
            Y: FixedQ4816.MaxValue,
            Z: FixedQ4816.MaxValue
        ));

        if (
            !enormous.TryLength(length: out var enormousLength) ||
            (Math.Abs(value: (enormousLength.Value - OneRaw)) > QuaternionUnitTolerance)
        ) {
            return "a bivector at the carrier's extreme did not exponentiate to a unit rotation";
        }

        return null;
    }
    /// <summary>Proves the presentation seam onto <see cref="System.Numerics.Quaternion"/> against hand-derived IEEE-754
    /// <c>binary32</c> bit patterns, compared through <see cref="BitConverter.SingleToInt32Bits"/> so no floating-point
    /// arithmetic enters the law: every ladder raw is placed at X, then Y, then Z, then W in turn while the other three
    /// lanes carry four distinct sentinels, and every lane reads back exactly what was put in it.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionPresentationMatchesLadder() {
        Span<long> lanes = stackalloc long[4];
        Span<int> read = stackalloc int[4];

        foreach (var (raw, bits) in PresentationBinary32Ladder) {
            for (var placed = 0; (placed < 4); ++placed) {
                for (var lane = 0; (lane < 4); ++lane) {
                    lanes[lane] = ((lane == placed)
                        ? raw
                        : QuaternionPresentationSentinels[lane]
                    );
                }

                var presented = QuaternionOf(lanes: lanes).ToQuaternion();

                read[0] = BitConverter.SingleToInt32Bits(value: presented.X);
                read[1] = BitConverter.SingleToInt32Bits(value: presented.Y);
                read[2] = BitConverter.SingleToInt32Bits(value: presented.Z);
                read[3] = BitConverter.SingleToInt32Bits(value: presented.W);

                for (var lane = 0; (lane < 4); ++lane) {
                    var expected = ((lane == placed)
                        ? bits
                        : QuaternionPresentationSentinelBits[lane]
                    );

                    if (read[lane] != expected) { return $"the raw {raw} placed at lane {placed} read {read[lane]:X8} at lane {lane}, expected {expected:X8}"; }
                }
            }
        }

        return null;
    }

    // The four lane sentinels: distinct from EACH OTHER and from every row of the shared binary32 ladder, so a
    // transposition among the three lanes the ladder raw is not sitting in fails as loudly as one involving it. Each is
    // a whole or half unit whose binary32 image is derived by hand: −3, ½, −2 and 4.
    private static readonly long[] QuaternionPresentationSentinels = [-196608L, 32768L, -131072L, 262144L];
    private static readonly int[] QuaternionPresentationSentinelBits = [
        unchecked((int)0xC0400000), 0x3F000000, unchecked((int)0xC0000000), 0x40800000,
    ];

    /// <summary>Proves <see cref="FixedQuaternion.FromTo"/>'s DEFINING property on the swept stream — the rotor really
    /// takes the start direction onto the end direction — that the result is a unit quaternion, that all three
    /// documented poles hold, that all three arms of the antiparallel fallback are reached, and that a common
    /// power-of-two rescaling leaves the answer bit-identical.</summary>
    /// <param name="left">The start direction's lanes in its first three positions, raw.</param>
    /// <param name="right">The end direction's lanes in its first three positions, raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionFromToShortestArc(long[] left, long[] right) {
        var from = new FixedVector3(
            X: Raw(value: left[0]),
            Y: Raw(value: left[1]),
            Z: Raw(value: left[2])
        );
        var to = new FixedVector3(
            X: Raw(value: right[0]),
            Y: Raw(value: right[1]),
            Z: Raw(value: right[2])
        );
        var fromDirection = from.Normalize();
        var toDirection = to.Normalize();
        var rotation = FixedQuaternion.FromTo(
            from: from,
            to: to
        );

        if (
            (fromDirection == FixedVector3.Zero) ||
            (toDirection == FixedVector3.Zero)
        ) {
            if (rotation != FixedQuaternion.Identity) { return "a zero operand did not return the identity"; }
        } else {
            if (AlignmentFailure(
                fromDirection: fromDirection,
                rotation: rotation,
                toDirection: toDirection
            ) is { } misaligned) { return misaligned; }

            var squares = ((((((BigInteger)rotation.X.Value) * rotation.X.Value) + (((BigInteger)rotation.Y.Value) * rotation.Y.Value)) +
                           (((BigInteger)rotation.Z.Value) * rotation.Z.Value)) + (((BigInteger)rotation.W.Value) * rotation.W.Value));

            if (BigInteger.Abs(value: (Oracles.NearestIntegerRoot(value: squares) - OneRaw)) > QuaternionUnitTolerance) { return "the rotor is not a unit quaternion"; }
            if (FixedQuaternion.FromTo(
                from: from,
                to: from
            ) != FixedQuaternion.Identity) { return "a start direction taken to itself did not return the identity exactly"; }
        }

        if (FixedQuaternion.FromTo(
            from: default,
            to: to
        ) != FixedQuaternion.Identity) { return "a zero start direction did not return the identity"; }
        if (FixedQuaternion.FromTo(
            from: from,
            to: default
        ) != FixedQuaternion.Identity) { return "a zero end direction did not return the identity"; }

        // All three arms of the least-aligned-axis choice, reached rather than believed reachable.
        foreach (var axis in QuaternionAntiparallelLadder) {
            var start = new FixedVector3(
                X: Raw(value: axis[0]),
                Y: Raw(value: axis[1]),
                Z: Raw(value: axis[2])
            );
            var reversed = new FixedVector3(
                X: -start.X,
                Y: -start.Y,
                Z: -start.Z
            );
            var half = FixedQuaternion.FromTo(
                from: start,
                to: reversed
            );

            if (half.W != FixedQ4816.Zero) { return $"the antiparallel witness [{axis[0]},{axis[1]},{axis[2]}] returned a non-zero scalar lane"; }
            if (AlignmentFailure(
                rotation: half,
                fromDirection: start.Normalize(),
                toDirection: reversed.Normalize()
            ) is { } misaligned) { return misaligned; }
        }

        if (
            WithinScaleGuard(
            bound: (1L << 40),
            values: left
        ) &&
            WithinScaleGuard(
            bound: (1L << 40),
            values: right
        )
        ) {
            foreach (var (leftShift, rightShift) in ScaleFreedomShifts) {
                var scaled = FixedQuaternion.FromTo(
                    from: new(
                        X: Raw(value: (left[0] << leftShift)),
                        Y: Raw(value: (left[1] << leftShift)),
                        Z: Raw(value: (left[2] << leftShift))
                    ),
                    to: new(
                        X: Raw(value: (right[0] << rightShift)),
                        Y: Raw(value: (right[1] << rightShift)),
                        Z: Raw(value: (right[2] << rightShift))
                    )
                );

                if (scaled != rotation) { return $"scaling by 2^{leftShift} and 2^{rightShift} moved the rotor"; }
            }
        }

        return null;
    }

    // The rotor takes the start direction onto the end direction, decided by ONE exact integer inequality on the cross
    // product and one on the dot: no angle is formed and no root is taken. The bound is 1024·2¹⁶, roughly one degree —
    // the documented antiparallel fallback's own ~0.45° slack plus the Q16 quantization budget.
    private static string? AlignmentFailure(FixedQuaternion rotation, FixedVector3 fromDirection, FixedVector3 toDirection) {
        var image = rotation.Rotate(vector: fromDirection);

        var (ix, iy, iz) = (((BigInteger)image.X.Value), ((BigInteger)image.Y.Value), ((BigInteger)image.Z.Value));
        var (tx, ty, tz) = (((BigInteger)toDirection.X.Value), ((BigInteger)toDirection.Y.Value), ((BigInteger)toDirection.Z.Value));
        var cross = BigInteger.Max(
            left: BigInteger.Abs(value: ((iy * tz) - (iz * ty))),
            right: BigInteger.Max(
                left: BigInteger.Abs(value: ((iz * tx) - (ix * tz))),
                right: BigInteger.Abs(value: ((ix * ty) - (iy * tx)))
            )
        );

        if (cross > QuaternionAlignmentBound) { return $"the rotated start direction is off the end direction: the largest exact cross component is {cross}"; }
        if ((((ix * tx) + (iy * ty)) + (iz * tz)).Sign < 0) { return "the rotated start direction points away from the end direction"; }

        return null;
    }

    /// <summary>Proves the great-circle interpolation's endpoints, its arc shape against a hand-derived ladder, its
    /// monotone traversal, that BOTH branches are reached, and that the shortest-arc flip names one rotation.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuaternionSlerpEndpointsAndArc() {
        var quarter = new FixedQuaternion(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: Raw(value: 46341L),
            W: Raw(value: 46341L)
        );
        var nearby = new FixedQuaternion(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: Raw(value: 256L),
            W: Raw(value: 65535L)
        );

        if (FixedQuaternion.Dot(
            left: FixedQuaternion.Identity,
            right: quarter
        ).Value > QuaternionNlerpThreshold) { return "the quarter-turn pair does not reach the sine-ratio branch"; }
        if (FixedQuaternion.Dot(
            left: FixedQuaternion.Identity,
            right: nearby
        ).Value <= QuaternionNlerpThreshold) { return "the nearly-parallel pair does not reach the normalized linear blend"; }
        if (FixedQuaternion.Dot(
            left: FixedQuaternion.Identity,
            right: -quarter
        ).Value >= FixedQ4816.Zero.Value) { return "the flipped pair does not reach the shortest-arc flip"; }
        if (FixedQuaternion.Slerp(
            from: FixedQuaternion.Identity,
            to: quarter,
            amount: FixedQ4816.Zero
        ) != FixedQuaternion.Identity) { return "the interpolation at zero is not the normalized start"; }

        var previousToStart = long.MaxValue;
        var previousToEnd = long.MinValue;

        foreach (var (amountRaw, expectedZ, expectedW) in QuaternionSlerpLadder) {
            var value = FixedQuaternion.Slerp(
                from: FixedQuaternion.Identity,
                to: quarter,
                amount: Raw(value: amountRaw)
            );
            var flipped = FixedQuaternion.Slerp(
                from: FixedQuaternion.Identity,
                to: -quarter,
                amount: Raw(value: amountRaw)
            );

            if (
                (value.X.Value != 0L) ||
                (value.Y.Value != 0L)
            ) { return $"the interpolation at {amountRaw} left the rotation plane"; }
            if (Math.Abs(value: (value.Z.Value - expectedZ)) > QuaternionSlerpTolerance) { return $"the Z lane at {amountRaw} is {value.Z.Value}, expected {expectedZ}"; }
            if (Math.Abs(value: (value.W.Value - expectedW)) > QuaternionSlerpTolerance) { return $"the W lane at {amountRaw} is {value.W.Value}, expected {expectedW}"; }
            if (Math.Abs(value: (flipped.Z.Value - expectedZ)) > QuaternionSlerpTolerance) { return $"the flipped Z lane at {amountRaw} is {flipped.Z.Value}, expected {expectedZ}"; }
            if (Math.Abs(value: (flipped.W.Value - expectedW)) > QuaternionSlerpTolerance) { return $"the flipped W lane at {amountRaw} is {flipped.W.Value}, expected {expectedW}"; }

            var toStart = FixedQuaternion.Dot(
                left: value,
                right: FixedQuaternion.Identity
            ).Value;
            var toEnd = FixedQuaternion.Dot(
                left: value,
                right: quarter
            ).Value;

            if (toStart > previousToStart) { return $"the dot against the start rose at {amountRaw}"; }
            if (toEnd < previousToEnd) { return $"the dot against the end fell at {amountRaw}"; }

            previousToStart = toStart;
            previousToEnd = toEnd;
        }

        var endpoint = FixedQuaternion.Slerp(
            from: FixedQuaternion.Identity,
            to: quarter,
            amount: FixedQ4816.One
        );
        var normalized = quarter.Normalize();

        if (Math.Abs(value: (endpoint.Z.Value - normalized.Z.Value)) > QuaternionSlerpEndpointTolerance) { return $"the interpolation at one is {endpoint.Z.Value} on Z, not the normalized end {normalized.Z.Value}"; }
        if (Math.Abs(value: (endpoint.W.Value - normalized.W.Value)) > QuaternionSlerpEndpointTolerance) { return $"the interpolation at one is {endpoint.W.Value} on W, not the normalized end {normalized.W.Value}"; }

        var blended = FixedQuaternion.Slerp(
            from: FixedQuaternion.Identity,
            to: nearby,
            amount: FixedQ4816.One
        );
        var nearbyNormalized = nearby.Normalize();

        if (Math.Abs(value: (blended.Z.Value - nearbyNormalized.Z.Value)) > QuaternionSlerpEndpointTolerance) { return "the normalized linear blend does not reach its own endpoint"; }

        return null;
    }

    // The rotate ladder, hand-derived from the rotation each rotor denotes in real three-space: the quarter turn about
    // z sending x̂ to ŷ and ŷ to −x̂, the half turn about z, and the half turn about (1,1,1)/√3 sending x̂ to (−⅓, ⅔, ⅔).
    private static readonly (long[] Rotor, long[] Vector, long[] Image)[] QuaternionRotateLadder = [
        ([0L, 0L, 46341L, 46341L], [65536L, 0L, 0L], [0L, 65536L, 0L]),
        ([0L, 0L, 46341L, 46341L], [0L, 65536L, 0L], [-65536L, 0L, 0L]),
        ([0L, 0L, 65536L, 0L], [65536L, 0L, 0L], [-65536L, 0L, 0L]),
        ([37837L, 37837L, 37837L, 0L], [65536L, 0L, 0L], [-21845L, 43691L, 43691L]),
    ];

    // Two fused stages, four roundings, doubled by the final shift: two raws by hand at the quarter-turn row, and eight
    // is that with room.
    private const long QuaternionRotateTolerance = 8L;
    private const long QuaternionRoundTripTolerance = 16L;

    // T4 — the axis-angle ladder. Hand-derived from the real sine and cosine of the ROUNDED HALF-ANGLE RAW the kernel
    // forms: all three basis axes, the quarter/half/third/two-thirds turns, both signs, and one tilted axis (1,1,1)/√3.
    // Tolerance two for the basis rows, where the unit axis multiplies through exactly, and four for the tilted row,
    // which carries one extra product rounding per lane.
    private static readonly (long[] Axis, long AngleRaw, long[] Expected, long Tolerance)[] QuaternionAxisAngleLadder = [
        ([0L, 0L, 65536L], 102944L, [0L, 0L, 46341L, 46341L], 2L),
        ([0L, 0L, 65536L], 205887L, [0L, 0L, 65536L, 0L], 2L),
        ([0L, 0L, 65536L], 68628L, [0L, 0L, 32768L, 56756L], 2L),
        ([0L, 0L, 65536L], 137257L, [0L, 0L, 56755L, 32769L], 2L),
        ([0L, 0L, 65536L], -102944L, [0L, 0L, -46341L, 46341L], 2L),
        ([65536L, 0L, 0L], 51472L, [25080L, 0L, 0L, 60547L], 2L),
        ([0L, 65536L, 0L], 102944L, [0L, 46341L, 0L, 46341L], 2L),
        ([37837L, 37837L, 37837L], 102944L, [26755L, 26755L, 26755L, 46341L], 4L),
        ([0L, 0L, 65536L], 0L, [0L, 0L, 0L, 65536L], 0L),
    ];
    // T5 — the exponential ladder. Hand-derived from the closed form (b̂·sin‖b‖, cos‖b‖) over the reals, evaluated at
    // the value each BIVECTOR RAW denotes; the tolerance is the axis normalization's one raw plus the sine's 0.51 ULP
    // plus the product's half, rounded up.
    private static readonly (long[] Bivector, long[] Expected)[] QuaternionExpLadder = [
        ([0L, 0L, 0L], [0L, 0L, 0L, 65536L]),
        ([0L, 0L, 51472L], [0L, 0L, 46341L, 46341L]),
        ([0L, 51472L, 0L], [0L, 46341L, 0L, 46341L]),
        ([51472L, 0L, 0L], [46341L, 0L, 0L, 46341L]),
        ([0L, 0L, -51472L], [0L, 0L, -46341L, 46341L]),
        ([0L, 0L, 102944L], [0L, 0L, 65536L, 0L]),
        ([0L, 0L, 205887L], [0L, 0L, 0L, -65536L]),
        ([29717L, 29717L, 29717L], [26755L, 26755L, 26755L, 46341L]),
    ];

    private const long QuaternionExpTolerance = 4L;
    private const long QuaternionLogTolerance = 8L;
    private const long QuaternionExpLogRoundTripTolerance = 40L;
    private const long QuaternionUnitTolerance = 8L;

    // T6 — the logarithm ladder. Hand-derived from the closed form v̂·atan2(‖v‖, w) over the reals. Every row keeps the
    // vector part at or above 4096 raw, the floor below which the atan2-over-norm scale division stops carrying
    // information.
    private static readonly (long[] Quaternion, long[] Expected)[] QuaternionLogLadder = [
        ([0L, 0L, 0L, 65536L], [0L, 0L, 0L]),
        ([0L, 0L, 0L, -65536L], [0L, 0L, 0L]),
        ([0L, 0L, 46341L, 46341L], [0L, 0L, 51472L]),
        ([0L, 0L, 65536L, 0L], [0L, 0L, 102944L]),
        ([0L, 0L, 32768L, 56756L], [0L, 0L, 34314L]),
        ([0L, 0L, -46341L, 46341L], [0L, 0L, -51472L]),
        ([21845L, 43691L, 43691L, 0L], [34314L, 68629L, 68629L]),
    ];
    // The three antiparallel witnesses, one per arm of the least-aligned-axis choice: |x| smallest, |y| smallest, and
    // |z| smallest with |x| the largest.
    private static readonly long[][] QuaternionAntiparallelLadder = [
        [0L, 65536L, 0L],
        [65536L, 0L, 0L],
        [196608L, 131072L, 65536L],
    ];
    // Roughly one degree of angular slack at Q16: the documented antiparallel fallback's own ~0.45° plus the
    // quantization budget, expressed as an exact integer bound on the cross product of two unit-length raws.
    private static readonly BigInteger QuaternionAlignmentBound = (new BigInteger(value: 1024L) << 16);
    // T7 — the interpolation ladder. Hand-derived from the real great-circle interpolation, the quaternion at half-angle
    // t·π/4. The tolerance is honest and large: Slerp chains a squared dot, a square root, an arctangent, a sine/cosine
    // pair and a division before renormalizing, and each of the five rounds.
    private static readonly (long AmountRaw, long ExpectedZ, long ExpectedW)[] QuaternionSlerpLadder = [
        (0L, 0L, 65536L),
        (16384L, 12785L, 64277L),
        (32768L, 25080L, 60547L),
        (49152L, 36410L, 54491L),
        (65536L, 46341L, 46341L),
    ];

    private const long QuaternionSlerpTolerance = 64L;
    private const long QuaternionSlerpEndpointTolerance = 4L;
    // The cosine above which the interpolation falls back to a normalized linear blend, as the source declares it.
    private const long QuaternionNlerpThreshold = 65503L;

}
