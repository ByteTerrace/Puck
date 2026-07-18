namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The host-facing audio output seam: a bounded ring of mixed stereo samples that the emulated mixer fills and the
/// host drains. The sink is strictly output-only — whether (or when) the host drains it never feeds back into
/// emulated state, so host audio timing cannot perturb determinism. For the same reason its contents are
/// <em>not</em> snapshot state: a snapshot captures none of it, and a restore clears it so a rewound machine starts
/// a fresh stream instead of replaying stale output.
/// </summary>
public interface IAudioSink {
    /// <summary>Gets the number of buffered samples available to drain (two per stereo frame, so always even).</summary>
    int AvailableSampleCount { get; }
    /// <summary>Gets the configured output sample rate in frames per emulated second, or zero while output is off.</summary>
    int SampleRate { get; }

    /// <summary>Enables (or, with zero, disables) audio output at the given sample rate, sizing the ring to hold one
    /// emulated second of stereo frames and discarding anything previously buffered. The rate is host configuration,
    /// not emulated state — it survives a snapshot restore and never appears in a snapshot.</summary>
    /// <param name="sampleRate">The output rate in frames per emulated second (<c>0</c>–<c>4194304</c>); zero turns
    /// output off.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleRate"/> is negative or exceeds the
    /// 4194304 Hz mixer rate.</exception>
    void Configure(int sampleRate);
    /// <summary>Drains buffered audio into the destination as interleaved left/right signed 16-bit samples, oldest
    /// first; whole frames are copied until the destination or the buffer is exhausted.</summary>
    /// <param name="destination">The span to fill.</param>
    /// <returns>The number of samples written (always even — two per frame).</returns>
    int ReadSamples(Span<short> destination);
}
