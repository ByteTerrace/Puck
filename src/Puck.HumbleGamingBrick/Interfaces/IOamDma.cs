namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The OAM DMA engine: a write to 0xFF46 copies a 0xNN00 page into object attribute memory one byte per machine
/// cycle, locking OAM from the CPU and PPU while the copy is in flight.
/// </summary>
public interface IOamDma : IClockedComponent {
    /// <summary>Gets whether a transfer is scheduled or running.</summary>
    bool IsActive { get; }
    /// <summary>Gets whether object attribute memory is currently locked by an in-flight transfer.</summary>
    bool IsOamLocked { get; }
    /// <summary>Gets the source page most recently written to 0xFF46.</summary>
    byte Page { get; }
    /// <summary>Gets or sets the source reader the bus wires in — reads a source byte (untimed) for the transfer.
    /// Wired post-construction so the engine never depends on the bus type, avoiding a construction cycle.</summary>
    Func<ushort, byte> ReadSource { get; set; }
    /// <summary>Starts (or restarts) a transfer from the given source page.</summary>
    /// <param name="page">The high byte of the source address; the copy reads <c>page × 0x100</c> onward.</param>
    void Start(byte page);
}
