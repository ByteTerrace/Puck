using System.Numerics;
using LeafOctonion = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- carrier scalars (UnitFraction16, UnitFraction32), the half-open unit fractions ----

    /// <summary>Proves the seam between the half-open fraction and the closed interval FROM THE FRACTION SIDE, which is
    /// the side the interval's own remarks describe in prose: the two share the grid and the fraction stops exactly one
    /// unit short of the interval's one; the bitwise complement and the interval's ARITHMETIC complement differ by exactly
    /// that one unit; the fraction's sum WRAPS where the interval's saturating sum CLAMPS, and the two agree exactly while
    /// the exact sum stays below one; and the fraction's own saturating sum stops one unit lower still. The embedding's
    /// exactness and its refusal at one are pinned from the interval side by closed-unit.kinship-exact and are NOT
    /// restated here.</summary>
    /// <param name="left">The first sampled operand lane.</param>
    /// <param name="right">The second sampled operand lane.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction32KinshipExact(long[] left, long[] right) {
        var rawA = ((uint)UnitFraction32Width.Fold(sampled: left[0]));
        var rawB = ((uint)UnitFraction32Width.Fold(sampled: right[0]));
        var a = UnitFraction32.FromRawBits(value: rawA);
        var b = UnitFraction32.FromRawBits(value: rawB);
        var embeddedA = UnitInterval32.FromUnitFraction32(value: a);
        var embeddedB = UnitInterval32.FromUnitFraction32(value: b);
        var exactSum = (new BigInteger(value: rawA) + rawB);
        // Read into a local so the shared-grid statement is a comparison the run makes rather than one the compiler folds
        // away — both counts are declared constants, and a folded comparison would make the counterexample unreachable.
        var bits = UnitFraction32.FractionBitCount;
        var one = (BigInteger.One << bits);

        // One fact, two types: the same grid, and the fraction stops exactly one unit short of the interval's one.
        if (bits != UnitInterval32.FractionBitCount) { return "the two types do not share the grid"; }
        if (UnitInterval32.One.Value != (((ulong)UnitFraction32.MaxValue.Value) + 1UL)) { return "the fraction's top is not one unit below the interval's one"; }

        // The order is preserved across the embedding, at every relation the two types both offer.
        if ((a < b) != (embeddedA < embeddedB)) { return $"the embedding does not preserve less-than at ({rawA}, {rawB})"; }
        if ((a <= b) != (embeddedA <= embeddedB)) { return $"the embedding does not preserve less-or-equal at ({rawA}, {rawB})"; }
        if ((a > b) != (embeddedA > embeddedB)) { return $"the embedding does not preserve greater-than at ({rawA}, {rawB})"; }
        if ((a >= b) != (embeddedA >= embeddedB)) { return $"the embedding does not preserve greater-or-equal at ({rawA}, {rawB})"; }
        if (Math.Sign(value: a.CompareTo(other: b)) != Math.Sign(value: embeddedA.CompareTo(other: embeddedB))) { return $"the embedding does not preserve the comparison at ({rawA}, {rawB})"; }

        // The one-unit offset the interval's remark states in prose: bitwise complement versus arithmetic complement.
        if (UnitInterval32.Complement(value: embeddedA).Value != (((ulong)(~a).Value) + 1UL)) { return $"the two complements are not one unit apart at {rawA}"; }

        // Wrap versus clamp, and the exact condition under which they agree.
        if ((a + b).Value != UnitFractionClaims<UnitFraction32Width, UnitFraction32>.Wrap(value: exactSum)) { return $"the fraction's sum does not wrap at ({rawA}, {rawB})"; }
        if (UnitInterval32.AddSaturating(
            x: embeddedA,
            y: embeddedB
        ).Value != BigInteger.Min(
            left: exactSum,
            right: one
        )) { return $"the interval's sum does not clamp at ({rawA}, {rawB})"; }
        if ((((ulong)(a + b).Value) == UnitInterval32.AddSaturating(
            x: embeddedA,
            y: embeddedB
        ).Value) != (exactSum < one)) { return $"the wrap and the clamp agree outside the sub-one regime at ({rawA}, {rawB})"; }
        if (UnitFraction32.AddSaturating(
            x: a,
            y: b
        ).Value != BigInteger.Min(
            left: exactSum,
            right: (one - BigInteger.One)
        )) { return $"the fraction's saturating sum does not stop one unit lower at ({rawA}, {rawB})"; }

        return null;
    }
    /// <summary>Proves the UQ0.16 surface EXHAUSTIVELY, which the width makes possible: every one of the 65 536 raws is
    /// rendered, parsed back, projected to double and complemented, and every raw is multiplied and divided against a
    /// committed band of divisors. At this width the sampled laws' envelope disappears — there is no operand the claim
    /// does not reach on one side of every binary statement.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitFraction16Exhaustive() {
        const int Bits = UnitFraction16.FractionBitCount;

        for (var raw = 0; (raw <= 65535); ++raw) {
            var value = UnitFraction16.FromRawBits(value: ((ushort)raw));
            var exact = new BigInteger(value: raw);
            var reference = Oracles.ExactDyadicDecimal(
                numerator: exact,
                shift: Bits
            );

            if (value.ToString() != reference) { return $"raw {raw} rendered as '{value.ToString()}'"; }
            if (UnitFraction16.Parse(
                provider: null,
                s: reference
            ) != value) { return $"raw {raw} did not parse back from '{reference}'"; }
            if (BitConverter.DoubleToUInt64Bits(value: ((double)value)) != Oracles.ExactBinary64Bits(
                numerator: exact,
                shift: Bits
            )) { return $"the projection of raw {raw} is wrong"; }
            if (UnitFraction16.FromDouble(value: ((double)value)) != value) { return $"the double round trip failed at raw {raw}"; }
            if ((~value).Value != (65535 - raw)) { return $"the complement of raw {raw} is wrong"; }
            if (-(-value) != value) { return $"the negation is not an involution at raw {raw}"; }

            foreach (var divisor in UnitFraction16Band) {
                var product = ((long)(value * UnitFraction16.FromRawBits(value: divisor)).Value);
                var quotient = ((long)(value / UnitFraction16.FromRawBits(value: divisor)).Value);

                if (product != ((long)Oracles.UnitFractionProduct(
                    fractionBitCount: Bits,
                    x: ((ulong)raw),
                    y: divisor
                ))) { return $"the product of {raw} and {divisor} is wrong"; }
                if (quotient != ((long)Oracles.UnitFractionQuotient(
                    fractionBitCount: Bits,
                    x: ((ulong)raw),
                    y: divisor
                ))) { return $"the quotient of {raw} by {divisor} is wrong"; }
            }
        }

        return null;
    }

    // The divisor band the exhaustive sweep runs every raw against: the smallest units, the odd divisors whose remainders
    // never vanish, the byte seam, the exact half either side, and the top either side. Twelve values, so the sweep is
    // 65 536 × 12 pairs on each of the two rounding kernels.
    private static readonly ushort[] UnitFraction16Band = [
        1, 2, 3, 5, 255, 256, 257, 32767, 32768, 32769, 65534, 65535,
    ];

    // ---- integer floored division (BinaryIntegerFunctions, over the raw carrier) ----

    /// <summary>Maps a sampled divisor onto one the operation defines: zero divides by nothing, and the signed minimum
    /// over minus one has no representable quotient. Both are substituted identically in subject and oracle, so every
    /// sampled pair reaches a defined comparison rather than being skipped asymmetrically. The two excluded pairs are
    /// the documented throw sites and belong to a probe, not to a value law.</summary>
    private static long Divisor(long a, long b) =>
        (((0L == b) || ((long.MinValue == a) && (-1L == b)))
            ? 1L
            : b
        );

    /// <summary>Proves the two operand pairs the value laws substitute are REFUSED rather than answered wrongly:
    /// division by zero and the signed minimum over minus one, at all three floored members.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>Without this the block's envelope reads "every operand pair except the two substituted ones", since
    /// <see cref="Divisor"/> maps both away and nothing else in the suite or the tools reaches them (worklist O1).</remarks>
    public static string? IntegerDivisionLimitsRefuse() {
        foreach (var (name, divide) in (((string Name, Action<long, long> Divide)[])[
            ("FloorDivide", static (a, b) => _ = a.FloorDivide(divisor: b)),
            ("CeilingDivide", static (a, b) => _ = a.CeilingDivide(divisor: b)),
            ("FloorDivRem", static (a, b) => _ = a.FloorDivRem(divisor: b)),
        ])) {
            if (!Throws<DivideByZeroException>(action: () => divide(
                7L,
                0L
            ))) {
                return $"{name} answered a division by zero instead of refusing it";
            }

            if (!Throws<OverflowException>(action: () => divide(
                long.MinValue,
                -1L
            ))) {
                return $"{name} answered the signed minimum over minus one, whose quotient is unrepresentable, instead of refusing it";
            }
        }

        return null;
    }
    /// <summary>The subject floored quotient.</summary>
    public static long FloorDivide(long a, long b) =>
        a.FloorDivide(divisor: Divisor(
            a: a,
            b: b
        ));
    /// <summary>The subject ceiling quotient.</summary>
    public static long CeilingDivide(long a, long b) =>
        a.CeilingDivide(divisor: Divisor(
            a: a,
            b: b
        ));
    /// <summary>The subject floored quotient read from the quotient-and-remainder pair.</summary>
    public static long FloorDivRemQuotient(long a, long b) =>
        a.FloorDivRem(divisor: Divisor(
            a: a,
            b: b
        )).Quotient;
    /// <summary>The subject floored remainder read from the quotient-and-remainder pair.</summary>
    public static long FloorDivRemRemainder(long a, long b) =>
        a.FloorDivRem(divisor: Divisor(
            a: a,
            b: b
        )).Remainder;
    /// <summary>The exact floored quotient, taken in arbitrary width so no carrier edge is a special case.</summary>
    public static long FloorDivideOracle(long a, long b) =>
        ((long)Oracles.FloorQuotient(
            numerator: a,
            denominator: Divisor(
                a: a,
                b: b
            )
        ));
    /// <summary>The exact ceiling quotient — the floored quotient, raised by one exactly when the division is inexact.</summary>
    public static long CeilingDivideOracle(long a, long b) {
        var divisor = Divisor(
            a: a,
            b: b
        );
        var quotient = Oracles.FloorQuotient(
            denominator: divisor,
            numerator: a
        );

        return ((long)(((quotient * divisor) == a)
            ? quotient
            : (quotient + System.Numerics.BigInteger.One)));
    }
    /// <summary>The exact floored remainder — what the value less the floored product leaves.</summary>
    public static long FloorDivRemRemainderOracle(long a, long b) {
        var divisor = Divisor(
            a: a,
            b: b
        );

        return ((long)(a - (Oracles.FloorQuotient(
            denominator: divisor,
            numerator: a
        ) * divisor)));
    }
    // ---- FixedComplex (the (0, −1) relation) ----

    /// <summary>The subject <see cref="FixedComplex"/> multiply.</summary>
    public static (long U, long V) ComplexMultiply(long u1, long v1, long u2, long v2) {
        var product = (new FixedComplex(
            Real: Raw(value: u1),
            Imaginary: Raw(value: v1)
        ) * new FixedComplex(
            Real: Raw(value: u2),
            Imaginary: Raw(value: v2)
        ));

        return (product.Real.Value, product.Imaginary.Value);
    }
    /// <summary>The subject <see cref="FixedComplex"/> conjugate.</summary>
    public static (long U, long V) ComplexConjugate(long u, long v) {
        var conjugate = new FixedComplex(
            Real: Raw(value: u),
            Imaginary: Raw(value: v)
        ).Conjugate();

        return (conjugate.Real.Value, conjugate.Imaginary.Value);
    }
    /// <summary>The subject <see cref="FixedComplex"/> negation.</summary>
    public static (long U, long V) ComplexNegate(long u, long v) {
        var negated = -new FixedComplex(
            Real: Raw(value: u),
            Imaginary: Raw(value: v)
        );

        return (negated.Real.Value, negated.Imaginary.Value);
    }
    // ---- FixedSplit (the (0, +1) relation) ----

    /// <summary>The subject <see cref="FixedComplex"/> multiply as a two-lane vector operation.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void ComplexMultiplyLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var (u, v) = ComplexMultiply(
            u1: left[0],
            v1: left[1],
            u2: right[0],
            v2: right[1]
        );

        result[0] = u;
        result[1] = v;
    }
    /// <summary>The subject <see cref="FixedSplit"/> multiply.</summary>
    public static (long U, long V) SplitMultiply(long u1, long v1, long u2, long v2) {
        var product = (new FixedSplit(
            U: Raw(value: u1),
            V: Raw(value: v1)
        ) * new FixedSplit(
            U: Raw(value: u2),
            V: Raw(value: v2)
        ));

        return (product.U.Value, product.V.Value);
    }
    /// <summary>The subject <see cref="FixedSplit"/> norm.</summary>
    public static long SplitNorm(long u, long v) =>
        new FixedSplit(
            U: Raw(value: u),
            V: Raw(value: v)
        ).Norm.Value;
    /// <summary>The subject <see cref="FixedSplit"/> conjugate.</summary>
    public static (long U, long V) SplitConjugate(long u, long v) {
        var conjugate = new FixedSplit(
            U: Raw(value: u),
            V: Raw(value: v)
        ).Conjugate();

        return (conjugate.U.Value, conjugate.V.Value);
    }
    // ---- FixedDual<FixedQ4816> (the (0, 0) relation) ----

    /// <summary>The subject <see cref="FixedDual{TValue}"/> multiply over <see cref="FixedQ4816"/>.</summary>
    public static (long U, long V) DualMultiply(long u1, long v1, long u2, long v2) {
        var product = (new FixedDual<FixedQ4816>(
            Real: Raw(value: u1),
            Dual: Raw(value: v1)
        ) * new FixedDual<FixedQ4816>(
            Real: Raw(value: u2),
            Dual: Raw(value: v2)
        ));

        return (product.Real.Value, product.Dual.Value);
    }
    // ---- QuadraticAlgebra<FixedQ4816> lanes ----

    /// <summary>The subject <see cref="QuadraticAlgebra{TScalar}"/> multiply for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static BinaryElemOp AlgebraMultiply(long pRaw, long qRaw) {
        var algebra = QuadraticAlgebra<FixedQ4816>.Create(
            p: Raw(value: pRaw),
            q: Raw(value: qRaw)
        );

        return (u1, v1, u2, v2) => {
            var product = algebra.Multiply(
                left: new(
                    U: Raw(value: u1),
                    V: Raw(value: v1)
                ),
                right: new(
                    U: Raw(value: u2),
                    V: Raw(value: v2)
                )
            );

            return (product.U.Value, product.V.Value);
        };
    }
    /// <summary>The subject <see cref="QuadraticAlgebra{TScalar}"/> norm for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static ScalarElemOp AlgebraNorm(long pRaw, long qRaw) {
        var algebra = QuadraticAlgebra<FixedQ4816>.Create(
            p: Raw(value: pRaw),
            q: Raw(value: qRaw)
        );

        return (u, v) => algebra.Norm(value: new(
            U: Raw(value: u),
            V: Raw(value: v)
        )).Value;
    }
    /// <summary>The subject <see cref="QuadraticAlgebra{TScalar}"/> Möbius step for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static UnaryElemOp AlgebraMobius(long pRaw, long qRaw) {
        var algebra = QuadraticAlgebra<FixedQ4816>.Create(
            p: Raw(value: pRaw),
            q: Raw(value: qRaw)
        );

        return (n, d) => {
            var step = algebra.MobiusStep(pair: new(
                Numerator: Raw(value: n),
                Denominator: Raw(value: d)
            ));

            return (step.Numerator.Value, step.Denominator.Value);
        };
    }
    // ---- oracle closures for the planar relations ----

    /// <summary>The oracle multiply for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static BinaryElemOp MultiplyOracle(long pRaw, long qRaw) =>
        (u1, v1, u2, v2) => Oracles.QuadraticMultiply(
            pRaw: pRaw,
            qRaw: qRaw,
            u1: u1,
            u2: u2,
            v1: v1,
            v2: v2
        );
    /// <summary>The oracle norm for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static ScalarElemOp NormOracle(long pRaw, long qRaw) =>
        (u, v) => Oracles.QuadraticNorm(
            pRaw: pRaw,
            qRaw: qRaw,
            u: u,
            v: v
        );
    /// <summary>The oracle Möbius numerator for the relation <c>(pRaw, qRaw)</c>.</summary>
    public static ScalarBinaryOp MobiusNumeratorOracle(long pRaw, long qRaw) =>
        (n, d) => Oracles.MobiusNumerator(
            d: d,
            n: n,
            pRaw: pRaw,
            qRaw: qRaw
        );
    /// <summary>The oracle multiply for the relation <c>(pRaw, qRaw)</c> in the two-lane shape the presented quadratic
    /// twins take — the THIRD LEG for every fixed-point twin whose two sides both round through
    /// <c>FixedQ4816.RoundProductSum</c> or <c>FusedArithmetic.RoundQ48SumToRaw</c>.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <returns>The bound oracle.</returns>
    public static VectorBinaryOp QuadraticMultiplyLanesOracle(long pRaw, long qRaw) =>
        (left, right, result) => {
            var product = Oracles.QuadraticMultiply(
                pRaw: pRaw,
                qRaw: qRaw,
                u1: left[0],
                v1: left[1],
                u2: right[0],
                v2: right[1]
            );

            result[0] = product.U;
            result[1] = product.V;
        };
    /// <summary>The oracle power of the adjoined root of <c>x² = P·x + Q</c>, by the pinned ascending-bit schedule with
    /// every step's arithmetic re-derived in <see cref="BigInteger"/>.</summary>
    /// <param name="p">The linear coefficient, raw Q16.</param>
    /// <param name="q">The constant coefficient, raw Q16.</param>
    /// <param name="exponent">The power.</param>
    /// <returns>The power's components as raws.</returns>
    public static (long U, long V) CompanionRootPowerOracle(long p, long q, ulong exponent) =>
        Oracles.CompanionRootPower(
            exponent: exponent,
            pRaw: p,
            qRaw: q
        );
    /// <summary>The oracle dual part of a <c>(0, 0)</c> product — ONE ties-to-even rounding of the exact
    /// <c>a·d + b·c</c> at shift sixteen — in the shape both the jet residual and <see cref="FixedDual{TScalar}"/>
    /// return.</summary>
    /// <param name="u1">The multiplicand's real part, raw.</param>
    /// <param name="v1">The multiplicand's dual part, raw.</param>
    /// <param name="u2">The multiplier's real part, raw.</param>
    /// <param name="v2">The multiplier's dual part, raw.</param>
    /// <returns>The residual's components as raws; the second is identically zero, as both subjects return.</returns>
    public static (long U, long V) JetResidualOracle(long u1, long v1, long u2, long v2) =>
        (Oracles.RoundDyadic(
            exact: ((((BigInteger)u1) * v2) + (((BigInteger)v1) * u2)),
            shift: 16
        ), 0L);
    // ---- PresentedAlgebra: the derived multi-lane products ----
    //
    // Every binding below is built LAZILY, on the delegate's first call, so a filtered run pays only for the
    // presentations its own tier actually drives. Each closure owns its algebra outright; the kernel's working buffers
    // are per-instance mutable state, so nothing here is shared between cases.

    /// <summary>The subject product of the presented Clifford signature <c>(p, q, r)</c> over the house scalar, with
    /// lanes indexed by BLADE BITMASK so the vector agrees with <see cref="Multivector"/> lane for lane.</summary>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of degenerate generators.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedCliffordMultiply(int positiveCount, int negativeCount, int degenerateCount) {
        FixedLaneAlgebra? binding = null;

        return (left, right, result) => {
            binding ??= CliffordBinding(
                degenerateCount: degenerateCount,
                negativeCount: negativeCount,
                positiveCount: positiveCount
            );

            binding.Multiply(
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The subject <see cref="GeometricAlgebra"/> product of the signature <c>(p, q, r)</c>.</summary>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of degenerate generators.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp GeometricMultiply(int positiveCount, int negativeCount, int degenerateCount) {
        var algebra = GeometricAlgebra.Create(
            degenerateCount: degenerateCount,
            negativeCount: negativeCount,
            positiveCount: positiveCount
        );

        return (left, right, result) => {
            var a = new Multivector();
            var b = new Multivector();

            for (var lane = 0; (lane < left.Length); ++lane) {
                a[lane] = Raw(value: left[lane]);
                b[lane] = Raw(value: right[lane]);
            }

            var product = algebra.GeometricProduct(
                left: a,
                right: b
            );

            for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = product[lane].Value; }
        };
    }
    /// <summary>The shared-nothing twisted-group oracle at a Clifford signature: one rounding per blade of the whole
    /// charged sum, with the charges from <see cref="Oracles.CliffordCharge"/>.</summary>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of degenerate generators.</param>
    /// <returns>The bound oracle.</returns>
    public static VectorBinaryOp CliffordProductOracle(int positiveCount, int negativeCount, int degenerateCount) {
        var charge = CliffordChargeSource(
            degenerateCount: degenerateCount,
            negativeCount: negativeCount,
            positiveCount: positiveCount
        );

        return (left, right, result) => Oracles.TwistedGroupProduct(
            chargeSource: charge,
            left: left,
            result: result,
            right: right,
            shift: 16
        );
    }
    /// <summary>The shared-nothing twisted-group oracle at a Cayley–Dickson floor: one rounding per lane of the whole
    /// charged sum, with the charges from <see cref="Oracles.CayleyDicksonCharge"/>.</summary>
    /// <param name="floors">The number of doublings.</param>
    /// <returns>The bound oracle.</returns>
    /// <remarks>This leg pins the ROUNDING DISCIPLINE, not the sign structure: the charge table it reads is a labelled
    /// faithful-carriage transcription of the presentation's own recursion (<see cref="Oracles.CayleyDicksonCharge"/>'s
    /// remark), so a shared error in the tower's signs would hide. The signs answer to the doubling-algebra comparison in
    /// the same case. What no other statement in the tree covers, and what this one does, is that the presented product
    /// at this floor is ONE ties-to-even rounding of the exact charged sum at full raw range.</remarks>
    public static VectorBinaryOp CayleyDicksonProductOracle(int floors) {
        var count = (1 << floors);
        var table = new int[(count * count)];

        for (var left = 0; (left < count); ++left) {
            for (var right = 0; (right < count); ++right) {
                table[((left * count) + right)] = Oracles.CayleyDicksonCharge(
                    floors: floors,
                    leftIndex: left,
                    rightIndex: right
                );
            }
        }

        var charge = ((Func<int, int, int>)((first, second) => table[((first * count) + second)]));

        return (left, right, result) => Oracles.TwistedGroupProduct(
            chargeSource: charge,
            left: left,
            result: result,
            right: right,
            shift: 16
        );
    }
    /// <summary>The per-term-rounding sibling of <see cref="CliffordProductOracle"/> — the discipline the fused kernel
    /// is claimed to differ from.</summary>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of degenerate generators.</param>
    /// <returns>The bound oracle.</returns>
    public static VectorBinaryOp CliffordPerProductOracle(int positiveCount, int negativeCount, int degenerateCount) {
        var charge = CliffordChargeSource(
            degenerateCount: degenerateCount,
            negativeCount: negativeCount,
            positiveCount: positiveCount
        );

        return (left, right, result) => Oracles.TwistedGroupPerProduct(
            chargeSource: charge,
            left: left,
            result: result,
            right: right,
            shift: 16
        );
    }
    /// <summary>The subject product of the presented Cayley–Dickson tower over the house scalar; a lane IS a tower
    /// index, which is also the normal-form key.</summary>
    /// <param name="floors">The number of doublings.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedCayleyDicksonMultiply(int floors) {
        FixedLaneAlgebra? binding = null;

        return (left, right, result) => {
            binding ??= CayleyDicksonBinding(floors: floors);

            binding.Multiply(
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The subject <see cref="DoublingAlgebra{TInner}"/> octonion product, lanes in tower-index order.</summary>
    /// <param name="left">The multiplicand's lanes.</param>
    /// <param name="right">The multiplier's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void DoublingOctonionMultiply(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        DoublingTower.WriteOctonionLanes(
            value: LeafOctonion.Multiply(
                left: ReadOctonion(lanes: left),
                right: ReadOctonion(lanes: right)
            ),
            lanes: result
        );
    /// <summary>The subject <see cref="DoublingAlgebra{TInner}"/> octonion associator, lanes in tower-index order.</summary>
    /// <param name="a">The first operand's lanes.</param>
    /// <param name="b">The second operand's lanes.</param>
    /// <param name="c">The third operand's lanes.</param>
    /// <param name="result">The destination lanes.</param>
    public static void DoublingOctonionAssociator(ReadOnlySpan<long> a, ReadOnlySpan<long> b, ReadOnlySpan<long> c, Span<long> result) =>
        DoublingTower.WriteOctonionLanes(
            value: LeafOctonion.Associator(
                left: ReadOctonion(lanes: a),
                middle: ReadOctonion(lanes: b),
                right: ReadOctonion(lanes: c)
            ),
            lanes: result
        );
    /// <summary>The subject associator of the presented Cayley–Dickson tower, formed as
    /// <c>(a·b)·c + −(a·(b·c))</c> through the algebra's own add and the material's negation.</summary>
    /// <param name="floors">The number of doublings.</param>
    /// <returns>The bound operation.</returns>
    public static VectorTernaryOp PresentedCayleyDicksonAssociator(int floors) {
        FixedLaneAlgebra? binding = null;

        return (a, b, c, result) => {
            binding ??= CayleyDicksonBinding(floors: floors);

            binding.Associator(
                a: a,
                b: b,
                c: c,
                result: result
            );
        };
    }
    /// <summary>The subject product of the presented monogenic algebra <c>xⁿ ≡ tail</c> over <see cref="ParityMaterial"/>
    /// — the binary field of that degree; a lane is a coefficient bit and IS the normal-form key.</summary>
    /// <param name="degree">The extension degree.</param>
    /// <param name="reductionTail">The modulus below its leading term, as a coefficient bitmask.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedBinaryFieldMultiply(int degree, ulong reductionTail) {
        ParityLaneAlgebra? binding = null;

        return (left, right, result) => {
            binding ??= new ParityLaneAlgebra(
                degree: degree,
                reductionTail: reductionTail
            );

            binding.Multiply(
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The shared-nothing <c>GF(2^degree)</c> oracle, by schoolbook carryless multiply and bit-by-bit
    /// reduction.</summary>
    /// <param name="degree">The extension degree.</param>
    /// <param name="reductionTail">The modulus below its leading term.</param>
    /// <returns>The bound oracle.</returns>
    public static VectorBinaryOp BinaryFieldProductOracle(int degree, ulong reductionTail) =>
        (left, right, result) => {
            var product = Oracles.BinaryFieldProduct(
                left: PackBits(lanes: left),
                right: PackBits(lanes: right),
                degree: degree,
                reductionTail: reductionTail
            );

            for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = ((long)((product >> lane) & BigInteger.One)); }
        };
    /// <summary>The subject <see cref="BinaryField{T}"/> product at degree eight.</summary>
    /// <param name="left">The multiplicand's coefficient bits.</param>
    /// <param name="right">The multiplier's coefficient bits.</param>
    /// <param name="result">The destination coefficient bits.</param>
    public static void BinaryFieldMultiply8(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        UnpackBits(
            value: BinaryFields.Degree8.Multiply(
                left: ((byte)PackBits(lanes: left)),
                right: ((byte)PackBits(lanes: right))
            ),
            lanes: result
        );
    /// <summary>The subject <see cref="BinaryField{T}"/> product at degree sixteen.</summary>
    /// <param name="left">The multiplicand's coefficient bits.</param>
    /// <param name="right">The multiplier's coefficient bits.</param>
    /// <param name="result">The destination coefficient bits.</param>
    public static void BinaryFieldMultiply16(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        UnpackBits(
            value: BinaryFields.Degree16.Multiply(
                left: ((ushort)PackBits(lanes: left)),
                right: ((ushort)PackBits(lanes: right))
            ),
            lanes: result
        );
    /// <summary>The subject product of the presented monogenic algebra of the relation <c>x² = P·x + Q</c> over the house
    /// scalar — the derived form of <see cref="QuadraticAlgebra{TScalar}"/>, whose key IS the exponent.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedQuadraticMultiply(long pRaw, long qRaw) {
        FixedLaneAlgebra? binding = null;

        return (left, right, result) => {
            binding ??= QuadraticBinding(
                pRaw: pRaw,
                qRaw: qRaw
            );

            binding.Multiply(
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The subject <see cref="QuadraticAlgebra{TScalar}"/> product as a two-lane vector operation.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp QuadraticMultiplyLanes(long pRaw, long qRaw) {
        var algebra = QuadraticAlgebra<FixedQ4816>.Create(
            p: Raw(value: pRaw),
            q: Raw(value: qRaw)
        );

        return (left, right, result) => {
            var product = algebra.Multiply(
                left: new(
                    U: Raw(value: left[0]),
                    V: Raw(value: left[1])
                ),
                right: new(
                    U: Raw(value: right[0]),
                    V: Raw(value: right[1])
                )
            );

            result[0] = product.U.Value;
            result[1] = product.V.Value;
        };
    }
    /// <summary>The subject presented power of the adjoined root, by the pinned ascending-bit schedule.</summary>
    /// <returns>The bound operation. The presentation is rebuilt only when the relation moves, so a ladder of exponents
    /// over one relation constructs it once.</returns>
    public static PowerOp PresentedRootPower() {
        FixedLaneAlgebra? binding = null;
        var boundP = 0L;
        var boundQ = 0L;

        return (p, q, exponent) => {
            if (
                (binding is null) ||
                (boundP != p) ||
                (boundQ != q)
            ) {
                binding = QuadraticBinding(
                    pRaw: p,
                    qRaw: q
                );
                boundP = p;
                boundQ = q;
            }

            var power = binding.Algebra.Power(
                value: binding.Algebra.Generator(symbol: 0),
                exponent: exponent
            );

            return (power[0L].Value, power[1L].Value);
        };
    }
    /// <summary>The subject <see cref="QuadraticAlgebra{TScalar}.CompanionPower"/>.</summary>
    /// <param name="p">The linear coefficient, raw Q16.</param>
    /// <param name="q">The constant coefficient, raw Q16.</param>
    /// <param name="exponent">The power.</param>
    /// <returns>The result components.</returns>
    public static (long U, long V) CompanionRootPower(long p, long q, ulong exponent) {
        var element = QuadraticAlgebra<FixedQ4816>.Create(
            p: Raw(value: p),
            q: Raw(value: q)
        ).CompanionPower(exponent: exponent);

        return (element.U.Value, element.V.Value);
    }

    // ---- the algebraic path problem: ONE quiver presentation at three materials ----
    //
    // The operand pair encodes a weighted digraph on GraphOrder vertices: lane i·n + j is the arc i → j, present when
    // the right operand's low bit is set, and carrying the left operand's low sixteen raw bits as its weight. A
    // non-negative weight below one keeps every path sum of a four-vertex graph exactly representable, so the tropical
    // statement is about (min, +) and never about wrapping.

    /// <summary>The number of vertices every graph law runs on; the lane count is its square.</summary>
    public const int GraphOrder = 4;

    /// <summary>The subject reflexive-transitive closure: the guarded sum over all lengths of a Boolean quiver
    /// element.</summary>
    /// <returns>The bound operation. The presentation is built on first use and owned by this closure alone, because
    /// the kernel's working buffers are per-instance mutable state.</returns>
    public static VectorBinaryOp PresentedBooleanStar() {
        PresentedAlgebra<bool, BooleanMaterial>? algebra = null;

        return (left, right, result) => {
            algebra ??= PresentedAlgebra<bool, BooleanMaterial>.Create(presentation: CodiscreteQuiver<bool, BooleanMaterial>(
                material: default,
                order: GraphOrder
            ));

            var count = right.Length;
            var coefficients = new bool[count];
            var keys = new long[count];
            var support = 0;

            for (var lane = 0; (lane < count); ++lane) {
                if (0L == (right[lane] & 1L)) { continue; }

                coefficients[support] = true;
                keys[support] = lane;
                ++support;
            }

            var element = algebra.FromSupport(
                keys: keys.AsSpan(
                    length: support,
                    start: 0
                ),
                coefficients: coefficients.AsSpan(
                    length: support,
                    start: 0
                )
            );

            result.Clear();

            if (!algebra.TrySumOverAllLengths(
                obstruction: out _,
                total: out var total,
                value: element
            )) {
                for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = -1L; }

                return;
            }

            for (var index = 0; (index < total.SupportCount); ++index) {
                result[((int)total.Keys[index])] = (total.Coefficients[index]
                    ? 1L
                    : 0L
                );
            }
        };
    }
    /// <summary>The shared-nothing reflexive-transitive closure oracle.</summary>
    /// <param name="left">The weight lanes, unread.</param>
    /// <param name="right">The arc-presence lanes.</param>
    /// <param name="result">The closure lanes.</param>
    public static void BooleanStarOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var count = right.Length;
        var adjacency = new bool[count];
        var closure = new bool[count];

        for (var lane = 0; (lane < count); ++lane) { adjacency[lane] = (0L != (right[lane] & 1L)); }

        Oracles.BooleanTransitiveClosure(
            adjacency: adjacency,
            order: GraphOrder,
            result: closure
        );

        for (var lane = 0; (lane < count); ++lane) {
            result[lane] = (closure[lane]
                ? 1L
                : 0L
            );
        }
    }
    /// <summary>The subject all-pairs shortest path: the guarded sum over all lengths of a tropical quiver element.</summary>
    /// <returns>The bound operation, owning its own presentation.</returns>
    public static VectorBinaryOp PresentedTropicalStar() {
        PresentedAlgebra<FixedQ4816, TropicalMaterial>? algebra = null;

        return (left, right, result) => {
            algebra ??= PresentedAlgebra<FixedQ4816, TropicalMaterial>.Create(presentation: CodiscreteQuiver<FixedQ4816, TropicalMaterial>(
                material: default,
                order: GraphOrder
            ));

            var count = right.Length;
            var coefficients = new FixedQ4816[count];
            var keys = new long[count];
            var support = 0;

            for (var lane = 0; (lane < count); ++lane) {
                if (0L == (right[lane] & 1L)) { continue; }

                coefficients[support] = Raw(value: GraphWeight(raw: left[lane]));
                keys[support] = lane;
                ++support;
            }

            var element = algebra.FromSupport(
                keys: keys.AsSpan(
                    length: support,
                    start: 0
                ),
                coefficients: coefficients.AsSpan(
                    length: support,
                    start: 0
                )
            );

            for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = long.MaxValue; }

            if (!algebra.TrySumOverAllLengths(
                obstruction: out _,
                total: out var total,
                value: element
            )) {
                for (var lane = 0; (lane < result.Length); ++lane) { result[lane] = -1L; }

                return;
            }

            for (var index = 0; (index < total.SupportCount); ++index) {
                result[((int)total.Keys[index])] = total.Coefficients[index].Value;
            }
        };
    }
    /// <summary>The shared-nothing all-pairs shortest path oracle.</summary>
    /// <param name="left">The weight lanes.</param>
    /// <param name="right">The arc-presence lanes.</param>
    /// <param name="result">The distance lanes.</param>
    public static void TropicalStarOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var count = right.Length;
        var weights = new long[count];

        for (var lane = 0; (lane < count); ++lane) {
            weights[lane] = ((0L == (right[lane] & 1L))
                ? long.MaxValue
                : GraphWeight(raw: left[lane])
            );
        }

        Oracles.TropicalShortestPath(
            order: GraphOrder,
            result: result,
            weights: weights
        );
    }
    /// <summary>The subject walk count: a power of a counting quiver element, by the pinned ascending-bit schedule, with
    /// the sequential schedule pinned against it in the same pass (the counting material is exact, so the two agree).</summary>
    /// <param name="length">The walk length.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedWalkCount(int length) {
        PresentedAlgebra<BigInteger, CountingMaterial>? owned = null;

        return (left, right, result) => {
            var algebra = (owned ??= PresentedAlgebra<BigInteger, CountingMaterial>.Create(presentation: CodiscreteQuiver<BigInteger, CountingMaterial>(
                material: default,
                order: GraphOrder
            )));
            var element = CountingAdjacency(
                algebra: algebra,
                left: left,
                right: right
            );
            var power = algebra.Power(
                exponent: ((ulong)length),
                value: element
            );
            var sequential = algebra.PowerSequential(
                exponent: ((ulong)length),
                value: element
            );

            result.Clear();

            for (var index = 0; (index < power.SupportCount); ++index) {
                result[((int)power.Keys[index])] = ((long)power.Coefficients[index]);
            }

            // The sequential schedule is a distinct public member with its own contract; over an exact material the two
            // must land on the same value, and a divergence is reported through the same lane comparison as everything
            // else by poisoning the result rather than by asserting here.
            for (var index = 0; (index < sequential.SupportCount); ++index) {
                if (sequential.Coefficients[index] != power[sequential.Keys[index]]) { result[((int)sequential.Keys[index])] = long.MinValue; }
            }

            if (sequential.SupportCount != power.SupportCount) { result[0] = long.MinValue; }
        };
    }
    /// <summary>The shared-nothing walk-count oracle, by repeated <see cref="BigInteger"/> matrix multiplication.</summary>
    /// <param name="length">The walk length.</param>
    /// <returns>The bound oracle.</returns>
    public static VectorBinaryOp WalkCountOracle(int length) =>
        (left, right, result) => {
            var count = right.Length;
            var adjacency = new BigInteger[count];
            var counts = new BigInteger[count];

            for (var lane = 0; (lane < count); ++lane) {
                adjacency[lane] = GraphMultiplicity(
                    left: left[lane],
                    right: right[lane]
                );
            }

            Oracles.WalkCount(
                adjacency: adjacency,
                length: length,
                order: GraphOrder,
                result: counts
            );

            for (var lane = 0; (lane < count); ++lane) { result[lane] = ((long)counts[lane]); }
        };

    /// <summary>Builds the counting quiver's adjacency element from an operand pair.</summary>
    /// <param name="algebra">The counting quiver algebra.</param>
    /// <param name="left">The multiplicity lanes.</param>
    /// <param name="right">The arc-presence lanes.</param>
    /// <returns>The adjacency element.</returns>
    private static PresentedAlgebra<BigInteger, CountingMaterial>.Element CountingAdjacency(PresentedAlgebra<BigInteger, CountingMaterial> algebra, ReadOnlySpan<long> left, ReadOnlySpan<long> right) {
        var count = right.Length;
        var coefficients = new BigInteger[count];
        var keys = new long[count];
        var support = 0;

        for (var lane = 0; (lane < count); ++lane) {
            var multiplicity = GraphMultiplicity(
                left: left[lane],
                right: right[lane]
            );

            if (multiplicity.IsZero) { continue; }

            coefficients[support] = multiplicity;
            keys[support] = lane;
            ++support;
        }

        return algebra.FromSupport(
            keys: keys.AsSpan(
                length: support,
                start: 0
            ),
            coefficients: coefficients.AsSpan(
                length: support,
                start: 0
            )
        );
    }

    /// <summary>Builds the codiscrete quiver on a given number of objects at any material: every ordered pair is an
    /// arrow, so the algebra IS the matrix algebra of that order.</summary>
    /// <typeparam name="TValue">The material's carrier.</typeparam>
    /// <typeparam name="TOps">The material.</typeparam>
    /// <param name="order">The number of objects.</param>
    /// <param name="material">The material.</param>
    /// <returns>The presentation.</returns>
    internal static ChargedPresentation<TValue, TOps> CodiscreteQuiver<TValue, TOps>(int order, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var arrows = new (int Source, int Target, TValue Weight)[(order * order)];

        for (var source = 0; (source < order); ++source) {
            for (var target = 0; (target < order); ++target) {
                arrows[((source * order) + target)] = (source, target, material.One);
            }
        }

        return Presentations.Quiver<TValue, TOps>(
            arrows: arrows,
            material: material,
            objectCount: order
        );
    }

}
