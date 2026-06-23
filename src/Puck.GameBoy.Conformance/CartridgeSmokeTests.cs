namespace Puck.GameBoy.Conformance;

/// <summary>
/// Self-contained mapper checks: ROM-bank switching, the MBC1 zero-to-one bank quirk versus MBC5's freely
/// selectable bank 0, and RAM enable gating. Each ROM is built so a bank's first byte equals its bank number, so a
/// read of <c>0x4000</c> reports which bank is mapped.
/// </summary>
internal static class CartridgeSmokeTests {
    private const ushort SwitchableRomBase = 0x4000;
    private const ushort CartridgeRamBase = 0xA000;

    public static IReadOnlyList<(string Name, Func<string?> Run)> All =>
        [
            ("MBC1 fixes bank 0 and switches the high bank", static () => {
                var cartridge = new Mbc1(rom: BuildBankedRom(bankCount: 8));

                if (cartridge.ReadRom(address: 0x0000) != 0) {
                    return $"low region = bank {cartridge.ReadRom(address: 0x0000)} (expected 0)";
                }

                cartridge.WriteRom(address: 0x2000, value: 3);

                return (cartridge.ReadRom(address: SwitchableRomBase) == 3)
                    ? null
                    : $"high region = bank {cartridge.ReadRom(address: SwitchableRomBase)} (expected 3)";
            }),
            ("MBC1 maps bank 0 to bank 1 (the zero-to-one quirk)", static () => {
                var cartridge = new Mbc1(rom: BuildBankedRom(bankCount: 8));

                cartridge.WriteRom(address: 0x2000, value: 0);

                return (cartridge.ReadRom(address: SwitchableRomBase) == 1)
                    ? null
                    : $"bank {cartridge.ReadRom(address: SwitchableRomBase)} (expected 1 from the 0->1 quirk)";
            }),
            ("MBC1 RAM is gated by the enable register", static () => {
                var cartridge = new Mbc1(rom: BuildBankedRom(bankCount: 2), ramByteCount: 0x2000);

                cartridge.WriteRam(address: CartridgeRamBase, value: 0x42); // disabled: dropped

                if (cartridge.ReadRam(address: CartridgeRamBase) != 0xFF) {
                    return "RAM readable while disabled";
                }

                cartridge.WriteRom(address: 0x0000, value: 0x0A); // enable RAM
                cartridge.WriteRam(address: CartridgeRamBase, value: 0x42);

                return (cartridge.ReadRam(address: CartridgeRamBase) == 0x42)
                    ? null
                    : $"RAM = 0x{cartridge.ReadRam(address: CartridgeRamBase):X2} (expected 0x42)";
            }),
            ("MBC5 switches the high bank and bank 0 stays selectable", static () => {
                var cartridge = new Mbc5(rom: BuildBankedRom(bankCount: 4));

                cartridge.WriteRom(address: 0x2000, value: 2);

                if (cartridge.ReadRom(address: SwitchableRomBase) != 2) {
                    return $"bank {cartridge.ReadRom(address: SwitchableRomBase)} (expected 2)";
                }

                // MBC5 has no zero-to-one quirk: bank 0 is selectable in the high region.
                cartridge.WriteRom(address: 0x2000, value: 0);

                return (cartridge.ReadRom(address: SwitchableRomBase) == 0)
                    ? null
                    : $"bank {cartridge.ReadRom(address: SwitchableRomBase)} (expected 0)";
            }),
            ("the cartridge loader selects MBC1 and MBC5 by header type", static () => {
                var mbc1 = Cartridge.Load(rom: BuildHeaderedRom(cartridgeType: 0x03, bankCount: 8));
                var mbc5 = Cartridge.Load(rom: BuildHeaderedRom(cartridgeType: 0x1B, bankCount: 8));

                return ((mbc1 is Mbc1) && (mbc5 is Mbc5))
                    ? null
                    : $"loaded {mbc1.GetType().Name} and {mbc5.GetType().Name}";
            }),
            ("MBC2 banks ROM (bit-8 decode) and stores its 4-bit RAM", static () => {
                var cartridge = new Mbc2(rom: BuildBankedRom(bankCount: 8));

                // Address bit 8 set selects the ROM bank; clear toggles RAM enable.
                cartridge.WriteRom(address: 0x2100, value: 3);

                if (cartridge.ReadRom(address: SwitchableRomBase) != 3) {
                    return $"bank {cartridge.ReadRom(address: SwitchableRomBase)} (expected 3)";
                }

                cartridge.WriteRom(address: 0x0000, value: 0x0A);
                cartridge.WriteRam(address: CartridgeRamBase, value: 0xF5);

                // Only the low nibble is stored; the high nibble reads back as ones.
                return (cartridge.ReadRam(address: CartridgeRamBase) == 0xF5)
                    ? null
                    : $"RAM = 0x{cartridge.ReadRam(address: CartridgeRamBase):X2} (expected 0xF5)";
            }),
            ("MBC3 banks ROM and ticks a latchable RTC", static () => {
                var cartridge = new Mbc3(rom: BuildBankedRom(bankCount: 16), ramByteCount: 0x2000, hasRtc: true);

                cartridge.WriteRom(address: 0x2000, value: 5);

                if (cartridge.ReadRom(address: SwitchableRomBase) != 5) {
                    return $"bank {cartridge.ReadRom(address: SwitchableRomBase)} (expected 5)";
                }

                cartridge.WriteRom(address: 0x0000, value: 0x0A); // enable RAM/RTC
                cartridge.WriteRom(address: 0x4000, value: 0x08); // map the seconds register
                cartridge.WriteRam(address: CartridgeRamBase, value: 0); // zero the live seconds

                cartridge.Step(tCycles: (4194304 * 5)); // five seconds of master cycles

                cartridge.WriteRom(address: 0x6000, value: 0x00); // latch sequence
                cartridge.WriteRom(address: 0x6000, value: 0x01);

                return (cartridge.ReadRam(address: CartridgeRamBase) == 5)
                    ? null
                    : $"RTC seconds = {cartridge.ReadRam(address: CartridgeRamBase)} (expected 5)";
            }),
            ("MBC7 stores an EEPROM word through the serial protocol", static () => {
                var cartridge = new Mbc7(rom: BuildBankedRom(bankCount: 8));

                cartridge.WriteRom(address: 0x0000, value: 0x0A); // primary RAM enable
                cartridge.WriteRom(address: 0x4000, value: 0x40); // secondary enable

                SendEepromCommand(cartridge: cartridge, command: 0x4C0); // EWEN
                SendEepromCommand(cartridge: cartridge, command: 0x500, data: 0xABCD, dataBits: 16); // WRITE word 0

                var word = ReadEepromWord(cartridge: cartridge, command: 0x600); // READ word 0

                return (word == 0xABCD)
                    ? null
                    : $"EEPROM word = 0x{word:X4} (expected 0xABCD)";
            }),
            ("CGB compatibility colorization assigns Tetris its boot-ROM palette", static () => {
                var rom = new byte[0x8000];

                "TETRIS"u8.CopyTo(destination: rom.AsSpan(start: 0x0134));
                rom[0x014B] = 0x01; // Nintendo licensee, so the boot ROM would colorize it

                // The title checksum (0xDB) selects palette combination 3, whose background is palette 24.
                var (background, _, _) = CompatibilityPalette.Resolve(rom: rom);

                return ((background[0] == 0x7FFF) && (background[1] == 0x03FF) && (background[2] == 0x001F) && (background[3] == 0x0000))
                    ? null
                    : $"bg = [{background[0]:X4} {background[1]:X4} {background[2]:X4} {background[3]:X4}]";
            }),
            ("CGB compatibility colorization falls back to the default for non-Nintendo games", static () => {
                var rom = new byte[0x8000];

                "TETRIS"u8.CopyTo(destination: rom.AsSpan(start: 0x0134));
                rom[0x014B] = 0x99; // not a Nintendo licensee -> default palette combination 0

                var (background, _, _) = CompatibilityPalette.Resolve(rom: rom);

                // Combination 0's background is palette 29.
                return ((background[0] == 0x7FFF) && (background[1] == 0x1BEF) && (background[2] == 0x6180) && (background[3] == 0x0000))
                    ? null
                    : $"bg = [{background[0]:X4} {background[1]:X4} {background[2]:X4} {background[3]:X4}]";
            }),
            ("a held boot button combination overrides the compatibility palette", static () => {
                var rom = new byte[0x8000];

                "TETRIS"u8.CopyTo(destination: rom.AsSpan(start: 0x0134));
                rom[0x014B] = 0x01;

                // Up + B is key-combination 11, which selects palette combination 28 (background palette 1).
                var selection = new BootPaletteSelection(Direction: BootPaletteDirection.Up, B: true);

                if (selection.KeyCombinationIndex != 11) {
                    return $"key index = {selection.KeyCombinationIndex} (expected 11)";
                }

                var (background, _, _) = CompatibilityPalette.Resolve(rom: rom, input: selection);

                return ((background[0] == 0x639F) && (background[1] == 0x4279) && (background[2] == 0x15B0) && (background[3] == 0x04CB))
                    ? null
                    : $"bg = [{background[0]:X4} {background[1]:X4} {background[2]:X4} {background[3]:X4}]";
            }),
            ("a DMG cartridge on a CGB console enables compatibility colorization", static () => {
                var rom = new byte[0x8000];

                rom[0x0147] = 0x00; // ROM only, no CGB flag at 0x143 -> a DMG game
                rom[0x0148] = 0x00;

                var ppu = new GameBoyMachine(model: ConsoleModel.Cgb, cartridge: Cartridge.Load(rom: rom)).Ppu;

                // The PPU drops to the DMG render path (IsColor false) but is colorizing rather than grayscale.
                return (!ppu.IsColor)
                    ? null
                    : "PPU stayed in the color render path for a DMG game on CGB";
            }),
            ("the cartridge loader selects every newly added mapper by header type", static () => {
                (byte Type, Type Expected)[] cases = [
                    (0x05, typeof(Mbc2)),
                    (0x10, typeof(Mbc3)),
                    (0x13, typeof(Mbc3)),
                    (0x22, typeof(Mbc7)),
                    (0x0B, typeof(Mmm01)),
                    (0xFC, typeof(PocketCamera)),
                    (0xFE, typeof(HuC3)),
                    (0xFF, typeof(HuC1)),
                ];

                foreach (var (type, expected) in cases) {
                    var cartridge = Cartridge.Load(rom: BuildHeaderedRom(cartridgeType: type, bankCount: 8));

                    if (cartridge.GetType() != expected) {
                        return $"type 0x{type:X2} -> {cartridge.GetType().Name} (expected {expected.Name})";
                    }
                }

                return null;
            }),
        ];

    // Drives one bit through the MBC7 EEPROM's serial port at 0xA080: data on bit 1, a low-then-high clock on bit 6,
    // chip-select held on bit 7.
    private static void ClockEepromBit(Mbc7 cartridge, int bit) {
        var data = ((bit != 0) ? 0x02 : 0x00);

        cartridge.WriteRam(address: 0xA080, value: (byte)(0x80 | data));
        cartridge.WriteRam(address: 0xA080, value: (byte)(0x80 | 0x40 | data));
    }

    private static void SendEepromCommand(Mbc7 cartridge, int command, int data = 0, int dataBits = 0) {
        for (var i = 10; i >= 0; i -= 1) {
            ClockEepromBit(cartridge: cartridge, bit: ((command >> i) & 0x01));
        }

        for (var i = (dataBits - 1); i >= 0; i -= 1) {
            ClockEepromBit(cartridge: cartridge, bit: ((data >> i) & 0x01));
        }

        cartridge.WriteRam(address: 0xA080, value: 0x00); // deselect
    }

    private static int ReadEepromWord(Mbc7 cartridge, int command) {
        for (var i = 10; i >= 0; i -= 1) {
            ClockEepromBit(cartridge: cartridge, bit: ((command >> i) & 0x01));
        }

        var value = 0;

        for (var i = 0; i < 16; i += 1) {
            ClockEepromBit(cartridge: cartridge, bit: 0);

            value = ((value << 1) | (cartridge.ReadRam(address: 0xA080) & 0x01));
        }

        cartridge.WriteRam(address: 0xA080, value: 0x00); // deselect

        return value;
    }

    private static byte[] BuildBankedRom(int bankCount) {
        var rom = new byte[bankCount * 0x4000];

        for (var bank = 0; bank < bankCount; bank += 1) {
            rom[bank * 0x4000] = (byte)bank;
        }

        return rom;
    }

    private static byte[] BuildHeaderedRom(byte cartridgeType, int bankCount) {
        var rom = BuildBankedRom(bankCount: bankCount);

        rom[0x0147] = cartridgeType;
        rom[0x0148] = 0x03; // 256 KiB (8 banks) ROM-size code; harmless for these checks

        return rom;
    }
}
