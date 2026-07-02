namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The divider and timer block (DIV/TIMA/TMA/TAC). The bus routes reads and writes of those four registers here; the
/// component owns the 16-bit internal counter whose high byte is DIV, the falling-edge detector that drives TIMA, and
/// the post-overflow reload of TIMA from TMA. It raises the timer interrupt when TIMA reloads.
/// </summary>
public interface ITimer {
    /// <summary>Gets the raw 16-bit internal counter whose high byte is DIV. The APU reads it to clock its frame
    /// sequencer off a falling edge of one counter bit (the DIV-APU event), so that resetting DIV also perturbs the
    /// sequencer exactly as the hardware does.</summary>
    ushort DivCounter { get; }

    /// <summary>Reads one of the timer's four registers.</summary>
    /// <param name="address">The register address (<c>0xFF04</c>–<c>0xFF07</c>).</param>
    /// <returns>The register value as the CPU observes it, including the bits that read back as set.</returns>
    byte ReadRegister(ushort address);
    /// <summary>Writes one of the timer's four registers, applying the register's hardware side effects: a DIV write
    /// resets the internal counter (and may step TIMA through the falling-edge detector), a TAC write can likewise step
    /// TIMA, and a TIMA or TMA write during the post-overflow reload window has its documented quirks.</summary>
    /// <param name="address">The register address (<c>0xFF04</c>–<c>0xFF07</c>).</param>
    /// <param name="value">The value being written.</param>
    void WriteRegister(ushort address, byte value);
}
