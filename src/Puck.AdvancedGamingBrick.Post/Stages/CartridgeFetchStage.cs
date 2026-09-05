namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Checks wide ROM fetches against byte reads at image, burst-page and sensor-overlay boundaries.</summary>
internal sealed class CartridgeFetchStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "cartridge-fetch";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        foreach (var gameCode in new[] { "TEST", "U3IJ", "KHPJ" }) {
            foreach (var length in new[] { 0, 1, 0xC5, 0x8501, 0x20001 }) {
                var rom = new byte[length];

                for (var index = 0; (index < length); ++index) {
                    rom[index] = ((byte)((index * 37) & 255));
                }

                if (length >= 0xB0) {
                    for (var index = 0; (index < 4); ++index) {
                        rom[(0xAC + index)] = ((byte)gameCode[index]);
                    }
                }

                var cartridge = new AgbCartridge(rom: rom);
                cartridge.WriteGpio(register: 0xC8u, value: 1);
                cartridge.WriteGpio(register: 0xC6u, value: 15);
                cartridge.WriteGpio(register: 0xC4u, value: 10);

                foreach (var address in ((ReadOnlySpan<uint>)[0, 0xC2, 0xC4, 0xC6, 0xC8, 0xCA,
                    0x81FE, 0x8200, 0x8300, 0x8400, 0x8500, 0x8502, 0x1FFFC, 0x1FFFE, 0x20000, 0x20002])) {
                    var expected = ((ushort)(cartridge.ReadRom(offset: address)
                        | (cartridge.ReadRom(offset: (address + 1u)) << 8)));
                    var actual = cartridge.ReadRomBurst(address: address, sequential: false);

                    if (actual != expected) {
                        return PostStageOutcome.Fail(detail: $"{gameCode}, length={length:X}, address={address:X}: {actual:X4} != {expected:X4}");
                    }
                }
            }
        }

        return PostStageOutcome.Pass(detail: "240 halfwords matched byte reads across empty/odd ROM images, GPIO/tilt overlays and 128 KiB page boundaries");
    }
}
