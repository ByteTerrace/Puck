namespace Puck.HumbleGamingDeck;

/// <summary>A cartridge board's bus transactions, edge observations, and complete mutable state.</summary>
public interface IHgdMapper : ISnapshotable {
    /// <summary>Gets the decoded board header.</summary>
    HgdCartridgeHeader Header {
        get;
    }
    /// <summary>Gets the board's active-high interrupt request.</summary>
    bool Irq {
        get;
    }

    /// <summary>Reads the cartridge CPU window.</summary>
    /// <param name="address">The CPU address, from $4020 through $FFFF.</param>
    /// <param name="openBus">The previously driven CPU data bus.</param>
    /// <returns>The value driven by the board, or open bus when it drives nothing.</returns>
    byte CpuRead(ushort address, byte openBus);
    /// <summary>Writes the cartridge CPU window.</summary>
    /// <param name="address">The CPU address.</param>
    /// <param name="value">The driven data byte.</param>
    void CpuWrite(ushort address, byte value);
    /// <summary>Peeks without changing board state.</summary>
    /// <param name="address">The CPU address.</param>
    /// <param name="openBus">The undriven value.</param>
    /// <returns>The current mapped byte.</returns>
    byte CpuPeek(ushort address, byte openBus);
    /// <summary>Reads pattern memory for the future PPU.</summary>
    /// <param name="address">The PPU pattern address, below $2000.</param>
    /// <returns>The mapped pattern byte.</returns>
    byte PpuRead(ushort address);
    /// <summary>Writes pattern memory for the future PPU.</summary>
    /// <param name="address">The PPU pattern address.</param>
    /// <param name="value">The data byte.</param>
    void PpuWrite(ushort address, byte value);
    /// <summary>Peeks pattern memory without clocking board logic.</summary>
    /// <param name="address">The PPU pattern address.</param>
    /// <returns>The mapped pattern byte.</returns>
    byte PpuPeek(ushort address);
    /// <summary>Observes each PPU address-line transition independently of a memory read.</summary>
    /// <param name="address">The new PPU address.</param>
    /// <param name="masterTick">The transition's master tick.</param>
    void ObservePpuAddress(ushort address, ulong masterTick);
    /// <summary>Observes each CPU M2 edge for board timing.</summary>
    /// <param name="high">The new M2 level.</param>
    /// <param name="masterTick">The edge's master tick.</param>
    void ObserveM2(bool high, ulong masterTick);
}
