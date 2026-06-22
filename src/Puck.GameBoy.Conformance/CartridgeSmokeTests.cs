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
        ];

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
