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

        for (byte channel = 0; (channel < PrimeWheel30.ChannelCount); ++channel) {
            var admissible = true;

            foreach (var offset in offsets) {
                var residue = ((byte)((PrimeWheel30.Residues[channel] + (offset % PrimeWheel30.Modulus)) % PrimeWheel30.Modulus));

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
    /// <param name="high">The inclusive upper bound for every member; a reversed interval is empty.</param>
    /// <param name="visit">Receives each anchor once; member values are the anchor plus <see cref="Offsets"/>.</param>
    /// <param name="cancellationToken">Cancels the anchor enumeration with the contract of <see cref="PrimeExploration.Enumerate(ulong, ulong, Action{ulong}, CancellationToken)"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="visit"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <remarks>Anchors come from <see cref="PrimeExploration.Enumerate(ulong, ulong, Action{ulong}, CancellationToken)"/>. No addition
    /// wraps at the top of the unsigned 64-bit domain. Each non-anchor member uses deterministic primality testing.</remarks>
    public void Enumerate(ulong low, ulong high, Action<ulong> visit, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(visit);
        cancellationToken.ThrowIfCancellationRequested();
        if (high < low) { return; }
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
            PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)(anchor % PrimeWheel30.Modulus)));
            if (((AdmissibleMask & (1 << channel)) != 0) && Matches(anchor: anchor)) {
                visit(anchor);
            }
        }, cancellationToken);
    }

    private bool Matches(ulong anchor) {
        for (var i = 1; (i < m_offsets.Length); ++i) {
            if (!PrimeExtensions.IsPrime(value: (anchor + m_offsets[i]))) {
                return false;
            }
        }
        return true;
    }
}
