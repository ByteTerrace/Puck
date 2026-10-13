using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    /// <summary>Compares the centered Montgomery kernels with BigInteger at the extremes of their operand bounds.</summary>
    /// <returns>The first counterexample, or <see langword="null"/>.</returns>
    public static string? CenteredReductionMatchesBigInteger() {
        var generator = Pcg32XshRr.Create(state: 2101, stream: 37);
        var ceiling = new BigInteger(value: ScaledResidueRing64.CenteredModulusCeiling);

        ulong NextWord() => (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();

        // The ceiling is the largest bound keeping 4n within the carrier, the bound every interval below rests on.
        if (((4 * (ceiling - 1)) >= MontgomeryRadix) || ((4 * ceiling) < MontgomeryRadix)) {
            return $"the centered modulus ceiling {ceiling} is not the largest bound keeping 4n within 2^64";
        }

        // The moduli: the smallest, each side of 2^32, 2^60 and 2^61, the ceiling's own odd neighbours, then drawn ones,
        // most of them in [2^61, 2^62) where the products reach furthest past the radix.
        var moduli = new List<ulong> {
            3UL, 5UL, 7UL, ((1UL << 32) - 1UL), ((1UL << 32) + 1UL), ((1UL << 60) - 1UL), ((1UL << 60) + 1UL),
            ((1UL << 61) - 1UL), ((1UL << 61) + 1UL),
        };

        for (var offset = 1UL; (offset < 512UL); offset += 2UL) { moduli.Add(item: (ScaledResidueRing64.CenteredModulusCeiling - offset)); }
        for (var count = 0; (count < 2_000); ++count) {
            moduli.Add(item: ((count % 4) switch {
                0 => ((NextWord() >> 34) | 1UL),
                1 => ((NextWord() >> 4) | 1UL),
                _ => (((NextWord() >> 3) | (1UL << 61)) | 1UL),
            }));
        }

        foreach (var modulus in moduli) {
            if (modulus < 3UL) { continue; }

            var inverse = modulus.ModularInverse();
            var n = new BigInteger(value: modulus);
            var half = ((long)(modulus >>> 1));
            var wide = ((long)((modulus << 1) - 1UL));
            var narrow = ((long)(modulus - 1UL));

            long Draw(long bound) => unchecked(((long)((NextWord() % ((((ulong)bound) << 1) + 1UL)) - ((ulong)bound))));
            // Operands at both ends of each bound, zero and one, and drawn ones; subtrahends at both ends of theirs.
            long[] traces = [-wide, wide, -(wide - 1L), (wide - 1L), 0L, 1L, -1L, Draw(bound: wide), Draw(bound: wide)];
            long[] accumulators = [-narrow, narrow, -(narrow - 1L), (narrow - 1L), 0L, 1L, -1L, Draw(bound: narrow), Draw(bound: narrow)];
            long[] constants = [-half, half, 0L, Draw(bound: half)];

            // The cross product less a centered constant stays within 2n of zero and stands for the product over the radix.
            foreach (var left in traces) {
                foreach (var right in traces) {
                    foreach (var constant in constants) {
                        var result = ScaledResidueRing64.ReduceCentered(inverse: inverse, left: left, modulus: modulus, right: right, subtrahend: constant);

                        if (CenteredFailure(bound: ((-2 * n), (2 * n)), constant: constant, doubling: 0, kernel: "ReduceCentered", left: left, modulus: n, result: result, right: right) is { } failure) { return failure; }
                    }
                }
            }
            // The square less a centered constant: within (-n, 2n) for a trace, inside the cross product's bound.
            foreach (var value in traces) {
                foreach (var constant in constants) {
                    var result = ScaledResidueRing64.ReduceCenteredSquare(doubling: 0, inverse: inverse, modulus: modulus, subtrahend: constant, value: value);

                    if (CenteredFailure(bound: (-n, (2 * n)), constant: constant, doubling: 0, kernel: "ReduceCenteredSquare", left: value, modulus: n, result: result, right: value) is { } failure) { return failure; }
                }
            }
            // The base-two step: an accumulator within n of zero, doubled or not, stays within (-n / 2, n).
            foreach (var value in accumulators) {
                for (var doubling = 0; (doubling < 2); ++doubling) {
                    var result = ScaledResidueRing64.ReduceCenteredSquare(doubling: doubling, inverse: inverse, modulus: modulus, subtrahend: 0L, value: value);

                    if (((2 * new BigInteger(value: result)) <= -n) || (CenteredFailure(bound: (-n, n), constant: 0L, doubling: doubling, kernel: "ReduceCenteredSquare", left: value, modulus: n, result: result, right: value) is not null)) {
                        return $"ReduceCenteredSquare({value}, doubling {doubling}) modulo {modulus} = {result}, not a value in (-n / 2, n) standing for 2^doubling value^2 over the radix";
                    }
                }
            }
            // The representatives: least magnitude for a reduced residue, the residue for a centered one.
            foreach (var residue in ((ulong[])[0UL, 1UL, (modulus >>> 1), ((modulus >>> 1) + 1UL), (modulus - 1UL), (NextWord() % modulus)])) {
                var centered = ScaledResidueRing64.Center(modulus: modulus, value: residue);

                if ((long.Abs(value: centered) > half) || (BigInteger.Remainder(dividend: (new BigInteger(value: centered) - residue), divisor: n) != BigInteger.Zero)) {
                    return $"Center({residue}) modulo {modulus} = {centered}, not the residue's representative of least magnitude";
                }
            }
            foreach (var value in traces) {
                var canonical = ScaledResidueRing64.CanonicalCentered(modulus: modulus, value: value);
                var expected = BigInteger.Remainder(dividend: (BigInteger.Remainder(dividend: value, divisor: n) + n), divisor: n);

                if (canonical != expected) { return $"CanonicalCentered({value}) modulo {modulus} = {canonical}, not the residue {expected}"; }
            }
        }

        return null;
    }

    /// <summary>Checks one centered kernel result against its interval and its congruence, in BigInteger.</summary>
    /// <param name="kernel">The kernel's name, for the message.</param>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor, before any doubling.</param>
    /// <param name="doubling">The doubling applied to the product.</param>
    /// <param name="constant">The subtracted constant.</param>
    /// <param name="modulus">The modulus.</param>
    /// <param name="result">The kernel's result.</param>
    /// <param name="bound">The open interval the result must lie in.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    private static string? CenteredFailure(string kernel, long left, long right, int doubling, long constant, BigInteger modulus, long result, (BigInteger Low, BigInteger High) bound) {
        var value = new BigInteger(value: result);
        var product = ((new BigInteger(value: left) * right) << doubling);

        // The radix is a unit modulo an odd modulus, so (result + constant) 2^64 = product modulo n fixes the congruence.
        if ((value <= bound.Low) || (value >= bound.High) || (BigInteger.Remainder(dividend: ((((value + constant) * MontgomeryRadix) - product)), divisor: modulus) != BigInteger.Zero)) {
            return $"{kernel}({left}, {right}, doubling {doubling}, less {constant}) modulo {modulus} = {result}, not a value in ({bound.Low}, {bound.High}) standing for the product over the radix less the constant";
        }

        return null;
    }
}
