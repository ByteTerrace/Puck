using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    /// <summary>Compares the signed and borrowed Montgomery kernels with BigInteger at the extremes of their operand bounds.</summary>
    /// <returns>The first counterexample, or <see langword="null"/>.</returns>
    public static string? SignedReductionMatchesBigInteger() {
        var generator = Pcg32XshRr.Create(state: 2101, stream: 37);
        var ceiling = new BigInteger(value: ScaledResidueRing64.SignedModulusCeiling);

        ulong NextWord() => (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();

        // The ceiling is the largest bound keeping 2n within the carrier, the bound every interval below rests on.
        if (((2 * (ceiling - 1)) >= MontgomeryRadix) || ((2 * ceiling) < MontgomeryRadix)) {
            return $"the signed modulus ceiling {ceiling} is not the largest bound keeping 2n within 2^64";
        }

        // The moduli: the smallest, each side of 2^32, 2^60, 2^61 and 2^62, the ceiling's own odd neighbours below it,
        // then drawn ones, half of them in [2^62, 2^63) where the products reach furthest past the radix.
        var moduli = new List<ulong> {
            3UL, 5UL, 7UL, ((1UL << 32) - 1UL), ((1UL << 32) + 1UL), ((1UL << 60) - 1UL), ((1UL << 60) + 1UL),
            ((1UL << 61) - 1UL), ((1UL << 61) + 1UL), ((1UL << 62) - 1UL), ((1UL << 62) + 1UL),
        };

        for (var offset = 1UL; (offset < 512UL); offset += 2UL) { moduli.Add(item: (ScaledResidueRing64.SignedModulusCeiling - offset)); }
        for (var count = 0; (count < 2_000); ++count) {
            moduli.Add(item: ((count % 4) switch {
                0 => ((NextWord() >> 34) | 1UL),
                1 => ((NextWord() >> 3) | 1UL),
                _ => (((NextWord() >> 2) | (1UL << 62)) | 1UL),
            }));
        }

        foreach (var modulus in moduli) {
            if (modulus < 3UL) { continue; }

            var inverse = modulus.ModularInverse();
            var n = new BigInteger(value: modulus);
            var half = ((long)(modulus >>> 1));
            var narrow = ((long)(modulus - 1UL));

            long Draw(long bound) => unchecked(((long)((NextWord() % ((((ulong)bound) << 1) + 1UL)) - ((ulong)bound))));
            // Operands at both ends of the bound, zero and one, and drawn ones; subtrahends at both ends of theirs.
            long[] operands = [-narrow, narrow, -(narrow - 1L), (narrow - 1L), 0L, 1L, -1L, Draw(bound: narrow), Draw(bound: narrow)];
            long[] constants = [-half, half, 0L, Draw(bound: half)];

            // The product less a centered constant stays within n of zero and stands for the product over the radix.
            foreach (var left in operands) {
                foreach (var right in operands) {
                    foreach (var constant in constants) {
                        var result = ScaledResidueRing64.ReduceSigned(inverse: inverse, left: left, modulus: modulus, right: right, subtrahend: constant);

                        if (SignedFailure(constant: constant, doubling: 0, kernel: "ReduceSigned", left: left, modulus: n, result: result, right: right) is { } failure) { return failure; }
                    }
                }
            }
            // The square less a centered constant, and the base-two step, doubled or not and less nothing.
            foreach (var value in operands) {
                foreach (var constant in constants) {
                    var result = ScaledResidueRing64.ReduceSignedSquareSubtract(inverse: inverse, modulus: modulus, subtrahend: constant, value: value);

                    if (SignedFailure(constant: constant, doubling: 0, kernel: "ReduceSignedSquareSubtract", left: value, modulus: n, result: result, right: value) is { } failure) { return failure; }
                }
                for (var doubling = 0; (doubling < 2); ++doubling) {
                    var result = ScaledResidueRing64.ReduceSignedSquare(doubling: doubling, inverse: inverse, modulus: modulus, value: value);

                    if (SignedFailure(constant: 0L, doubling: doubling, kernel: "ReduceSignedSquare", left: value, modulus: n, result: result, right: value) is { } failure) { return failure; }
                }
            }
            // The representatives: least magnitude for a reduced residue.
            foreach (var residue in ((ulong[])[0UL, 1UL, (modulus >>> 1), ((modulus >>> 1) + 1UL), (modulus - 1UL), (NextWord() % modulus)])) {
                var centered = ScaledResidueRing64.Center(modulus: modulus, value: residue);

                if ((long.Abs(value: centered) > half) || (BigInteger.Remainder(dividend: (new BigInteger(value: centered) - residue), divisor: n) != BigInteger.Zero)) {
                    return $"Center({residue}) modulo {modulus} = {centered}, not the residue's representative of least magnitude";
                }
            }
        }

        return BorrowedSquareMatchesBigInteger(next: NextWord);
    }

    /// <summary>Compares the borrowed square with BigInteger at every size of modulus, the top of the carrier included.</summary>
    /// <param name="next">The word source.</param>
    /// <returns>The first counterexample, or <see langword="null"/>.</returns>
    private static string? BorrowedSquareMatchesBigInteger(Func<ulong> next) {
        // The moduli: the smallest, each side of 2^62 and 2^63, the carrier's top odd values, and drawn ones, half of
        // them above 2^63, where no signed word holds a representative within the modulus of zero.
        var moduli = new List<ulong> {
            3UL, 5UL, ((1UL << 62) - 1UL), ((1UL << 62) + 1UL), ((1UL << 63) - 1UL), ((1UL << 63) + 1UL),
        };

        for (var offset = 0UL; (offset < 512UL); offset += 2UL) { moduli.Add(item: (ulong.MaxValue - offset)); }
        for (var count = 0; (count < 2_000); ++count) {
            moduli.Add(item: (((count % 2) == 0) ? next() | (1UL << 63) | 1UL : next() | 1UL));
        }

        foreach (var modulus in moduli) {
            var inverse = modulus.ModularInverse();
            var n = new BigInteger(value: modulus);
            var drawn = (next() % modulus);
            BigInteger[] representatives = [
                (n - 1), -(n - 1), (n - 2), -(n - 2), 0, 1, -1, drawn, -new BigInteger(value: (next() % modulus)),
            ];

            foreach (var representative in representatives) {
                // The word is the representative modulo 2^64, and the borrow mask is set exactly for a negative one.
                var word = ((ulong)BigInteger.Remainder(dividend: (representative + MontgomeryRadix), divisor: MontgomeryRadix));
                var borrow = ((representative.Sign < 0) ? ulong.MaxValue : 0UL);

                var (value, carried) = ScaledResidueRing64.ReduceBorrowedSquare(borrow: borrow, inverse: inverse, modulus: modulus, value: word);

                if ((carried != 0UL) && (carried != ulong.MaxValue)) { return $"ReduceBorrowedSquare({representative}) modulo {modulus} returned the borrow mask {carried}, neither zero nor all ones"; }

                var result = (new BigInteger(value: value) - ((carried == 0UL) ? BigInteger.Zero : MontgomeryRadix));

                if ((result <= -n) || (result >= n) || (BigInteger.Remainder(dividend: ((result * MontgomeryRadix) - (representative * representative)), divisor: n) != BigInteger.Zero)) {
                    return $"ReduceBorrowedSquare({representative}) modulo {modulus} = {result}, not a value in (-n, n) standing for the square over the radix";
                }
            }
        }

        return null;
    }
    /// <summary>Checks one signed kernel result against the interval <c>(-n, n)</c> and its congruence, in BigInteger.</summary>
    /// <param name="kernel">The kernel's name, for the message.</param>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor, before any doubling.</param>
    /// <param name="doubling">The doubling applied to the product.</param>
    /// <param name="constant">The subtracted constant.</param>
    /// <param name="modulus">The modulus.</param>
    /// <param name="result">The kernel's result.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    private static string? SignedFailure(string kernel, long left, long right, int doubling, long constant, BigInteger modulus, long result) {
        var value = new BigInteger(value: result);
        var product = ((new BigInteger(value: left) * right) << doubling);

        // The radix is a unit modulo an odd modulus, so (result + constant) 2^64 = product modulo n fixes the congruence.
        if ((value <= -modulus) || (value >= modulus) || (BigInteger.Remainder(dividend: ((((value + constant) * MontgomeryRadix) - product)), divisor: modulus) != BigInteger.Zero)) {
            return $"{kernel}({left}, {right}, doubling {doubling}, less {constant}) modulo {modulus} = {result}, not a value in (-n, n) standing for the product over the radix less the constant";
        }

        return null;
    }
}
