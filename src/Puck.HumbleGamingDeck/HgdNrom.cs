namespace Puck.HumbleGamingDeck;

/// <summary>NROM's fixed PRG/CHR windows, optional PRG memory, and header-selected nametable wiring, including the
/// four-screen variant's own 2 KiB of nametable RAM.</summary>
public sealed class HgdNrom : HgdBoard {
    /// <summary>Initializes a new instance of the <see cref="HgdNrom"/> class and installs a trainer at $7000 when present.</summary>
    /// <param name="cartridge">The validated, immutable NROM image.</param>
    /// <exception cref="ArgumentNullException">The cartridge is null.</exception>
    public HgdNrom(HgdCartridge cartridge)
        : base(cartridge: cartridge) {
        // A 16 KiB image repeats through $8000-$FFFF; both sizes are whole 8 KiB windows.
        for (var window = 0; (window < 4); ++window) {
            Memory.MapPrg(
                count: 1,
                firstWindow: window,
                offset: ((window << 13) & (Memory.PrgLength - 1))
            );
        }
        Memory.MapChr(
            count: 8,
            firstWindow: 0,
            offset: 0
        );
    }

    /// <inheritdoc/>
    public override bool Irq => false;
    /// <inheritdoc/>
    public override bool TimesOnEdges => false;

    /// <inheritdoc/>
    public override byte CpuPeek(ushort address, byte openBus) {
        if (address >= 0x8000) {
            return Memory.ReadPrg(address: address);
        }

        var size = Memory.PrgRamLength;

        return (((address < 0x6000) || (size == 0)) ? openBus : Memory.ReadPrgRam(offset: address & (size - 1)));
    }
    /// <inheritdoc/>
    public override void CpuWrite(ushort address, byte value) {
        var size = Memory.PrgRamLength;

        if ((address >= 0x6000) && (address < 0x8000) && (size != 0)) {
            Memory.WritePrgRam(offset: address & (size - 1), value: value);
        }
    }
    /// <inheritdoc/>
    public override void ObservePpuAddress(ushort address, ulong masterTick) { }
    /// <inheritdoc/>
    public override void ObserveM2(bool high, ulong masterHalfTick) { }
    /// <inheritdoc/>
    public override void SaveState(StateWriter writer) =>
        Memory.Transfer(transfer: new StateSaveTransfer(writer: writer));
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain all declared board RAM.</exception>
    public override void LoadState(StateReader reader) =>
        Memory.Transfer(transfer: new StateLoadTransfer(reader: reader));
}
