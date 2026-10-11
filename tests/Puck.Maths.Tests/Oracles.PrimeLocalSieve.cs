using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>Encloses floor(r / max(14, 0.84 ln r)) with BigInteger arithmetic and repeated squaring.</summary>
    public static Enclosure PrimeGourdonCutoff(BigInteger root) {
        var logarithm = EncloseLog2(guardBitCount: 40, raw: (((long)root) << 16));
        // 693147180559945309 / 10^18 < ln(2) < 693147180559945310 / 10^18.
        // EncloseLog2 carries 56 fraction bits and shares no table or polynomial with the subject.
        var scale = ((new BigInteger(value: 1_000_000_000_000_000_000) * 100) << 56);
        var lowDivisor = BigInteger.Max(left: (14 * scale), right: ((84 * new BigInteger(value: 693_147_180_559_945_309)) * logarithm.Low));
        var highDivisor = BigInteger.Max(left: (14 * scale), right: ((84 * new BigInteger(value: 693_147_180_559_945_310)) * logarithm.High));

        return new(High: ((root * scale) / lowDivisor), Low: ((root * scale) / highDivisor));
    }
    /// <summary>Returns the widest local sieve width whose integer cost bound stays at or below one global count's work floor.</summary>
    /// <param name="value">The bound whose count the local sieve would replace.</param>
    /// <param name="gourdonCutoff">The Gourdon cutoff y for a Gourdon count, or <see langword="null"/> for the quotient recurrence.</param>
    /// <returns>The largest width W, at least one, with <c>ceil(W/span)*pass + W &lt;= floor</c>.</returns>
    /// <remarks>Every quantity is formed in BigInteger from the definitions: the root by an exact-squaring search, the
    /// Rosser-Schoenfeld bound ceil(1255060*root/(693147*floor(log2 root))) from the bit length, pass = root plus thirty
    /// per bounded prime, span = (16 MiB - 32 KiB - 1) * 30, and the floor as thirty per quotient initialization
    /// division, ceil(root/2), or floor(x/(y+1)). The answer comes from a binary search over the monotone cost
    /// predicate, not from the closed whole-window-plus-partial form.</remarks>
    public static ulong PrimeLocalSieveBudget(ulong value, uint? gourdonCutoff) {
        var x = ((BigInteger)value);
        var root = IntegerSquareRoot(value: x);
        var log2 = (((long)root.GetBitLength()) - 1);
        var primes = (((1_255_060 * root) + ((693_147 * log2) - 1)) / (693_147 * log2));
        var pass = (root + (30 * primes));
        var floor = ((gourdonCutoff is uint cutoff) ? (x / (((BigInteger)cutoff) + 1)) : (30 * ((root + 1) / 2)));
        var span = (((((16 * 1024) * 1024) - (32 * 1024)) - 1) * ((BigInteger)30));

        BigInteger Cost(BigInteger width) => (((((width + span) - 1) / span) * pass) + width);
        var low = BigInteger.Zero;
        var high = floor;

        while (low < high) {
            var middle = (((low + high) + 1) / 2);

            if (Cost(width: middle) <= floor) { low = middle; } else { high = (middle - 1); }
        }
        return ((ulong)BigInteger.Max(left: low, right: BigInteger.One));
    }
}
