namespace Puck.HumbleGamingBrick.Ares;

/// <summary>
/// A placeholder APU that participates in the bus as open bus (reads contribute nothing; writes are dropped). The
/// full ares APU port is deferred — the PPU-timing (mealybug) conformance work does not depend on audio. Wiring it
/// in now keeps the bus routing complete and lets sound-register accesses behave benignly.
/// </summary>
public sealed class AresApu : IAresIo {
    /// <inheritdoc/>
    public byte ReadIo(int cycle, ushort address, byte data) =>
        data;

    /// <inheritdoc/>
    public void WriteIo(int cycle, ushort address, byte data) {
        // Deferred: the APU does not yet model sound-register state.
    }
}
