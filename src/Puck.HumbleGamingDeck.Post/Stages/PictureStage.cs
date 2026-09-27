namespace Puck.HumbleGamingDeck.Post;

/// <summary>
/// Renders an original fixture picture and compares every pixel with an independent reference renderer. The fixture
/// program waits out the PPU's warm-up, writes both palettes, fills nametable 0 with tiles that step along the diagonal
/// and its attribute table with each byte's own index, copies three sprites into object memory through OAM DMA (one in
/// front, one behind the background, one flipped both ways), enables rendering, and counts sprite 0 hits. The reference
/// renderer maps each pixel straight from the same tables with no pipeline, shifter, or evaluation, so the two agree only
/// if the PPU fetches, shifts, prioritizes, and flips correctly.
/// </summary>
internal sealed class PictureStage : IPostStage<PostContext> {
    private const int Frames = 12;
    private const ushort HitCounter = 0x0010;

    private static ReadOnlySpan<byte> Palette => [
        0x0F, 0x16, 0x27, 0x18, 0x0F, 0x1A, 0x2C, 0x12, 0x0F, 0x30, 0x21, 0x11, 0x0F, 0x05, 0x15, 0x25,
        0x0F, 0x14, 0x24, 0x34, 0x0F, 0x19, 0x29, 0x39, 0x0F, 0x13, 0x23, 0x33, 0x0F, 0x06, 0x16, 0x26,
    ];
    private static ReadOnlySpan<byte> Sprites => [
        0x40, 3, 0x01, 0x50,
        0x60, 2, 0x22, 0x90,
        0x80, 1, 0xC3, 0xA0,
    ];

    /// <inheritdoc/>
    public string Name => "ppu-picture";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var chr = CreatePatterns();
        using var instance = HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(
            cartridge: HgdCartridge.Load(image: CreateProgram().Build(chr: chr))));
        var machine = instance.Machine;

        machine.RunCycles(masterTicks: (Frames * 357_366UL));

        var frame = machine.Ppu.Frame;
        var mismatches = 0;
        var first = "";

        for (var y = 0; (y < HgdPpu.Height); ++y) {
            for (var x = 0; (x < HgdPpu.Width); ++x) {
                var expected = ReferencePixel(chr: chr, x: x, y: y);
                var actual = frame[((y * HgdPpu.Width) + x)];

                if (actual != expected) {
                    if (mismatches == 0) {
                        first = $"first at ({x},{y}): {actual:X3} versus {expected:X3}";
                    }
                    ++mismatches;
                }
            }
        }
        if (mismatches != 0) {
            return PostStageOutcome.Fail(detail: $"{mismatches} of {(HgdPpu.Width * HgdPpu.Height)} pixels differ from the reference; {first}");
        }

        var hits = machine.Bus.Peek(address: HitCounter);

        if (hits == 0) {
            return PostStageOutcome.Fail(detail: "the picture matched but no sprite 0 hit was ever observed through $2002");
        }

        return PostStageOutcome.Pass(detail: $"all {(HgdPpu.Width * HgdPpu.Height)} pixels match the reference renderer after {Frames} frames (background, attributes, palettes, front, behind, and flipped sprites); {hits} sprite 0 hits counted");
    }

    private static DeckProgram CreateProgram() {
        var program = new DeckProgram();

        program.Bytes(0x78, 0xD8).Op(opcode: 0xA2, operand: ((byte)0xFF)).Bytes(0x9A)
            .Op(opcode: 0xA9, operand: ((byte)0x00)).Op(address: 0x2000, opcode: 0x8D).Op(address: 0x2001, opcode: 0x8D)
            .Label(name: "vblank1").Op(address: 0x2002, opcode: 0x2C).Branch(label: "vblank1", opcode: 0x10)
            .Label(name: "vblank2").Op(address: 0x2002, opcode: 0x2C).Branch(label: "vblank2", opcode: 0x10)
            // Palettes from the table at "palette".
            .Op(opcode: 0xA9, operand: ((byte)0x3F)).Op(address: 0x2006, opcode: 0x8D)
            .Op(opcode: 0xA9, operand: ((byte)0x00)).Op(address: 0x2006, opcode: 0x8D)
            .Op(opcode: 0xA2, operand: ((byte)0x00))
            .Label(name: "palette-loop").Jump(label: "palette", opcode: 0xBD).Op(address: 0x2007, opcode: 0x8D)
            .Bytes(0xE8).Op(opcode: 0xE0, operand: ((byte)32)).Branch(label: "palette-loop", opcode: 0xD0)
            // Nametable 0: tile (column + row) AND 3.
            .Op(opcode: 0xA9, operand: ((byte)0x20)).Op(address: 0x2006, opcode: 0x8D)
            .Op(opcode: 0xA9, operand: ((byte)0x00)).Op(address: 0x2006, opcode: 0x8D)
            .Op(opcode: 0xA0, operand: ((byte)0x00))
            .Label(name: "row").Op(opcode: 0x84, operand: ((byte)0x00)).Op(opcode: 0xA2, operand: ((byte)0x00))
            .Label(name: "column").Bytes(0x8A, 0x18).Op(opcode: 0x65, operand: ((byte)0x00)).Op(opcode: 0x29, operand: ((byte)0x03))
            .Op(address: 0x2007, opcode: 0x8D).Bytes(0xE8).Op(opcode: 0xE0, operand: ((byte)32)).Branch(label: "column", opcode: 0xD0)
            .Bytes(0xC8).Op(opcode: 0xC0, operand: ((byte)30)).Branch(label: "row", opcode: 0xD0)
            // Attribute table 0: each byte its own index.
            .Op(opcode: 0xA2, operand: ((byte)0x00))
            .Label(name: "attribute").Bytes(0x8A).Op(address: 0x2007, opcode: 0x8D).Bytes(0xE8)
            .Op(opcode: 0xE0, operand: ((byte)64)).Branch(label: "attribute", opcode: 0xD0)
            // Page 2 all $FF, then the sprites from the table at "sprites", then OAM DMA from page 2.
            .Op(opcode: 0xA9, operand: ((byte)0xFF)).Op(opcode: 0xA2, operand: ((byte)0x00))
            .Label(name: "fill").Op(address: 0x0200, opcode: 0x9D).Bytes(0xE8).Branch(label: "fill", opcode: 0xD0)
            .Op(opcode: 0xA2, operand: ((byte)0x00))
            .Label(name: "sprite-loop").Jump(label: "sprites", opcode: 0xBD).Op(address: 0x0200, opcode: 0x9D)
            .Bytes(0xE8).Op(opcode: 0xE0, operand: ((byte)Sprites.Length)).Branch(label: "sprite-loop", opcode: 0xD0)
            .Op(opcode: 0xA9, operand: ((byte)0x00)).Op(address: 0x2003, opcode: 0x8D)
            .Op(opcode: 0xA9, operand: ((byte)0x02)).Op(address: 0x4014, opcode: 0x8D)
            // Scroll 0, pattern table 0 for both layers, 8x8 sprites, rendering on with the left column shown.
            .Op(opcode: 0xA9, operand: ((byte)0x00)).Op(address: 0x2005, opcode: 0x8D).Op(address: 0x2005, opcode: 0x8D)
            .Op(address: 0x2000, opcode: 0x8D).Op(opcode: 0xA9, operand: ((byte)0x1E)).Op(address: 0x2001, opcode: 0x8D)
            // Count one sprite 0 hit per frame: wait for the flag, count it, wait for the pre-render line to clear it.
            .Label(name: "wait-hit").Op(address: 0x2002, opcode: 0x2C).Branch(label: "wait-hit", opcode: 0x50)
            .Op(opcode: 0xE6, operand: ((byte)HitCounter))
            .Label(name: "wait-clear").Op(address: 0x2002, opcode: 0x2C).Branch(label: "wait-clear", opcode: 0x70)
            .Jump(label: "wait-hit", opcode: 0x4C)
            .Label(name: "palette").Bytes(Palette)
            .Label(name: "sprites").Bytes(Sprites);

        return program;
    }
    // Tile 0 is empty, tile 1 asymmetric in both axes so flips are visible, tile 2 solid colour 1, tile 3 a checker of
    // all four colours.
    private static byte[] CreatePatterns() {
        var chr = new byte[8192];

        for (var row = 0; (row < 8); ++row) {
            chr[(16 + row)] = ((byte)(0x80 >> row));
            chr[((16 + 8) + row)] = ((byte)((row < 4) ? 0xF0 : 0x00));
            chr[(32 + row)] = 0xFF;
            chr[(48 + row)] = 0xAA;
            chr[((48 + 8) + row)] = 0xCC;
        }

        return chr;
    }
    private static int PatternBit(ReadOnlySpan<byte> chr, int tile, int row, int column) =>
        ((chr[((tile * 16) + row)] >> (7 - column)) & 1) | (((chr[(((tile * 16) + 8) + row)] >> (7 - column)) & 1) << 1);
    private static ushort ReferencePixel(ReadOnlySpan<byte> chr, int x, int y) {
        var tile = ((x >> 3) + (y >> 3)) & 3;
        var pattern = PatternBit(chr: chr, column: x & 7, row: y & 7, tile: tile);
        var attribute = (((y >> 5) * 8) + (x >> 5));
        var quadrant = ((((y & 31) >> 4) * 4) + (((x & 31) >> 4) * 2));
        var background = ((pattern == 0) ? 0 : ((((attribute >> quadrant) & 3) * 4) + pattern));
        var index = background;

        for (var sprite = 0; (sprite < Sprites.Length); sprite += 4) {
            var row = (y - (Sprites[sprite] + 1));
            var column = (x - Sprites[(sprite + 3)]);
            var attributes = Sprites[(sprite + 2)];

            if ((row < 0) || (row > 7) || (column < 0) || (column > 7)) {
                continue;
            }
            if ((attributes & 0x80) != 0) {
                row = (7 - row);
            }
            if ((attributes & 0x40) != 0) {
                column = (7 - column);
            }

            var spritePattern = PatternBit(chr: chr, column: column, row: row, tile: Sprites[(sprite + 1)]);

            if (spritePattern == 0) {
                continue;
            }
            if ((background == 0) || ((attributes & 0x20) == 0)) {
                index = ((0x10 + ((attributes & 3) * 4)) + spritePattern);
            }

            break;
        }

        // Palette entry 0 of every sub-palette shows the backdrop at $3F00.
        return Palette[(((index & 3) == 0) ? 0 : index)];
    }
}
