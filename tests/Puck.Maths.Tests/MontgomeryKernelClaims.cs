using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly BigInteger MontgomeryRadix = (BigInteger.One << 64);

    public static string? MontgomeryReductionMatchesBigInteger() {
        var generator = Pcg32XshRr.Create(state: 2026, stream: 7);

        ulong NextWord() => (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();

        for (var trial = 0; (trial < 60_000); ++trial) {
            // Alternate full-width moduli, moduli above 2^63 (where the radix sits between the modulus and twice it),
            // and narrow ones, then pin the carrier's extremes.
            var modulus = ((trial % 3) switch {
                0 => (NextWord() | 1UL),
                1 => (NextWord() | (1UL << 63) | 1UL),
                _ => ((NextWord() >> 32) | 1UL),
            });

            if (trial < 4) { modulus = ((ulong[])[3UL, ((1UL << 63) + 1UL), ulong.MaxValue, ((1UL << 63) - 1UL)])[trial]; }
            if (modulus < 3UL) { continue; }

            var inverse = modulus.ModularInverse();
            var one = ScaledResidueRing64.RadixResidue(modulus: modulus);

            if (((BigInteger)one) != (MontgomeryRadix % modulus)) { return $"RadixResidue({modulus}) = {one}, not 2^64 mod {modulus}"; }

            var left = (((trial & 7) == 0) ? (modulus - 1UL) : (NextWord() % modulus));
            var right = (((trial & 7) == 0) ? (modulus - 1UL) : (NextWord() % modulus));
            var reduced = ScaledResidueRing64.Reduce(inverse: inverse, left: left, modulus: modulus, right: right);

            // The radix is a unit modulo an odd modulus, so a reduced residue whose product with the radix matches
            // left * right is the one exact answer, prime modulus or not.
            if ((reduced >= modulus) || ((((((BigInteger)reduced) * MontgomeryRadix) - (((BigInteger)left) * right)) % modulus) != BigInteger.Zero)) {
                return $"Reduce({left}, {right}) modulo {modulus} = {reduced}, not the reduced product over the radix";
            }

            // One arbitrary word against a reduced operand is inside the admissible range as well: Encode's case.
            var word = NextWord();
            var encoded = ScaledResidueRing64.Reduce(inverse: inverse, left: word, modulus: modulus, right: right);

            if ((encoded >= modulus) || ((((((BigInteger)encoded) * MontgomeryRadix) - (((BigInteger)word) * right)) % modulus) != BigInteger.Zero)) {
                return $"Reduce({word}, {right}) modulo {modulus} = {encoded}, not the reduced product over the radix";
            }
            if (modulus > uint.MaxValue) { continue; }

            // The narrow lazy form carries residues in [1, modulus], with the modulus standing for zero.
            var narrowLeft = (((trial & 7) == 1) ? modulus : ((NextWord() % modulus) + 1UL));
            var narrowRight = (((trial & 7) == 2) ? modulus : ((NextWord() % modulus) + 1UL));
            var narrow = ScaledResidueRing64.ReduceNarrow(inverse: inverse, left: narrowLeft, modulus: modulus, right: narrowRight);

            if ((narrow == 0UL) || (narrow > modulus) || ((((((BigInteger)narrow) * MontgomeryRadix) - (((BigInteger)narrowLeft) * narrowRight)) % modulus) != BigInteger.Zero)) {
                return $"ReduceNarrow({narrowLeft}, {narrowRight}) modulo {modulus} = {narrow}, not the product over the radix in [1, modulus]";
            }
        }

        return null;
    }
    public static string? SelfridgeSymbolsMatchReciprocity() {
        var generator = Pcg32XshRr.Create(state: 2027, stream: 11);
        var values = new List<ulong>();

        for (var value = 3UL; (value < 6_000UL); value += 2UL) { values.Add(item: value); }
        for (var root = 3UL; (root < 2_000UL); root += 2UL) { values.Add(item: (root * root)); }
        for (var count = 0; (count < 4_000); ++count) {
            values.Add(item: ((((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32()) | 1UL);
        }

        values.Add(item: ulong.MaxValue);

        foreach (var value in values) {
            for (var magnitude = 5UL; (magnitude < 64UL); magnitude += 2UL) {
                // Method A's candidates alternate sign so that each is one modulo four: negative exactly at magnitudes
                // three modulo four.
                var discriminant = ((3UL == (magnitude & 3UL)) ? -((BigInteger)magnitude) : ((BigInteger)magnitude));
                var expected = Oracles.JacobiSymbolReciprocity(denominator: value, numerator: discriminant);
                var actual = PrimeKernels.SelfridgeSymbol(magnitude: magnitude, residue: (value % magnitude));

                if (actual != expected) {
                    return $"the tabulated symbol for D = {discriminant} over {value} is {actual}; the symbol (D / {value}) is {expected}";
                }
            }
        }

        return null;
    }
}
