namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The audio processing unit as the bus sees it. It owns the sound registers (NR10–NR52) and wave-pattern RAM; the bus
/// forwards reads and writes of that range here. Reads return the register with its unused bits set, and the master
/// control register (NR52) reports the power state and which channels are still sounding.
/// </summary>
public interface IApu {
    /// <summary>Reads one of the audio registers or a byte of wave RAM.</summary>
    /// <param name="address">The address (<c>0xFF10</c>–<c>0xFF3F</c>).</param>
    /// <returns>The value as the CPU observes it, including the bits that read back as set.</returns>
    byte ReadRegister(ushort address);
    /// <summary>Writes one of the audio registers or a byte of wave RAM, applying its side effects — length reloads,
    /// triggers, DAC power, and the master power switch that clears the register file.</summary>
    /// <param name="address">The address (<c>0xFF10</c>–<c>0xFF3F</c>).</param>
    /// <param name="value">The value being written.</param>
    void WriteRegister(ushort address, byte value);
    /// <summary>Reads one of the Color-only PCM output registers (<c>0xFF76</c> PCM12, <c>0xFF77</c> PCM34), which expose
    /// the live four-bit digital output of the four channels, two per byte.</summary>
    /// <param name="address">The register address (<c>0xFF76</c> or <c>0xFF77</c>).</param>
    /// <returns>The packed digital outputs of the two channels the register covers.</returns>
    byte ReadPcm(ushort address);
}
