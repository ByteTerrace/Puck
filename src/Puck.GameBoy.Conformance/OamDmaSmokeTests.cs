namespace Puck.GameBoy.Conformance;

/// <summary>
/// Self-contained OAM DMA checks driving the real <see cref="SystemBus"/>: a write to <c>0xFF46</c> copies 160
/// bytes from the source page into object-attribute memory, the register reads the page back, and the memory is
/// locked (reads return <c>0xFF</c>) while the transfer is in flight.
/// </summary>
internal static class OamDmaSmokeTests {
    private const ushort DmaRegister = 0xFF46;
    private const ushort SourceBase = 0xC000;
    private const byte SourcePage = 0xC0;
    private const ushort OamBase = 0xFE00;
    private const int OamLength = 0xA0;

    public static IReadOnlyList<(string Name, Func<string?> Run)> All =>
        [
            ("OAM DMA copies 160 bytes from the source page", static () => {
                var bus = MakeBusWithSource();

                bus.WriteByte(address: DmaRegister, value: SourcePage);

                // One setup cycle plus 160 transfer cycles; step generously past completion.
                for (var index = 0; index < 200; index += 1) {
                    bus.InternalCycle();
                }

                for (var index = 0; index < OamLength; index += 1) {
                    var expected = (byte)(index ^ 0x5A);
                    var actual = bus.ReadByte(address: (ushort)(OamBase + index));

                    if (actual != expected) {
                        return $"OAM[{index}]=0x{actual:X2} (expected 0x{expected:X2})";
                    }
                }

                return null;
            }),
            ("OAM is locked (reads 0xFF) during the transfer", static () => {
                var bus = MakeBusWithSource();

                bus.WriteByte(address: DmaRegister, value: SourcePage);
                bus.InternalCycle();
                bus.InternalCycle();

                var duringTransfer = bus.ReadByte(address: OamBase);

                return (duringTransfer == 0xFF)
                    ? null
                    : $"OAM read mid-transfer = 0x{duringTransfer:X2} (expected 0xFF lock)";
            }),
            ("DMA register reads back the source page", static () => {
                var bus = MakeBusWithSource();

                bus.WriteByte(address: DmaRegister, value: SourcePage);

                var readback = bus.ReadByte(address: DmaRegister);

                return (readback == SourcePage)
                    ? null
                    : $"0xFF46 read = 0x{readback:X2} (expected 0x{SourcePage:X2})";
            }),
            ("OAM stays readable through the setup cycle, locks once copying", static () => {
                var bus = MakeBusWithSource();

                bus.WriteByte(address: OamBase, value: 0x42);
                bus.WriteByte(address: DmaRegister, value: SourcePage);

                var afterStart = bus.ReadByte(address: OamBase);

                bus.InternalCycle();

                var duringSetup = bus.ReadByte(address: OamBase);

                bus.InternalCycle();

                var whileCopying = bus.ReadByte(address: OamBase);

                return ((afterStart == 0x42) && (duringSetup == 0x42) && (whileCopying == 0xFF))
                    ? null
                    : $"afterStart=0x{afterStart:X2}, duringSetup=0x{duringSetup:X2}, whileCopying=0x{whileCopying:X2} (expected 0x42,0x42,0xFF)";
            }),
            ("OAM unlocks exactly one cycle after the final byte copy", static () => {
                var bus = MakeBusWithSource();

                bus.WriteByte(address: DmaRegister, value: SourcePage);

                // 1 setup cycle + 160 copy cycles: OAM is still locked on the 161st step (the last copy)...
                for (var index = 0; index < 161; index += 1) {
                    bus.InternalCycle();
                }

                var atLastCopy = bus.ReadByte(address: OamBase);

                // ...and becomes readable on the very next cycle.
                bus.InternalCycle();

                var afterEnd = bus.ReadByte(address: OamBase);

                return ((atLastCopy == 0xFF) && (afterEnd == (byte)(0 ^ 0x5A)))
                    ? null
                    : $"atLastCopy=0x{atLastCopy:X2} (want 0xFF), afterEnd=0x{afterEnd:X2} (want 0x5A)";
            }),
            ("DMA from an echo source page reads work RAM, not the OAM lock", static () => {
                var bus = new SystemBus(
                    model: ConsoleModel.Dmg,
                    cartridge: new RomOnlyCartridge(rom: new byte[0x8000])
                );

                // Source page 0xFE aliases the work-RAM echo: 0xFE00 maps to work RAM at 0xDE00 (minus 0x2000).
                bus.WriteByte(address: 0xDE00, value: 0x33);
                bus.WriteByte(address: DmaRegister, value: 0xFE);

                for (var index = 0; index < 200; index += 1) {
                    bus.InternalCycle();
                }

                var copied = bus.ReadByte(address: OamBase);

                return (copied == 0x33)
                    ? null
                    : $"OAM[0]=0x{copied:X2} (expected 0x33 echoed work RAM, not 0xFF lock)";
            }),
        ];

    private static SystemBus MakeBusWithSource() {
        var bus = new SystemBus(
            model: ConsoleModel.Dmg,
            cartridge: new RomOnlyCartridge(rom: new byte[0x8000])
        );

        // Seed the work-RAM source page with a recognizable pattern.
        for (var index = 0; index < OamLength; index += 1) {
            bus.WriteByte(
                address: (ushort)(SourceBase + index),
                value: (byte)(index ^ 0x5A)
            );
        }

        return bus;
    }
}
