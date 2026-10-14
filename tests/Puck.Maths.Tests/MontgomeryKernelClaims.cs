using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly BigInteger MontgomeryRadix = (BigInteger.One << 64);

    public static string? MontgomeryReductionMatchesBigInteger() {
        var generator = Pcg32XshRr.Create(state: 2026, stream: 7);

        ulong NextWord() => (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();

        for (var trial = 0; (trial < 60_000); ++trial) {
            // Alternate full-width moduli, moduli above 2^63 (where the radix sits between the modulus and twice it),
            // moduli below the lazy ceiling and below 2^62 and narrow ones, then pin the carrier's extremes, the lazy
            // ceiling's neighbours and 2^62's.
            var modulus = ((trial % 5) switch {
                0 => (NextWord() | 1UL),
                1 => (NextWord() | (1UL << 63) | 1UL),
                2 => ((NextWord() >> 4) | 1UL),
                3 => ((NextWord() >> 2) | 1UL),
                _ => ((NextWord() >> 32) | 1UL),
            });

            if (trial < 8) {
                modulus = ((ulong[])[
                    3UL, ((1UL << 63) + 1UL), ulong.MaxValue, ((1UL << 63) - 1UL),
                    (ScaledResidueRing64.LazyModulusCeiling - 1UL), (ScaledResidueRing64.LazyModulusCeiling + 1UL),
                    ((1UL << 62) - 1UL), ((1UL << 62) + 1UL),
                ])[trial];
            }
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

            // The additive kernels are linear in the representation, so the residue arithmetic is the whole statement;
            // above 2^63 the untruncated sum leaves the carrier, which is where a fold read off the sum alone would err.
            var sum = ScaledResidueRing64.Add(left: left, modulus: modulus, right: right);
            var difference = ScaledResidueRing64.Subtract(left: left, modulus: modulus, right: right);
            var selected = ScaledResidueRing64.SubtractBySelect(left: left, modulus: modulus, right: right);

            if (((BigInteger)sum) != ((((BigInteger)left) + right) % modulus)) { return $"Add({left}, {right}) modulo {modulus} = {sum}, not the reduced sum"; }
            if (((BigInteger)difference) != (((((BigInteger)left) - right) + modulus) % modulus)) { return $"Subtract({left}, {right}) modulo {modulus} = {difference}, not the reduced difference"; }
            if (((BigInteger)selected) != (((((BigInteger)left) - right) + modulus) % modulus)) { return $"SubtractBySelect({left}, {right}) modulo {modulus} = {selected}, not the reduced difference"; }

            // One arbitrary word against a reduced operand is inside the admissible range as well: Encode's case.
            var word = NextWord();
            var encoded = ScaledResidueRing64.Reduce(inverse: inverse, left: word, modulus: modulus, right: right);

            if ((encoded >= modulus) || ((((((BigInteger)encoded) * MontgomeryRadix) - (((BigInteger)word) * right)) % modulus) != BigInteger.Zero)) {
                return $"Reduce({word}, {right}) modulo {modulus} = {encoded}, not the reduced product over the radix";
            }
            if (modulus < (1UL << 62)) {
                // The folds' whole admissible ranges: Canonical on values up to and including 4n - 1, which the carrier
                // holds below 2^62, and Fold on values up to and including 2n - 1.
                var wide = (((trial & 7) == 3) ? ((modulus << 2) - 1UL) : (NextWord() % (modulus << 2)));
                var folded = (((trial & 7) == 3) ? ((modulus << 1) - 1UL) : (NextWord() % (modulus << 1)));

                if (ScaledResidueRing64.Canonical(modulus: modulus, value: wide) != (wide % modulus)) { return $"Canonical({wide}) modulo {modulus} is not the residue"; }
                if (ScaledResidueRing64.Fold(modulus: modulus, value: folded) != (folded % modulus)) { return $"Fold({folded}) modulo {modulus} is not the residue"; }
            }
        }

        return null;
    }
    public static string? UnreducedChainsMatchExactDecision() {
        var generator = Pcg32XshRr.Create(state: 2058, stream: 13);
        var values = new List<ulong>();

        ulong NextWord() => (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();

        // Each ceiling is the largest bound under which every modulus below it keeps its chains' operand bound inside
        // the carrier: 16n at most 2^64 for lazy operands below 4n whose products are summed, 2n at most 2^64 for
        // signed operands within n of zero. The comparisons run in BigInteger so the run makes them.
        var ceiling = new BigInteger(value: ScaledResidueRing64.LazyModulusCeiling);
        var signedCeiling = new BigInteger(value: ScaledResidueRing64.SignedModulusCeiling);

        if (((16 * (ceiling - 1)) >= MontgomeryRadix) || ((16 * ceiling) < MontgomeryRadix)) { return $"the lazy modulus ceiling {ceiling} is not the largest bound keeping 16n below 2^64"; }
        if (((2 * (signedCeiling - 1)) >= MontgomeryRadix) || ((2 * signedCeiling) < MontgomeryRadix)) { return $"the signed modulus ceiling {signedCeiling} is not the largest bound keeping 2n below 2^64"; }

        // Values below the lazy ceiling and up to the signed ceiling, where the word decision's base-two round and its
        // Lucas ladder carry signed representatives with no fold; and a band above, where the base-two round carries
        // borrowed squares and the ladder folds after every product.
        for (var count = 0; (count < 1_200); ++count) { values.Add(item: (NextWord() >> 4) | 1UL); }
        for (var count = 0; (count < 600); ++count) { values.Add(item: ((NextWord() >> 1) | ScaledResidueRing64.LazyModulusCeiling) | 1UL); }
        for (var count = 0; (count < 300); ++count) { values.Add(item: NextWord() | ScaledResidueRing64.SignedModulusCeiling | 1UL); }
        // Each ceiling's neighbours on both sides, 2^61 and 2^62 among them.
        for (var offset = 1UL; (offset < 64UL); offset += 2UL) {
            values.Add(item: (ScaledResidueRing64.LazyModulusCeiling - offset));
            values.Add(item: (ScaledResidueRing64.LazyModulusCeiling + offset));
            values.Add(item: ((1UL << 61) - offset));
            values.Add(item: ((1UL << 61) + offset));
            values.Add(item: ((1UL << 62) - offset));
            values.Add(item: ((1UL << 62) + offset));
            values.Add(item: (ScaledResidueRing64.SignedModulusCeiling - offset));
            values.Add(item: (ScaledResidueRing64.SignedModulusCeiling + offset));
        }
        // Base-two strong pseudoprimes above 2^32: the fifth Fermat number and the least strong pseudoprimes to the first
        // five, six and seven prime bases below the lazy ceiling, the least strong pseudoprime to the first nine prime
        // bases below 2^62, four products (k + 1)(2k + 1) of two primes just above 2^60, and the least
        // (6k + 1)(12k + 1)(18k + 1) of three primes above the signed ceiling. Only the Lucas ladder rejects them.
        ulong[] pseudoprimes = [
            4_294_967_297UL, 2_152_302_898_747UL, 3_474_749_660_383UL, 341_550_071_728_321UL, 3_825_123_056_546_413_051UL,
            1_152_924_759_532_614_421UL, 1_152_932_412_797_378_461UL, 1_152_940_867_862_269_501UL, 1_152_942_690_078_591_301UL,
            9_275_096_661_522_257_161UL,
        ];

        foreach (var pseudoprime in pseudoprimes) {
            if (!PrimeField64.IsStrongProbablePrime(value: pseudoprime, witness: 2UL) || Oracles.ExactPrimality(value: pseudoprime) || Oracles.StrongLucasSelfridge(value: pseudoprime)) {
                return $"{pseudoprime} is not the composite base-two strong pseudoprime rejected by the Lucas test that this law relies on";
            }

            values.Add(item: pseudoprime);
        }

        foreach (var value in values) {
            var expected = Oracles.ExactPrimality(value: value);

            if (value.IsPrime() != expected) { return $"IsPrime({value}) disagrees with the exact BigInteger decision {expected}"; }
            if (PrimeField64.IsBaillieProbablePrime(value: value) != expected) { return $"IsBaillieProbablePrime({value}) disagrees with the exact BigInteger decision {expected}"; }

            var lucas = Oracles.StrongLucasSelfridge(value: value);

            if (PrimeField64.IsStrongLucasProbablePrime(value: value) != lucas) { return $"IsStrongLucasProbablePrime({value}) disagrees with the companion-matrix Lucas oracle {lucas}"; }
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
