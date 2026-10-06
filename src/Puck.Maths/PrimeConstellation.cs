namespace Puck.Maths;

/// <summary>A finite prime pattern defined by ascending nonnegative offsets from its prime anchor.</summary>
/// <remarks>The wheel mask is an early rejection for tuples entirely above five. Tuples involving two, three or five are tested explicitly. Every reported member lies inside the requested interval.</remarks>
public sealed class PrimeConstellation {
    private readonly ulong[] m_offsets;

    /// <summary>Creates a pattern and its algebraic wheel admissibility mask.</summary>
    /// <param name="offsets">A nonempty strictly increasing sequence beginning with zero. The sequence is copied.</param>
    /// <exception cref="ArgumentException"><paramref name="offsets"/> is empty, does not begin with zero, or is not strictly increasing.</exception>
    public PrimeConstellation(ReadOnlySpan<ulong> offsets) {
        if (offsets.IsEmpty || (offsets[0] != 0)) {
            throw new ArgumentException(message: "Offsets must be nonempty and begin with zero.", paramName: nameof(offsets));
        }
        for (var i = 1; (i < offsets.Length); ++i) {
            if (offsets[i] <= offsets[(i - 1)]) {
                throw new ArgumentException(message: "Offsets must be strictly increasing.", paramName: nameof(offsets));
            }
        }
        m_offsets = offsets.ToArray();
        var mask = ((byte)0);

        for (byte channel = 0; (channel < 8); ++channel) {
            var admissible = true;

            foreach (var offset in offsets) {
                var residue = ((byte)((PrimeWheel30.Residues[channel] + (offset % 30)) % 30));

                if (!PrimeWheel30.TryChannel(channel: out _, residue: residue)) {
                    admissible = false;
                    break;
                }
            }
            if (admissible) {
                mask |= ((byte)(1 << channel));
            }
        }
        AdmissibleMask = mask;
    }

    /// <summary>Gets the copied offsets, beginning with zero and strictly increasing.</summary>
    public ReadOnlySpan<ulong> Offsets => m_offsets;
    /// <summary>Gets the admissible anchor channels as an eight-bit algebraic mask.</summary>
    /// <remarks>A zero mask rules out tuples entirely above five, but does not rule out exceptional small-prime tuples.</remarks>
    public byte AdmissibleMask { get; }

    /// <summary>Reports all prime anchors whose complete pattern lies in an inclusive interval, in ascending numerical order.</summary>
    /// <param name="low">The inclusive lower bound for every member.</param>
    /// <param name="high">The inclusive upper bound for every member.</param>
    /// <param name="visit">Receives each anchor once; member values are the anchor plus <see cref="Offsets"/>.</param>
    /// <param name="segmentBytes">The segment storage size passed to prime enumeration.</param>
    /// <param name="strategy">The marking strategy passed to prime enumeration.</param>
    /// <param name="layout">The byte layout passed to prime enumeration.</param>
    /// <param name="mode">The enumeration mode passed to prime enumeration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="visit"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="low"/> exceeds <paramref name="high"/>, <paramref name="segmentBytes"/> is not positive, or an enumeration option is undefined.</exception>
    /// <remarks>No addition wraps at the top of the unsigned 64-bit domain. Each non-anchor member uses deterministic primality testing.</remarks>
    public void Enumerate(
        ulong low,
        ulong high,
        Action<ulong> visit,
        int segmentBytes = 32768,
        PrimeSieveStrategy strategy = PrimeSieveStrategy.BucketPackets,
        PrimeByteLayout layout = PrimeByteLayout.Numeric,
        PrimeSieveMode mode = PrimeSieveMode.Automatic
    ) {
        ArgumentNullException.ThrowIfNull(visit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(low, high);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentBytes);
        if (!Enum.IsDefined(value: strategy)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(strategy));
        }
        if (!Enum.IsDefined(value: layout)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(layout));
        }
        if (!Enum.IsDefined(value: mode)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(mode));
        }
        var maximumOffset = m_offsets[^1];

        if (maximumOffset > high) {
            return;
        }
        var anchorHigh = (high - maximumOffset);

        if (low > anchorHigh) {
            return;
        }

        ReadOnlySpan<ulong> exceptionalPrimes = [2, 3, 5];

        foreach (var prime in exceptionalPrimes) {
            if ((prime >= low) && (prime <= anchorHigh) && Matches(anchor: prime)) {
                visit(prime);
            }
        }
        var anchorLow = Math.Max(val1: low, val2: 7UL);

        if ((AdmissibleMask == 0) || (anchorLow > anchorHigh)) {
            return;
        }
        PrimeExploration.Enumerate(anchorLow, anchorHigh, anchor => {
            PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)(anchor % 30)));
            if (((AdmissibleMask & (1 << channel)) != 0) && Matches(anchor: anchor)) {
                visit(anchor);
            }
        }, segmentBytes, strategy, layout, mode);
    }

    private bool Matches(ulong anchor) {
        for (var i = 1; (i < m_offsets.Length); ++i) {
            if (!PrimeExploration.IsPrime(value: (anchor + m_offsets[i]))) {
                return false;
            }
        }
        return true;
    }
}
