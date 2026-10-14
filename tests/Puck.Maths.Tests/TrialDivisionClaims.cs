namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // Each trial-division rung, which every host runs: a rung its processor does not accelerate runs in software.
    private static readonly (string Name, Func<ulong, bool> Rung)[] TrialDivisionRungs = [
        ("512-bit", PrimeKernels.HasTrialFactor512), ("256-bit", PrimeKernels.HasTrialFactor256),
        ("128-bit", PrimeKernels.HasTrialFactor128), ("scalar", PrimeKernels.HasTrialFactorScalar),
    ];
    // Each narrow trial-division rung, over 32-bit values.
    private static readonly (string Name, Func<uint, bool> Rung)[] NarrowTrialDivisionRungs = [
        ("512-bit narrow", PrimeKernels.HasNarrowTrialFactor512), ("256-bit narrow", PrimeKernels.HasNarrowTrialFactor256),
        ("128-bit narrow", PrimeKernels.HasNarrowTrialFactor128), ("scalar narrow", PrimeKernels.HasNarrowTrialFactorScalar),
    ];

    /// <summary>Compares every trial-division rung with remainders by an independently listed set of primes, and the word decision with the exact oracle on either side of the table's last prime.</summary>
    /// <returns>The first counterexample, or <see langword="null"/>.</returns>
    public static string? TrialDivisionRungsMatchRemainders() {
        var generator = Pcg32XshRr.Create(state: 2309, stream: 41);
        var primes = new List<ulong>();

        ulong NextWord() => (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();

        // The odd primes by trial division, independent of the table the rungs read.
        for (var candidate = 3UL; (primes.Count < 140); candidate += 2UL) {
            var isPrime = true;

            for (var divisor = 3UL; ((divisor * divisor) <= candidate); divisor += 2UL) {
                if (0UL == (candidate % divisor)) { isPrime = false; break; }
            }
            if (isPrime) { primes.Add(item: candidate); }
        }

        var count = PrimeKernels.TrialDivisionPrimeCount;
        var ceiling = PrimeKernels.TrialDivisionCeiling;

        if (primes[(count - 1)] != ceiling) { return $"the {count}th odd prime is {primes[(count - 1)]}, not the trial-division ceiling {ceiling}"; }

        var tested = primes.GetRange(count: count, index: 0);
        var beyond = primes.GetRange(index: count, count: (primes.Count - count));

        string? Check(ulong value) {
            var expected = tested.Exists(match: prime => (0UL == (value % prime)));

            foreach (var (name, rung) in TrialDivisionRungs) {
                if (rung(arg: value) != expected) { return $"the {name} trial-division rung says {!expected} for {value}, whose remainders say {expected}"; }
            }
            if (value <= uint.MaxValue) {
                foreach (var (name, rung) in NarrowTrialDivisionRungs) {
                    if (rung(arg: ((uint)value)) != expected) { return $"the {name} trial-division rung says {!expected} for {value}, whose remainders say {expected}"; }
                }
            }

            return null;
        }

        // Every value below 2^16, every multiple of each tested prime at the top of the carrier, each tested prime times
        // each prime past the table, its square and drawn multiples, products of primes past the table, which no tested
        // prime divides, drawn words and the top of the carrier.
        for (var value = 0UL; (value < (1UL << 16)); ++value) {
            if (Check(value: value) is { } failure) { return failure; }
        }
        foreach (var prime in tested) {
            var top = ((ulong.MaxValue / prime) * prime);
            var values = new List<ulong> { prime, (prime * prime), top, (top - prime), (top + 1UL), (top - 1UL) };

            foreach (var other in beyond) { values.Add(item: (prime * other)); }
            for (var draw = 0; (draw < 16); ++draw) { values.Add(item: ((NextWord() / prime) * prime)); }
            foreach (var value in values) {
                if (Check(value: value) is { } failure) { return failure; }
            }
        }
        // Words whose 16-bit digits sit at the folding rungs' extremes, 0, 2^15 - 1, 2^15 and 2^16 - 1 in every
        // position, where the balanced digit sum reaches furthest from zero, and each tested prime's multiples nearest
        // them.
        ulong[] digitExtremes = [0x0000UL, 0x7FFFUL, 0x8000UL, 0xFFFFUL];

        foreach (var d0 in digitExtremes) {
            foreach (var d1 in digitExtremes) {
                foreach (var d2 in digitExtremes) {
                    foreach (var d3 in digitExtremes) {
                        var word = ((d0 | (d1 << 16)) | (d2 << 32)) | (d3 << 48);

                        if (Check(value: word) is { } failure) { return failure; }
                        foreach (var prime in tested) {
                            var below = ((word / prime) * prime);

                            if ((Check(value: below) ?? Check(value: (below + prime))) is { } multiple) { return multiple; }
                        }
                    }
                }
            }
        }
        foreach (var first in beyond) {
            foreach (var second in beyond) {
                if ((Check(value: (first * second)) ?? Check(value: ((first * second) * first))) is { } failure) { return failure; }
            }
        }
        for (var draw = 0; (draw < 40_000); ++draw) {
            if (Check(value: NextWord()) is { } failure) { return failure; }
        }
        for (var offset = 0UL; (offset < 1_024UL); ++offset) {
            if ((Check(value: (ulong.MaxValue - offset)) ?? Check(value: (uint.MaxValue - offset))) is { } failure) { return failure; }
        }
        // The narrow rungs' own top: every multiple of each tested prime nearest 2^32, a drawn 32-bit multiple of each,
        // and drawn 32-bit words.
        foreach (var prime in tested) {
            var top = ((uint.MaxValue / prime) * prime);

            foreach (var value in ((ulong[])[top, (top - prime), (top + 1UL), (top - 1UL), (((NextWord() & uint.MaxValue) / prime) * prime)])) {
                if (Check(value: value) is { } failure) { return failure; }
            }
        }
        for (var draw = 0; (draw < 40_000); ++draw) {
            if (Check(value: NextWord() & uint.MaxValue) is { } failure) { return failure; }
        }

        // The word decision above 2^32 on products whose least factor is the table's last primes or the first primes
        // past it, the cofactor the first prime above each of 2^24, 2^40 and the largest the carrier holds: those past
        // the table reach Baillie–PSW, and each is composite.
        foreach (var least in ((ulong[])[3UL, 5UL, 7UL, primes[(count - 2)], ceiling, beyond[0], beyond[1], beyond[2]])) {
            foreach (var start in ((ulong[])[((1UL << 24) + 1UL), ((1UL << 40) + 1UL), (((ulong.MaxValue / least) - 4_000UL) | 1UL)])) {
                var cofactor = start;

                while (!Oracles.ExactPrimality(value: cofactor)) { cofactor += 2UL; }

                var product = (least * cofactor);

                if (product.IsPrime() || Oracles.ExactPrimality(value: product)) { return $"IsPrime({product}) = {product.IsPrime()} for the composite {least} * {cofactor}"; }
                if (!cofactor.IsPrime()) { return $"IsPrime({cofactor}) is false for a prime"; }
            }
        }

        return null;
    }
}
