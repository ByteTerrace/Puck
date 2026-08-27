namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The GB-style volume envelope — one hardware block the pulse and noise PSG channels each carry an instance of. It
/// holds the NRx2 image (initial volume, direction, period) plus the live volume and the 64&#160;Hz countdown, and is
/// composed into a channel rather than re-implemented per channel, so the two channels stay bit-locked by construction.
/// </summary>
internal struct ApuEnvelopeUnit {
    /// <summary>The live output volume, 0–15.</summary>
    public int Volume;
    /// <summary>The volume a trigger latches (NRx2 bits 7–4).</summary>
    public int Initial;
    /// <summary>Whether the envelope counts up (NRx2 bit 3).</summary>
    public bool Increase;
    /// <summary>The envelope period in 64&#160;Hz steps; zero leaves the unit idle (NRx2 bits 2–0).</summary>
    public int Period;
    /// <summary>The 64&#160;Hz countdown to the next volume step.</summary>
    public int Timer;

    /// <summary>Reads back NRx2: initial volume, direction, and period (all bits readable).</summary>
    /// <returns>The register image.</returns>
    public readonly byte Read() => ((byte)((Initial << 4) | (Increase
        ? 0x8
        : 0) | Period));
    /// <summary>Applies an NRx2 write.</summary>
    /// <param name="value">The written byte.</param>
    /// <returns>Whether the channel's DAC is powered (any of the upper five bits set).</returns>
    public bool Write(byte value) {
        Initial = (value >> 4) & 0xF;
        Increase = ((value & 0x8) != 0);
        Period = value & 0x7;

        return ((value & 0xF8) != 0);
    }
    /// <summary>Latches the reload volume and period on a channel trigger.</summary>
    public void Trigger() {
        Volume = Initial;
        Timer = Period;
    }
    /// <summary>Clocks the envelope (64&#160;Hz), stepping the volume one unit toward its rail when the period expires.</summary>
    public void Clock() {
        if (Period == 0) {
            return;
        }

        if (--Timer <= 0) {
            Timer = Period;

            if (
                Increase &&
                (Volume < 15)
            ) {
                ++Volume;
            } else if (
                !Increase &&
                (Volume > 0)
            ) {
                --Volume;
            }
        }
    }
}
