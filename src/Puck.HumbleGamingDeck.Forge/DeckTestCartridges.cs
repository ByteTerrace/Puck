namespace Puck.HumbleGamingDeck.Forge;

/// <summary>
/// The original test cartridges the repository ships for the Deck, each an NROM-256 image built by
/// <see cref="DeckProgram"/>, so no world depends on a downloaded ROM. Every image is a pure function of this source:
/// building one twice gives the same bytes.
/// </summary>
public static class DeckTestCartridges {
    // Zero-page cells.
    private const byte Frame = 0x10;
    private const byte BallLeft = 0x22;
    private const byte BallUp = 0x23;
    private const byte BallX = 0x20;
    private const byte BallY = 0x21;
    private const byte Mask = 0x12;
    private const byte Pad = 0x11;
    // The ball's travel: x from 0 to 240 keeps both of its 8-pixel columns on screen, and y from 8 to 190 keeps it
    // above the ground rows, which start at line 208.
    private const byte BallRight = 240;
    private const byte BallBottom = 190;
    private const byte BallTop = 8;
    // Opcodes, by mnemonic and addressing mode.
    private const byte AdcImmediate = 0x69;
    private const byte AndImmediate = 0x29;
    private const byte Bcc = 0x90;
    private const byte Bcs = 0xB0;
    private const byte Beq = 0xF0;
    private const byte BitAbsolute = 0x2C;
    private const byte Bne = 0xD0;
    private const byte Bpl = 0x10;
    private const byte Clc = 0x18;
    private const byte Cld = 0xD8;
    private const byte CmpImmediate = 0xC9;
    private const byte CmpZeroPage = 0xC5;
    private const byte CpxImmediate = 0xE0;
    private const byte CpyImmediate = 0xC0;
    private const byte DecZeroPage = 0xC6;
    private const byte Dex = 0xCA;
    private const byte IncZeroPage = 0xE6;
    private const byte Inx = 0xE8;
    private const byte Iny = 0xC8;
    private const byte Jmp = 0x4C;
    private const byte Jsr = 0x20;
    private const byte LdaAbsolute = 0xAD;
    private const byte LdaAbsoluteX = 0xBD;
    private const byte LdaAbsoluteY = 0xB9;
    private const byte LdaImmediate = 0xA9;
    private const byte LdaZeroPage = 0xA5;
    private const byte LdxImmediate = 0xA2;
    private const byte LdyImmediate = 0xA0;
    private const byte LsrAccumulator = 0x4A;
    private const byte OraImmediate = 0x09;
    private const byte Pha = 0x48;
    private const byte Pla = 0x68;
    private const byte RolZeroPage = 0x26;
    private const byte Rti = 0x40;
    private const byte Rts = 0x60;
    private const byte Sei = 0x78;
    private const byte StaAbsolute = 0x8D;
    private const byte StaAbsoluteX = 0x9D;
    private const byte StaZeroPage = 0x85;
    private const byte Tay = 0xA8;
    private const byte Txa = 0x8A;
    private const byte Txs = 0x9A;

    /// <summary>Gets the shipped cartridges in file-name order: each file name, with its <c>.nes</c> extension, and the
    /// function that builds its image.</summary>
    public static IReadOnlyList<(string FileName, Func<byte[]> Build)> All { get; } = [
        ("colour-bars.nes", ColourBars),
        ("moving-sprite.nes", MovingSprite),
    ];

    /// <summary>Builds eight full-height vertical colour bars (white, yellow, cyan, green, magenta, red, blue, black),
    /// each four tiles wide. Held buttons tint the picture through the PPU's emphasis bits, read once a frame in vertical
    /// blank: A emphasises blue, B green and Select red, and Start turns the picture greyscale.</summary>
    /// <returns>The iNES image.</returns>
    public static byte[] ColourBars() {
        var program = Prologue();

        Palette(
            background: [0x0F, 0x30, 0x28, 0x0F, 0x0F, 0x2C, 0x2A, 0x0F, 0x0F, 0x24, 0x16, 0x0F, 0x0F, 0x12, 0x0F, 0x0F],
            program: program
        );
        // Nametable 0: every row is the bar row; tile 1 shows colour 1 and tile 2 colour 2 of each bar's palette.
        _ = program
            .Op(opcode: LdaImmediate, operand: ((byte)0x20)).Op(address: 0x2006, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(address: 0x2006, opcode: StaAbsolute)
            .Op(opcode: LdyImmediate, operand: ((byte)0x00))
            .Label(name: "bar-row").Op(opcode: LdxImmediate, operand: ((byte)0x00))
            .Label(name: "bar-column").Jump(label: "bar-tiles", opcode: LdaAbsoluteX).Op(address: 0x2007, opcode: StaAbsolute)
            .Bytes(Inx).Op(opcode: CpxImmediate, operand: ((byte)32)).Branch(label: "bar-column", opcode: Bne)
            .Bytes(Iny).Op(opcode: CpyImmediate, operand: ((byte)30)).Branch(label: "bar-row", opcode: Bne)
            // Attribute table 0: each 32-pixel column takes palette (column / 2) in all four quadrants.
            .Op(opcode: LdxImmediate, operand: ((byte)0x00))
            .Label(name: "bar-attribute").Bytes(Txa).Op(opcode: AndImmediate, operand: ((byte)0x07)).Bytes(Tay)
            .Jump(label: "bar-attributes", opcode: LdaAbsoluteY).Op(address: 0x2007, opcode: StaAbsolute)
            .Bytes(Inx).Op(opcode: CpxImmediate, operand: ((byte)64)).Branch(label: "bar-attribute", opcode: Bne);
        Epilogue(
            mask: 0x0A,
            program: program
        );
        // Each frame: the mask from the held buttons (A, B and Select are the top three pad bits, the emphasis bits'
        // own positions), written in vertical blank.
        _ = program
            .Label(name: "main").Jump(label: "wait-frame", opcode: Jsr).Jump(label: "read-pad", opcode: Jsr)
            .Op(opcode: LdaZeroPage, operand: Pad).Op(opcode: AndImmediate, operand: ((byte)0xE0))
            .Op(opcode: OraImmediate, operand: ((byte)0x0A)).Op(opcode: StaZeroPage, operand: Mask)
            .Op(opcode: LdaZeroPage, operand: Pad).Op(opcode: AndImmediate, operand: ((byte)0x10)).Branch(label: "tint", opcode: Beq)
            .Op(opcode: LdaZeroPage, operand: Mask).Op(opcode: OraImmediate, operand: ((byte)0x01)).Op(opcode: StaZeroPage, operand: Mask)
            .Label(name: "tint").Op(opcode: LdaZeroPage, operand: Mask).Op(address: 0x2001, opcode: StaAbsolute)
            .Jump(label: "main", opcode: Jmp);
        Subroutines(program: program);
        _ = program
            .Label(name: "bar-tiles").Bytes(1, 1, 1, 1, 2, 2, 2, 2, 1, 1, 1, 1, 2, 2, 2, 2, 1, 1, 1, 1, 2, 2, 2, 2, 1, 1, 1, 1, 2, 2, 2, 2)
            .Label(name: "bar-attributes").Bytes(0x00, 0x00, 0x55, 0x55, 0xAA, 0xAA, 0xFF, 0xFF);

        return program.Build(chr: Patterns());
    }
    /// <summary>Builds a 16 by 16 orange ball that bounces round a navy sky above a green checkered ground, one pixel a
    /// frame on each axis. A held direction on the control pad steers it: left or right, up or down sets its heading on
    /// that axis until it next meets an edge.</summary>
    /// <returns>The iNES image.</returns>
    public static byte[] MovingSprite() {
        var program = Prologue();

        Palette(
            background: [0x02, 0x1A, 0x2A, 0x0A, 0x02, 0x1A, 0x2A, 0x0A, 0x02, 0x1A, 0x2A, 0x0A, 0x02, 0x1A, 0x2A, 0x0A],
            program: program
        );
        _ = program
            // Nametable 0: rows 0 to 25 are sky (tile 0) and rows 26 to 29 the checkered ground (tile 3); attributes 0.
            .Op(opcode: LdaImmediate, operand: ((byte)0x20)).Op(address: 0x2006, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(address: 0x2006, opcode: StaAbsolute)
            .Op(opcode: LdyImmediate, operand: ((byte)0x00))
            .Label(name: "ground-row").Op(opcode: LdxImmediate, operand: ((byte)0x00))
            .Label(name: "ground-column").Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(opcode: CpyImmediate, operand: ((byte)26))
            .Branch(label: "ground-store", opcode: Bcc).Op(opcode: LdaImmediate, operand: ((byte)0x03))
            .Label(name: "ground-store").Op(address: 0x2007, opcode: StaAbsolute)
            .Bytes(Inx).Op(opcode: CpxImmediate, operand: ((byte)32)).Branch(label: "ground-column", opcode: Bne)
            .Bytes(Iny).Op(opcode: CpyImmediate, operand: ((byte)30)).Branch(label: "ground-row", opcode: Bne)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(opcode: LdxImmediate, operand: ((byte)0x00))
            .Label(name: "ground-attribute").Op(address: 0x2007, opcode: StaAbsolute)
            .Bytes(Inx).Op(opcode: CpxImmediate, operand: ((byte)64)).Branch(label: "ground-attribute", opcode: Bne)
            // Object memory: every sprite hidden below the picture, then the ball at (64, 64) heading right and down.
            .Op(opcode: LdaImmediate, operand: ((byte)0xFF)).Op(opcode: LdxImmediate, operand: ((byte)0x00))
            .Label(name: "hide").Op(address: 0x0200, opcode: StaAbsoluteX).Bytes(Inx).Branch(label: "hide", opcode: Bne)
            .Op(opcode: LdaImmediate, operand: ((byte)64)).Op(opcode: StaZeroPage, operand: BallX).Op(opcode: StaZeroPage, operand: BallY)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(opcode: StaZeroPage, operand: BallLeft).Op(opcode: StaZeroPage, operand: BallUp)
            .Jump(label: "place-ball", opcode: Jsr);
        Epilogue(
            mask: 0x1E,
            program: program
        );
        _ = program
            .Label(name: "main").Jump(label: "wait-frame", opcode: Jsr).Jump(label: "read-pad", opcode: Jsr)
            // Steer: the pad's low nibble holds up, down, left and right, from bit 3 down.
            .Op(opcode: LdaZeroPage, operand: Pad).Op(opcode: AndImmediate, operand: ((byte)0x02)).Branch(label: "not-left", opcode: Beq)
            .Op(opcode: LdaImmediate, operand: ((byte)0x01)).Op(opcode: StaZeroPage, operand: BallLeft)
            .Label(name: "not-left").Op(opcode: LdaZeroPage, operand: Pad).Op(opcode: AndImmediate, operand: ((byte)0x01)).Branch(label: "not-right", opcode: Beq)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(opcode: StaZeroPage, operand: BallLeft)
            .Label(name: "not-right").Op(opcode: LdaZeroPage, operand: Pad).Op(opcode: AndImmediate, operand: ((byte)0x08)).Branch(label: "not-up", opcode: Beq)
            .Op(opcode: LdaImmediate, operand: ((byte)0x01)).Op(opcode: StaZeroPage, operand: BallUp)
            .Label(name: "not-up").Op(opcode: LdaZeroPage, operand: Pad).Op(opcode: AndImmediate, operand: ((byte)0x04)).Branch(label: "move-x", opcode: Beq)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(opcode: StaZeroPage, operand: BallUp)
            // Move on x, turning at either edge.
            .Label(name: "move-x").Op(opcode: LdaZeroPage, operand: BallLeft).Branch(label: "move-left", opcode: Bne)
            .Op(opcode: LdaZeroPage, operand: BallX).Op(opcode: CmpImmediate, operand: BallRight).Branch(label: "turn-left", opcode: Bcs)
            .Op(opcode: IncZeroPage, operand: BallX).Jump(label: "move-y", opcode: Jmp)
            .Label(name: "turn-left").Op(opcode: LdaImmediate, operand: ((byte)0x01)).Op(opcode: StaZeroPage, operand: BallLeft).Jump(label: "move-y", opcode: Jmp)
            .Label(name: "move-left").Op(opcode: LdaZeroPage, operand: BallX).Branch(label: "turn-right", opcode: Beq)
            .Op(opcode: DecZeroPage, operand: BallX).Jump(label: "move-y", opcode: Jmp)
            .Label(name: "turn-right").Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(opcode: StaZeroPage, operand: BallLeft)
            // Move on y, turning at the top and above the ground.
            .Label(name: "move-y").Op(opcode: LdaZeroPage, operand: BallUp).Branch(label: "move-up", opcode: Bne)
            .Op(opcode: LdaZeroPage, operand: BallY).Op(opcode: CmpImmediate, operand: BallBottom).Branch(label: "turn-up", opcode: Bcs)
            .Op(opcode: IncZeroPage, operand: BallY).Jump(label: "moved", opcode: Jmp)
            .Label(name: "turn-up").Op(opcode: LdaImmediate, operand: ((byte)0x01)).Op(opcode: StaZeroPage, operand: BallUp).Jump(label: "moved", opcode: Jmp)
            .Label(name: "move-up").Op(opcode: LdaZeroPage, operand: BallY).Op(opcode: CmpImmediate, operand: ((byte)(BallTop + 1))).Branch(label: "turn-down", opcode: Bcc)
            .Op(opcode: DecZeroPage, operand: BallY).Jump(label: "moved", opcode: Jmp)
            .Label(name: "turn-down").Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(opcode: StaZeroPage, operand: BallUp)
            .Label(name: "moved").Jump(label: "place-ball", opcode: Jsr).Jump(label: "main", opcode: Jmp)
            // The ball is four sprites of the quarter-disc tile 4 flipped into place (top left, top right, bottom left,
            // bottom right), each object entry y, tile, attributes, x; the NMI copies them to object memory.
            .Label(name: "place-ball")
            .Op(opcode: LdaZeroPage, operand: BallY).Op(address: 0x0200, opcode: StaAbsolute).Op(address: 0x0204, opcode: StaAbsolute)
            .Bytes(Clc).Op(opcode: AdcImmediate, operand: ((byte)8)).Op(address: 0x0208, opcode: StaAbsolute).Op(address: 0x020C, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x04)).Op(address: 0x0201, opcode: StaAbsolute).Op(address: 0x0205, opcode: StaAbsolute)
            .Op(address: 0x0209, opcode: StaAbsolute).Op(address: 0x020D, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(address: 0x0202, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x40)).Op(address: 0x0206, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x80)).Op(address: 0x020A, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0xC0)).Op(address: 0x020E, opcode: StaAbsolute)
            .Op(opcode: LdaZeroPage, operand: BallX).Op(address: 0x0203, opcode: StaAbsolute).Op(address: 0x020B, opcode: StaAbsolute)
            .Bytes(Clc).Op(opcode: AdcImmediate, operand: ((byte)8)).Op(address: 0x0207, opcode: StaAbsolute).Op(address: 0x020F, opcode: StaAbsolute)
            .Bytes(Rts);
        Subroutines(program: program);

        return program.Build(chr: Patterns());
    }

    // Reset: interrupts and decimal mode off, the stack at $01FF, rendering and the NMI off, the frame counter cleared,
    // then two vertical blanks for the PPU to warm up.
    private static DeckProgram Prologue() =>
        new DeckProgram()
            .Bytes(Sei, Cld).Op(opcode: LdxImmediate, operand: ((byte)0xFF)).Bytes(Txs)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(address: 0x2000, opcode: StaAbsolute).Op(address: 0x2001, opcode: StaAbsolute)
            .Op(opcode: StaZeroPage, operand: Frame)
            .Label(name: "warm-up-1").Op(address: 0x2002, opcode: BitAbsolute).Branch(label: "warm-up-1", opcode: Bpl)
            .Label(name: "warm-up-2").Op(address: 0x2002, opcode: BitAbsolute).Branch(label: "warm-up-2", opcode: Bpl);
    // Writes all 32 palette entries: the four background palettes given, then four identical sprite palettes, orange,
    // white and red on the backdrop.
    private static void Palette(DeckProgram program, ReadOnlySpan<byte> background) {
        _ = program
            .Op(opcode: LdaImmediate, operand: ((byte)0x3F)).Op(address: 0x2006, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(address: 0x2006, opcode: StaAbsolute);
        foreach (var entry in background) {
            _ = program.Op(opcode: LdaImmediate, operand: entry).Op(address: 0x2007, opcode: StaAbsolute);
        }
        for (var palette = 0; (palette < 4); ++palette) {
            foreach (var entry in ((ReadOnlySpan<byte>)[background[0], 0x27, 0x30, 0x16])) {
                _ = program.Op(opcode: LdaImmediate, operand: entry).Op(address: 0x2007, opcode: StaAbsolute);
            }
        }
    }
    // Copies object memory from page 2, resets the scroll to the top left of nametable 0, enables the NMI with both
    // layers on pattern table 0, and turns rendering on with the given mask.
    private static void Epilogue(DeckProgram program, byte mask) =>
        _ = program
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(address: 0x2003, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x02)).Op(address: 0x4014, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(address: 0x2005, opcode: StaAbsolute).Op(address: 0x2005, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x80)).Op(address: 0x2000, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: mask).Op(address: 0x2001, opcode: StaAbsolute);
    // wait-frame returns once the NMI has counted the next frame. read-pad strobes the port-one controller and leaves its
    // eight buttons in Pad, A in bit 7 down to Right in bit 0. The NMI copies object memory from page 2, counts the frame
    // and returns.
    private static void Subroutines(DeckProgram program) =>
        _ = program
            .Label(name: "wait-frame").Op(opcode: LdaZeroPage, operand: Frame)
            .Label(name: "wait-frame-loop").Op(opcode: CmpZeroPage, operand: Frame).Branch(label: "wait-frame-loop", opcode: Beq).Bytes(Rts)
            .Label(name: "read-pad").Op(opcode: LdaImmediate, operand: ((byte)0x01)).Op(address: 0x4016, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(address: 0x4016, opcode: StaAbsolute).Op(opcode: LdxImmediate, operand: ((byte)0x08))
            .Label(name: "read-pad-bit").Op(address: 0x4016, opcode: LdaAbsolute).Bytes(LsrAccumulator).Op(opcode: RolZeroPage, operand: Pad)
            .Bytes(Dex).Branch(label: "read-pad-bit", opcode: Bne).Bytes(Rts)
            .Label(name: "nmi").Bytes(Pha)
            .Op(opcode: LdaImmediate, operand: ((byte)0x00)).Op(address: 0x2003, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x02)).Op(address: 0x4014, opcode: StaAbsolute)
            .Op(opcode: IncZeroPage, operand: Frame).Bytes(Pla, Rti);
    // Tile 0 is empty, tile 1 solid colour 1, tile 2 solid colour 2, tile 3 a checker of all four colours, and tile 4 the
    // top-left quarter of a 16-pixel disc in colour 1.
    private static byte[] Patterns() {
        var chr = new byte[8192];
        ReadOnlySpan<byte> quarterDisc = [0x07, 0x1F, 0x3F, 0x7F, 0x7F, 0xFF, 0xFF, 0xFF];

        for (var row = 0; (row < 8); ++row) {
            chr[(16 + row)] = 0xFF;
            chr[((32 + 8) + row)] = 0xFF;
            chr[(48 + row)] = 0xAA;
            chr[((48 + 8) + row)] = 0xCC;
            chr[(64 + row)] = quarterDisc[row];
        }

        return chr;
    }
}
