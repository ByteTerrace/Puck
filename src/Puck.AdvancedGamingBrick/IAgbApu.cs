namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The audio-processing unit: the four legacy PSG channels (two square, wave, noise) plus the Advanced
/// GamingBrick's two Direct Sound FIFO channels, mixed per SOUNDCNT into a stereo stream. Advanced on the master
/// clock like the other peripherals; it drives a frame sequencer for the PSG envelopes/length/sweep and, when
/// output is configured, resamples the mix into a drainable ring the host empties.
/// </summary>
public interface IAgbApu : IAgbClockedComponent {
    /// <summary>Reads a sound register (control, channel, or wave-RAM, I/O 0x60–0xA7).</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <returns>The register value.</returns>
    ushort ReadRegister(uint offset);
    /// <summary>Writes a sound register. Writes to the FIFO ranges (0xA0–0xA7) enqueue Direct Sound samples.</summary>
    /// <param name="offset">The I/O offset within the 0x04000000 page.</param>
    /// <param name="value">The value to write.</param>
    void WriteRegister(uint offset, ushort value);
    /// <summary>Streams one byte into a Direct Sound FIFO (0 = A, 1 = B). The bus decomposes each 8/16/32-bit write
    /// to the FIFO register windows into the exact bytes it carries and streams them here in write order, so a narrow
    /// write correctly fills only part of the next 32-bit FIFO word.</summary>
    /// <param name="fifo">The Direct Sound FIFO index (0 = A, 1 = B).</param>
    /// <param name="value">The byte to append.</param>
    void WriteFifoByte(int fifo, byte value);
    /// <summary>Notifies the APU that a timer overflowed, advancing whichever Direct Sound FIFO is clocked by it.</summary>
    /// <param name="timer">The timer index (0 or 1) that overflowed.</param>
    void OnTimerOverflow(int timer);
    /// <summary>Returns and clears the request to DMA-refill Direct Sound FIFO A (it has drained to half or less).</summary>
    /// <returns><see langword="true"/> if FIFO A needs refilling.</returns>
    bool ConsumeFifoARefill();
    /// <summary>Returns and clears the request to DMA-refill Direct Sound FIFO B.</summary>
    /// <returns><see langword="true"/> if FIFO B needs refilling.</returns>
    bool ConsumeFifoBRefill();
    /// <summary>Enables host audio output at the given sample rate, sizing the output ring to one emulated second of
    /// stereo frames. When the ring is full the oldest frame is dropped, so an undrained ring holds the newest second. A
    /// rate of zero disables output (the default), so headless conformance runs incur no audio work. Reconfiguration
    /// discards queued samples; draining while disabled returns zero.</summary>
    /// <param name="sampleRate">The host sample rate in Hz, or 0 to disable.</param>
    void ConfigureOutput(int sampleRate);

    /// <summary>Gets the currently configured host output rate in Hz — the read half of <see cref="ConfigureOutput"/> —
    /// or 0 while output is disabled.</summary>
    int SampleRate { get; }

    /// <summary>Drains queued stereo samples (interleaved left/right), oldest first, into
    /// <paramref name="destination"/>. Only whole frames are copied, so an odd trailing slot stays untouched.</summary>
    /// <param name="destination">The buffer to fill.</param>
    /// <returns>The number of samples written (left and right counted separately), which is always even.</returns>
    int DrainSamples(Span<short> destination);
}
