namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The MBC7's two-axis accelerometer readout — the DI seam through which the outside world (recorded per-segment host
/// input, a fixed test reading) reaches the emulated machine, on the same <c>TryAdd</c> precedent as
/// <see cref="ICameraSensor"/>. Unlike the camera (a live poll latched at the instant a ROM triggers a capture), tilt
/// is bidirectional: <see cref="SetTilt"/> records one host-supplied reading per step (mirroring <c>IJoypad</c>'s
/// held-buttons shape) and <see cref="Read"/> is the cartridge's side of the same erase-then-latch protocol
/// (<see cref="Mbc7Cartridge"/>) — so a value set mid-segment stays constant for the rest of that segment and any
/// mid-segment latch replays bit-identically.
/// </summary>
public interface ITiltSensor {
    /// <summary>Reads the current accelerometer reading, raw 16-bit hardware units centered near <c>0x81D0</c> (the
    /// same units <see cref="Mbc7Cartridge"/>'s X/Y latch registers hold).</summary>
    /// <param name="x">The horizontal-axis reading.</param>
    /// <param name="y">The vertical-axis reading.</param>
    void Read(out int x, out int y);

    /// <summary>Records one host-supplied tilt sample, each axis -1..1, held constant until the next call — the
    /// per-segment input a queued host's <c>ApplyInput</c> feeds every step. A no-op on an implementation that does
    /// not accept host input (a fixed test double).</summary>
    /// <param name="x">The horizontal tilt, -1..1.</param>
    /// <param name="y">The vertical tilt, -1..1.</param>
    void SetTilt(float x, float y);
}
