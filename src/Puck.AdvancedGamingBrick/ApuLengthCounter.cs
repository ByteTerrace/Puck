namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The GB-style length counter — one hardware block all three legacy PSG channels carry an instance of. It holds the
/// remaining length and NRx4's enable bit; the channels differ only in the reload width (64 steps for pulse and noise,
/// 256 for wave), which each channel writes into <see cref="Counter"/> itself.
/// </summary>
internal struct ApuLengthCounter {
    /// <summary>The remaining length in 256&#160;Hz steps.</summary>
    public int Counter;
    /// <summary>Whether the counter gates the channel (NRx4 bit 6).</summary>
    public bool Enabled;

    /// <summary>Clocks the counter (256&#160;Hz).</summary>
    /// <returns>Whether the counter just expired, which silences the channel.</returns>
    public bool Clock() =>
        (Enabled &&
        (Counter > 0) &&
        (--Counter == 0));
}
