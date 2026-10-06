using System.Collections.ObjectModel;

namespace Puck.Maths;

/// <summary>Exact counts, residue transitions and consecutive gaps within an inclusive prime interval.</summary>
/// <remarks>Transitions use algebraic channels and involve only adjacent reported primes both greater than five. Gaps include the exceptional small primes. Neither statistic invents a neighboring prime outside the requested interval.</remarks>
public sealed class PrimeStatistics {
    private readonly ulong[] m_channelCounts = new ulong[8];
    private readonly ulong[] m_transitionCounts = new ulong[64];
    private readonly Dictionary<ulong, ulong> m_gapCounts = [];
    private readonly ReadOnlyDictionary<ulong, ulong> m_readOnlyGapCounts;

    private PrimeStatistics() {
        m_readOnlyGapCounts = new(dictionary: m_gapCounts);
    }

    /// <summary>Gets the number of reported primes, including two, three and five when present.</summary>
    public ulong Count { get; private set; }
    /// <summary>Gets the number of reported primes among two, three and five.</summary>
    public ulong SmallPrimeCount { get; private set; }
    /// <summary>Gets the exceptional-prime presence mask: bit zero for two, bit one for three and bit two for five.</summary>
    public byte SmallPrimeMask { get; private set; }
    /// <summary>Gets eight counts for primes greater than five, indexed by algebraic channel.</summary>
    public ReadOnlySpan<ulong> ChannelCounts => m_channelCounts;
    /// <summary>Gets sixty-four adjacent channel transition counts, indexed by <c>8 * from + to</c>.</summary>
    public ReadOnlySpan<ulong> TransitionCounts => m_transitionCounts;
    /// <summary>Gets each consecutive reported prime gap and its exact occurrence count.</summary>
    public IReadOnlyDictionary<ulong, ulong> GapCounts => m_readOnlyGapCounts;

    /// <summary>Gets the number of transitions between two algebraic channels.</summary>
    /// <param name="from">The preceding prime's channel, from zero through seven.</param>
    /// <param name="to">The following prime's channel, from zero through seven.</param>
    /// <returns>The exact number of adjacent reported prime pairs with these channels.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either channel exceeds seven.</exception>
    public ulong TransitionCount(byte from, byte to) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, ((byte)7));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(to, ((byte)7));
        return m_transitionCounts[((8 * from) + to)];
    }
    /// <summary>Measures an inclusive prime interval using exact unsigned counts.</summary>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound.</param>
    /// <param name="segmentBytes">The segment storage size passed to prime enumeration.</param>
    /// <param name="strategy">The marking strategy passed to prime enumeration.</param>
    /// <param name="layout">The byte layout passed to prime enumeration.</param>
    /// <param name="mode">The enumeration mode passed to prime enumeration.</param>
    /// <returns>An immutable view of the interval's counts and transitions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An interval, segment size or enumeration option is invalid.</exception>
    public static PrimeStatistics Measure(
        ulong low,
        ulong high,
        int segmentBytes = 32768,
        PrimeSieveStrategy strategy = PrimeSieveStrategy.BucketPackets,
        PrimeByteLayout layout = PrimeByteLayout.Numeric,
        PrimeSieveMode mode = PrimeSieveMode.Automatic
    ) {
        var statistics = new PrimeStatistics();
        var hasPrevious = false;
        var previous = 0UL;
        byte previousChannel = 0;

        PrimeExploration.Enumerate(low, high, prime => {
            ++statistics.Count;
            if (hasPrevious) {
                var gap = (prime - previous);

                statistics.m_gapCounts.TryGetValue(key: gap, value: out var count);
                statistics.m_gapCounts[gap] = (count + 1);
            }
            if (prime <= 5) {
                ++statistics.SmallPrimeCount;
                statistics.SmallPrimeMask |= prime switch {
                    2 => ((byte)1),
                    3 => ((byte)2),
                    5 => ((byte)4),
                    _ => ((byte)0),
                };
            } else {
                PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)(prime % 30)));
                ++statistics.m_channelCounts[channel];
                if (hasPrevious && (previous > 5)) {
                    ++statistics.m_transitionCounts[((8 * previousChannel) + channel)];
                }
                previousChannel = channel;
            }
            previous = prime;
            hasPrevious = true;
        }, segmentBytes, strategy, layout, mode);
        return statistics;
    }
}
