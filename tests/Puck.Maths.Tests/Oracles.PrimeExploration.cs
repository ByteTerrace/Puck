using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>The documented algebraic labels, independently recorded rather than read from the subject.</summary>
    public static readonly byte[] PrimeExplorationResidues = [1, 7, 19, 13, 11, 17, 29, 23];
    /// <summary>The integer-index sieve used by every small exploration reference.</summary>
    public static readonly bool[] PrimeExplorationSieve = PrimeSieve(inclusiveMaximum: 100_000);

    /// <summary>Finds a channel by its explicit residue definition, without calling the shipped wheel.</summary>
    public static int PrimeExplorationChannel(ulong residue) => residue switch {
        1 => 0,
        7 => 1,
        19 => 2,
        13 => 3,
        11 => 4,
        17 => 5,
        29 => 6,
        23 => 7,
        _ => -1
    };
    /// <summary>Multiplies residues as small ordinary integers rather than algebraic channel coordinates.</summary>
    public static byte PrimeExplorationProduct(byte left, byte right) =>
        ((byte)PrimeExplorationChannel(residue: ((ulong)((PrimeExplorationResidues[left] * PrimeExplorationResidues[right]) % 30))));
    /// <summary>Pushes each selected residue through ordinary multiplication modulo thirty.</summary>
    public static byte PrimeExplorationPermutation(byte mask, byte multiplier) {
        var answer = 0;

        for (byte channel = 0; (channel < 8); ++channel) {
            if ((mask & (1 << channel)) != 0) {
                answer |= (1 << PrimeExplorationProduct(left: channel, right: multiplier));
            }
        }
        return ((byte)answer);
    }
    /// <summary>Forms a candidate in arbitrary width, so the reference itself cannot wrap the final block.</summary>
    public static BigInteger PrimeExplorationValue(ulong block, byte channel) =>
        ((new BigInteger(value: block) * 30) + PrimeExplorationResidues[channel]);
    /// <summary>Reads small primes from an integer-index sieve and larger ones from the independent twenty-base decision.</summary>
    public static List<ulong> PrimeExplorationPrimes(ulong low, ulong high) {
        var primes = new List<ulong>();

        if (high < low) { return primes; }
        for (var value = low; ; ++value) {
            var prime = ((high <= 100_000) ? PrimeExplorationSieve[((int)value)] : ExactPrimality(value: value));

            if (prime) { primes.Add(item: value); }
            if (value == high) { break; }
        }
        return primes;
    }
    /// <summary>Crosses out a uint interval by ordinary integer offsets, without wheel masks or carried cursors.</summary>
    public static List<ulong> PrimeExplorationIntegerWindow(uint low, uint high) {
        var composite = new bool[checked((int)((high - low) + 1))];
        var bases = PrimeSieve(inclusiveMaximum: 65535);

        for (uint prime = 2; (prime < bases.Length); ++prime) {
            if (!bases[prime]) { continue; }
            var square = (((ulong)prime) * prime);

            if (square > high) { break; }
            var first = Math.Max(val1: square, val2: ((((((ulong)low) + prime) - 1) / prime) * prime));

            for (var multiple = first; (multiple <= high); multiple += prime) {
                composite[((int)(multiple - low))] = true;
            }
        }
        var result = new List<ulong>();

        for (var offset = 0; (offset < composite.Length); ++offset) {
            var value = (((ulong)low) + ((uint)offset));

            if ((value >= 2) && !composite[offset]) { result.Add(item: value); }
        }
        return result;
    }
    /// <summary>Tests all eight residues against divisibility by two, three, and five.</summary>
    public static byte PrimeExplorationConstellationMask(ReadOnlySpan<ulong> offsets) {
        var mask = 0;

        for (var channel = 0; (channel < 8); ++channel) {
            var allowed = true;

            foreach (var offset in offsets) {
                var shifted = ((((ulong)PrimeExplorationResidues[channel]) + (offset % 30)) % 30);

                if (((shifted % 2) == 0) || ((shifted % 3) == 0) || ((shifted % 5) == 0)) { allowed = false; }
            }
            if (allowed) { mask |= (1 << channel); }
        }
        return ((byte)mask);
    }
    /// <summary>Searches prime starts directly, with arbitrary-width sums rather than lane masks or segment coordinates.</summary>
    public static List<ulong> PrimeExplorationConstellations(ulong low, ulong high, ReadOnlySpan<ulong> offsets) {
        var starts = new List<ulong>();

        foreach (var start in PrimeExplorationPrimes(high: high, low: low)) {
            var matches = true;

            foreach (var offset in offsets) {
                var shifted = (new BigInteger(value: start) + offset);

                if ((shifted > high) || !ExactPrimality(value: ((ulong)shifted))) { matches = false; break; }
            }
            if (matches) { starts.Add(item: start); }
        }
        return starts;
    }
    /// <summary>Counts primes, gaps, and adjacent wheel channels from an independently decided ordered list.</summary>
    public static (ulong Count, ulong SmallCount, byte SmallMask, ulong[] Channels, ulong[] Transitions, Dictionary<ulong, ulong> Gaps)
        PrimeExplorationStatistics(ulong low, ulong high) {
        var primes = PrimeExplorationPrimes(high: high, low: low);
        var channels = new ulong[8];
        var transitions = new ulong[64];
        var gaps = new Dictionary<ulong, ulong>();
        var smallCount = 0UL;
        var smallMask = 0;

        for (var index = 0; (index < primes.Count); ++index) {
            var prime = primes[index];
            var channel = PrimeExplorationChannel(residue: (prime % 30));

            if (channel >= 0) { ++channels[channel]; } else {
                ++smallCount;
                smallMask |= ((prime == 2) ? 1 : ((prime == 3) ? 2 : 4));
            }
            if (index == 0) { continue; }
            var previous = primes[(index - 1)];
            var gap = (prime - previous);

            gaps.TryGetValue(key: gap, value: out var count);
            gaps[gap] = (count + 1);
            var previousChannel = PrimeExplorationChannel(residue: (previous % 30));

            if ((channel >= 0) && (previousChannel >= 0)) { ++transitions[((previousChannel * 8) + channel)]; }
        }
        return (((ulong)primes.Count), smallCount, ((byte)smallMask), channels, transitions, gaps);
    }
}
