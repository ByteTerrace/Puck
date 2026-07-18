namespace Puck.Abstractions.Machines;

/// <summary>
/// Optional audio-output capability for an <see cref="IScreenMachine"/> whose content produces sound — the neutral
/// seam a speaker device drains instead of reaching past the contract into a concrete core, mirroring
/// <see cref="IQueuedScreenMachine"/>'s optional-capability precedent. Strictly output-only and presentation-side by
/// design, exactly like the cores' own host-facing audio rings: a machine's simulation state never depends on
/// whether (or how fast) a consumer drains this, so it carries no snapshot state and costs an unattached machine
/// nothing. An implementor fixes whether (and at what rate) it synthesizes audio for its whole lifetime — the same
/// attach-at-construction shape a device costume or fit policy already uses — so <see cref="SampleRate"/> answers 0
/// forever on a machine no consumer ever asked for sound, and the underlying core performs zero presentation-side
/// mix/resample work for it.
/// </summary>
public interface IAudioMachine {
    /// <summary>Gets the configured output rate in frames per emulated second, or 0 while no consumer requested
    /// audio (in which case <see cref="ReadSamples"/> always returns 0).</summary>
    int SampleRate { get; }

    /// <summary>Drains buffered audio into <paramref name="destination"/> as interleaved left/right signed 16-bit
    /// samples, oldest first. Always returns 0 while <see cref="SampleRate"/> is 0.</summary>
    /// <param name="destination">The span to fill.</param>
    /// <returns>The number of samples written (always even — two per stereo frame).</returns>
    int ReadSamples(Span<short> destination);
}
