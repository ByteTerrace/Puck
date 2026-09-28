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
    /// <summary>Gets the battery-backed RAM a save file persists, empty when the board keeps none.</summary>
    Span<byte> BatteryRam {
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
    /// <summary>Reads the PPU window: pattern memory below $2000 and the nametables from $2000, which the board routes to
    /// a page of the console's nametable RAM or to memory of its own.</summary>
    /// <param name="address">The fourteen-bit PPU address, below $3F00.</param>
    /// <param name="nametables">The console's nametable RAM, whose page the board selects.</param>
    /// <returns>The byte the board drives.</returns>
    byte PpuRead(ushort address, HgdNametableRam nametables);
    /// <summary>Writes the PPU window.</summary>
    /// <param name="address">The fourteen-bit PPU address, below $3F00.</param>
    /// <param name="value">The data byte.</param>
    /// <param name="nametables">The console's nametable RAM, whose page the board selects.</param>
    void PpuWrite(ushort address, byte value, HgdNametableRam nametables);
    /// <summary>Reads the PPU window without clocking board logic.</summary>
    /// <param name="address">The fourteen-bit PPU address, below $3F00.</param>
    /// <param name="nametables">The console's nametable RAM, whose page the board selects.</param>
    /// <returns>The byte the board would drive.</returns>
    byte PpuPeek(ushort address, HgdNametableRam nametables);
    /// <summary>Observes each PPU address-line transition independently of a memory read.</summary>
    /// <param name="address">The new PPU address.</param>
    /// <param name="masterTick">The transition's master tick.</param>
    void ObservePpuAddress(ushort address, ulong masterTick);
    /// <summary>Observes each CPU M2 edge for board timing.</summary>
    /// <param name="high">The new M2 level.</param>
    /// <param name="masterHalfTick">The edge's elapsed time in half master ticks.</param>
    void ObserveM2(bool high, ulong masterHalfTick);
}
