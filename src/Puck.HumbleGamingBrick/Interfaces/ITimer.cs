namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The divider/timer block (DIV, TIMA, TMA, TAC). A free-running 16-bit counter drives DIV from its high byte and
/// TIMA from a selected tap; an overflow reloads TIMA from TMA and requests the timer interrupt.
/// </summary>
public interface ITimer : IClockedComponent {
    /// <summary>Gets the free-running 16-bit internal counter whose bits drive DIV and the timer taps.</summary>
    int InternalCounter { get; }
    /// <summary>Seeds the internal counter directly — for the post-boot phase and the speed-switch reset — without
    /// the zero-clear a DIV write performs.</summary>
    void SetInternalCounter(ushort value);
    /// <summary>Reads a timer register (0xFF04-0xFF07).</summary>
    byte ReadRegister(ushort address);
    /// <summary>Writes a timer register (0xFF04-0xFF07).</summary>
    void WriteRegister(ushort address, byte value);
}
