namespace Puck.HumbleGamingBrick;

/// <summary>
/// A cartridge with no mapper: a flat 32&#160;KiB ROM directly addressed across the whole ROM region, optionally with a
/// single unbanked, always-enabled RAM window. There are no control registers, so the only snapshot state is the RAM.
/// </summary>
public sealed class RomOnlyCartridge : CartridgeBase {
    /// <summary>Creates a ROM-only cartridge.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header.</param>
    public RomOnlyCartridge(byte[] rom, CartridgeHeader header)
        : base(rom: rom, header: header) { }

    /// <inheritdoc/>
    protected override bool RamAccessible =>
        Header.HasRam;

    /// <inheritdoc/>
    public override void WriteControl(ushort address, byte value) {
        // A flat ROM has no mapper registers; writes to the ROM region are inert.
    }

    /// <inheritdoc/>
    protected override int MapRomOffset(ushort address) =>
        address;
    /// <inheritdoc/>
    protected override int MapRamOffset(ushort address) =>
        (address - MemoryMap.ExternalRamStart);
    /// <inheritdoc/>
    protected override void SaveRegisters(StateWriter writer) {
        // No registers.
    }
    /// <inheritdoc/>
    protected override void LoadRegisters(StateReader reader) {
        // No registers.
    }
}
