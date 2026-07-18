namespace Puck.Demo.Forge.Framework;

/// <summary>
/// The framework font and text printers. The font is ~40 hand-authored glyphs (space, the digits 0-9, A-Z, the '&gt;'
/// cursor, '-', and the sentence '.'), authored in the forge's ASCII-row style ('#' = colour index 3 on a transparent index-0 field)
/// and encoded to 2bpp tiles at build time; a game appends <see cref="BuildFontTiles"/> to its tile set at any base
/// id and tells the module where via <c>fontTileBase</c>. Strings are encoded to tile ids at BUILD time (there is no
/// runtime character mapping), printed either through the background write queue (any time) or directly (LCD off /
/// VBlank), plus packed-BCD number printers for scores and counters.
/// </summary>
internal sealed class TextModule {
    /// <summary>The number of glyphs in the framework font.</summary>
    public const int GlyphCount = 40;

    private readonly BgModule m_bg;
    private readonly Sm83Emitter m_emitter;
    private readonly byte m_fontTileBase;
    private readonly int m_printQueuedLabel;
    private readonly int m_printDirectLabel;
    private readonly int m_printBcdQueuedLabel;
    private readonly int m_printBcdDirectLabel;

    /// <summary>Creates the module over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="bg">The background module (queued prints go through its queue-push subroutine).</param>
    /// <param name="fontTileBase">The tile id of the font's first glyph (the space) in the game's tile set.</param>
    public TextModule(Sm83Emitter emitter, BgModule bg, byte fontTileBase) {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(bg);

        m_bg = bg;
        m_emitter = emitter;
        m_fontTileBase = fontTileBase;
        m_printQueuedLabel = emitter.NewLabel();
        m_printDirectLabel = emitter.NewLabel();
        m_printBcdQueuedLabel = emitter.NewLabel();
        m_printBcdDirectLabel = emitter.NewLabel();
    }

    /// <summary>The tile id of the digit '0' (digits are contiguous, so digit d = this + d).</summary>
    public byte DigitTileBase => (byte)(m_fontTileBase + 1);
    /// <summary>The tile id of the letter 'A' (letters are contiguous, so letter n = this + n).</summary>
    public byte LetterTileBase => (byte)(m_fontTileBase + 11);

    /// <summary>Maps a supported character to its font tile id.</summary>
    /// <param name="fontTileBase">The tile id of the font's first glyph.</param>
    /// <param name="character">The character (space, 0-9, A-Z, '&gt;', '-').</param>
    /// <returns>The tile id.</returns>
    public static byte TileFor(byte fontTileBase, char character) => (byte)(fontTileBase + GlyphIndexOf(character: character));

    /// <summary>Maps a supported character to its tile id with this module's font base.</summary>
    /// <param name="character">The character.</param>
    /// <returns>The tile id.</returns>
    public byte TileFor(char character) => TileFor(fontTileBase: m_fontTileBase, character: character);

    /// <summary>Encodes a string to font tile ids (build time; no terminator — <see cref="RomDataBuilder.AddText"/>
    /// appends the print routines' <c>0xFF</c>).</summary>
    /// <param name="text">The text.</param>
    /// <returns>One tile id per character.</returns>
    public byte[] EncodeString(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var tiles = new byte[text.Length];

        for (var index = 0; (index < text.Length); index++) {
            tiles[index] = TileFor(character: text[index]);
        }

        return tiles;
    }

    /// <summary>Emits a queued print of a baked string at a map cell (usable any time; drains next VBlank).</summary>
    /// <param name="text">The string table (<c>0xFF</c>-terminated tile ids).</param>
    /// <param name="row">The map row.</param>
    /// <param name="column">The map column of the first character.</param>
    public void EmitPrintQueued(RomTable text, int row, int column) {
        m_emitter.LoadImmediate(pair: Reg16.Hl, value: text.Address);
        m_emitter.LoadImmediate(pair: Reg16.De, value: Hw.MapCell(row: row, column: column));
        m_emitter.Call(label: m_printQueuedLabel);
    }

    /// <summary>Emits a direct print of a baked string at a map cell (LCD off / VBlank only).</summary>
    /// <param name="text">The string table.</param>
    /// <param name="row">The map row.</param>
    /// <param name="column">The map column of the first character.</param>
    public void EmitPrintDirect(RomTable text, int row, int column) {
        m_emitter.LoadImmediate(pair: Reg16.Hl, value: text.Address);
        m_emitter.LoadImmediate(pair: Reg16.De, value: Hw.MapCell(row: row, column: column));
        m_emitter.Call(label: m_printDirectLabel);
    }

    /// <summary>Emits a queued print of a packed-BCD number (two digits per byte, most-significant byte first).</summary>
    /// <param name="bcdAddress">The number's first (most significant) byte in work RAM.</param>
    /// <param name="byteCount">The number of BCD bytes (digits = 2 × this).</param>
    /// <param name="row">The map row.</param>
    /// <param name="column">The map column of the first digit.</param>
    public void EmitPrintBcdQueued(ushort bcdAddress, int byteCount, int row, int column) {
        m_emitter.LoadImmediate(pair: Reg16.Hl, value: bcdAddress);
        m_emitter.LoadImmediate(pair: Reg16.De, value: Hw.MapCell(row: row, column: column));
        m_emitter.LoadImmediate(destination: Reg8.B, value: (byte)byteCount);
        m_emitter.Call(label: m_printBcdQueuedLabel);
    }

    /// <summary>Emits a direct print of a packed-BCD number (LCD off / VBlank only).</summary>
    /// <param name="bcdAddress">The number's first (most significant) byte in work RAM.</param>
    /// <param name="byteCount">The number of BCD bytes.</param>
    /// <param name="row">The map row.</param>
    /// <param name="column">The map column of the first digit.</param>
    public void EmitPrintBcdDirect(ushort bcdAddress, int byteCount, int row, int column) {
        m_emitter.LoadImmediate(pair: Reg16.Hl, value: bcdAddress);
        m_emitter.LoadImmediate(pair: Reg16.De, value: Hw.MapCell(row: row, column: column));
        m_emitter.LoadImmediate(destination: Reg8.B, value: (byte)byteCount);
        m_emitter.Call(label: m_printBcdDirectLabel);
    }

    /// <summary>Emits the module's library subroutines (the four printers). Called once by the framework facade.</summary>
    public void EmitLibrary() {
        EmitPrintQueuedSubroutine();
        EmitPrintDirectSubroutine();
        EmitPrintBcdQueuedSubroutine();
        EmitPrintBcdDirectSubroutine();
    }

    /// <summary>Builds the font's 2bpp tile bytes (<see cref="GlyphCount"/> × 16), glyph order: space, 0-9, A-Z,
    /// '&gt;', '-'. Append to the game's tile data at the <c>fontTileBase</c> given to the constructor.</summary>
    /// <returns>The tile bytes.</returns>
    public static byte[] BuildFontTiles() {
        var tiles = new byte[(GlyphCount * 16)];

        for (var glyph = 0; (glyph < GlyphCount); glyph++) {
            EncodeGlyph(rows: Glyphs[glyph]).CopyTo(array: tiles, index: (glyph * 16));
        }

        return tiles;
    }

    private void EmitPrintQueuedSubroutine() {
        var loop = m_emitter.NewLabel();

        // printQueued: HL = string, DE = map cell; pushes each glyph through the background queue. Clobbers all.
        m_emitter.MarkLabel(label: m_printQueuedLabel);
        m_emitter.MarkLabel(label: loop);
        m_emitter.Load(destination: Reg8.A, source: Reg8.Memory);
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: 0xFF);
        m_emitter.Return(condition: Condition.Zero);
        m_emitter.Increment(pair: Reg16.Hl);
        m_emitter.Push(pair: StackPair.Hl);
        m_emitter.Push(pair: StackPair.De);
        m_bg.EmitQueuePush();
        m_emitter.Pop(pair: StackPair.De);
        m_emitter.Pop(pair: StackPair.Hl);
        m_emitter.Increment(pair: Reg16.De);
        m_emitter.JumpRelative(label: loop);
    }
    private void EmitPrintDirectSubroutine() {
        var loop = m_emitter.NewLabel();

        // printDirect: HL = string, DE = map cell; raw stores (LCD off / VBlank). Clobbers A, D, E, H, L.
        m_emitter.MarkLabel(label: m_printDirectLabel);
        m_emitter.MarkLabel(label: loop);
        m_emitter.Load(destination: Reg8.A, source: Reg8.Memory);
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: 0xFF);
        m_emitter.Return(condition: Condition.Zero);
        m_emitter.Increment(pair: Reg16.Hl);
        m_emitter.StoreAToDe();
        m_emitter.Increment(pair: Reg16.De);
        m_emitter.JumpRelative(label: loop);
    }
    private void EmitPrintBcdQueuedSubroutine() {
        var loop = m_emitter.NewLabel();

        // printBcdQueued: HL = BCD (MSB first), DE = map cell, B = byte count. Clobbers all.
        m_emitter.MarkLabel(label: m_printBcdQueuedLabel);
        m_emitter.MarkLabel(label: loop);

        // High nibble digit.
        m_emitter.Load(destination: Reg8.A, source: Reg8.Memory);
        m_emitter.Shift(op: ShiftOp.Swap, register: Reg8.A);
        m_emitter.ArithmeticImmediate(op: AluOp.And, value: 0x0F);
        m_emitter.ArithmeticImmediate(op: AluOp.Add, value: DigitTileBase);
        EmitGuardedQueuePush();
        m_emitter.Increment(pair: Reg16.De);

        // Low nibble digit.
        m_emitter.Load(destination: Reg8.A, source: Reg8.Memory);
        m_emitter.ArithmeticImmediate(op: AluOp.And, value: 0x0F);
        m_emitter.ArithmeticImmediate(op: AluOp.Add, value: DigitTileBase);
        EmitGuardedQueuePush();
        m_emitter.Increment(pair: Reg16.De);

        m_emitter.Increment(pair: Reg16.Hl);
        m_emitter.Decrement(register: Reg8.B);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: loop);
        m_emitter.Return();
    }
    private void EmitPrintBcdDirectSubroutine() {
        var loop = m_emitter.NewLabel();

        // printBcdDirect: HL = BCD (MSB first), DE = map cell, B = byte count. LCD off / VBlank only.
        m_emitter.MarkLabel(label: m_printBcdDirectLabel);
        m_emitter.MarkLabel(label: loop);
        m_emitter.Load(destination: Reg8.A, source: Reg8.Memory);
        m_emitter.Shift(op: ShiftOp.Swap, register: Reg8.A);
        m_emitter.ArithmeticImmediate(op: AluOp.And, value: 0x0F);
        m_emitter.ArithmeticImmediate(op: AluOp.Add, value: DigitTileBase);
        m_emitter.StoreAToDe();
        m_emitter.Increment(pair: Reg16.De);
        m_emitter.Load(destination: Reg8.A, source: Reg8.Memory);
        m_emitter.ArithmeticImmediate(op: AluOp.And, value: 0x0F);
        m_emitter.ArithmeticImmediate(op: AluOp.Add, value: DigitTileBase);
        m_emitter.StoreAToDe();
        m_emitter.Increment(pair: Reg16.De);
        m_emitter.Increment(pair: Reg16.Hl);
        m_emitter.Decrement(register: Reg8.B);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: loop);
        m_emitter.Return();
    }

    // A queue push that survives the push subroutine's clobbers: BC/DE/HL saved around the call (A carries the tile).
    private void EmitGuardedQueuePush() {
        m_emitter.Push(pair: StackPair.Bc);
        m_emitter.Push(pair: StackPair.De);
        m_emitter.Push(pair: StackPair.Hl);
        m_bg.EmitQueuePush();
        m_emitter.Pop(pair: StackPair.Hl);
        m_emitter.Pop(pair: StackPair.De);
        m_emitter.Pop(pair: StackPair.Bc);
    }
    private static int GlyphIndexOf(char character) =>
        character switch {
            ' ' => 0,
            (>= '0') and (<= '9') => (1 + (character - '0')),
            (>= 'A') and (<= 'Z') => (11 + (character - 'A')),
            '>' => 37,
            '-' => 38,
            '.' => 39,
            _ => throw new ArgumentException(message: $"The framework font has no glyph for '{character}' (supported: space, 0-9, A-Z, '>', '-', '.').", paramName: nameof(character)),
        };

    // Encodes an 8-row ASCII pattern ('#' = colour index 3, anything else = 0) to the 16-byte 2bpp tile form.
    private static byte[] EncodeGlyph(string[] rows) {
        var indices = new byte[64];

        for (var row = 0; (row < 8); row++) {
            var line = rows[row];

            for (var column = 0; (column < 8); column++) {
                indices[((row * 8) + column)] = (byte)(((column < line.Length) && (line[column] == '#')) ? 3 : 0);
            }
        }

        return HgbImage.EncodeTile2bpp(tileIndices: indices);
    }

    private static readonly string[][] Glyphs = [
        // Space.
        [ "........", "........", "........", "........", "........", "........", "........", "........" ],
        // Digits 0-9.
        [ ".####...", ".#..#...", ".#..#...", ".#..#...", ".#..#...", ".#..#...", ".####...", "........" ],
        [ "...#....", "..##....", "...#....", "...#....", "...#....", "...#....", "..###...", "........" ],
        [ ".####...", "....#...", "....#...", ".####...", ".#......", ".#......", ".####...", "........" ],
        [ ".####...", "....#...", "....#...", ".####...", "....#...", "....#...", ".####...", "........" ],
        [ ".#..#...", ".#..#...", ".#..#...", ".####...", "....#...", "....#...", "....#...", "........" ],
        [ ".####...", ".#......", ".#......", ".####...", "....#...", "....#...", ".####...", "........" ],
        [ ".####...", ".#......", ".#......", ".####...", ".#..#...", ".#..#...", ".####...", "........" ],
        [ ".####...", "....#...", "....#...", "...#....", "..#.....", "..#.....", "..#.....", "........" ],
        [ ".####...", ".#..#...", ".#..#...", ".####...", ".#..#...", ".#..#...", ".####...", "........" ],
        [ ".####...", ".#..#...", ".#..#...", ".####...", "....#...", "....#...", ".####...", "........" ],
        // Letters A-Z.
        [ ".###....", "#...#...", "#...#...", "#####...", "#...#...", "#...#...", "#...#...", "........" ],
        [ "####....", "#...#...", "#...#...", "####....", "#...#...", "#...#...", "####....", "........" ],
        [ ".####...", "#.......", "#.......", "#.......", "#.......", "#.......", ".####...", "........" ],
        [ "####....", "#...#...", "#...#...", "#...#...", "#...#...", "#...#...", "####....", "........" ],
        [ "#####...", "#.......", "#.......", "####....", "#.......", "#.......", "#####...", "........" ],
        [ "#####...", "#.......", "#.......", "####....", "#.......", "#.......", "#.......", "........" ],
        [ ".####...", "#.......", "#.......", "#..##...", "#...#...", "#...#...", ".####...", "........" ],
        [ "#...#...", "#...#...", "#...#...", "#####...", "#...#...", "#...#...", "#...#...", "........" ],
        [ ".###....", "..#.....", "..#.....", "..#.....", "..#.....", "..#.....", ".###....", "........" ],
        [ "..###...", "...#....", "...#....", "...#....", "...#....", "#..#....", ".##.....", "........" ],
        [ "#...#...", "#..#....", "#.#.....", "##......", "#.#.....", "#..#....", "#...#...", "........" ],
        [ "#.......", "#.......", "#.......", "#.......", "#.......", "#.......", "#####...", "........" ],
        [ "#...#...", "##.##...", "#.#.#...", "#.#.#...", "#...#...", "#...#...", "#...#...", "........" ],
        [ "#...#...", "##..#...", "#.#.#...", "#..##...", "#...#...", "#...#...", "#...#...", "........" ],
        [ ".###....", "#...#...", "#...#...", "#...#...", "#...#...", "#...#...", ".###....", "........" ],
        [ "####....", "#...#...", "#...#...", "####....", "#.......", "#.......", "#.......", "........" ],
        [ ".###....", "#...#...", "#...#...", "#...#...", "#.#.#...", "#..#....", ".##.#...", "........" ],
        [ "####....", "#...#...", "#...#...", "####....", "#.#.....", "#..#....", "#...#...", "........" ],
        [ ".####...", "#.......", "#.......", ".###....", "....#...", "....#...", "####....", "........" ],
        [ "#####...", "..#.....", "..#.....", "..#.....", "..#.....", "..#.....", "..#.....", "........" ],
        [ "#...#...", "#...#...", "#...#...", "#...#...", "#...#...", "#...#...", ".###....", "........" ],
        [ "#...#...", "#...#...", "#...#...", "#...#...", "#...#...", ".#.#....", "..#.....", "........" ],
        [ "#...#...", "#...#...", "#...#...", "#.#.#...", "#.#.#...", "##.##...", "#...#...", "........" ],
        [ "#...#...", "#...#...", ".#.#....", "..#.....", ".#.#....", "#...#...", "#...#...", "........" ],
        [ "#...#...", "#...#...", ".#.#....", "..#.....", "..#.....", "..#.....", "..#.....", "........" ],
        [ "#####...", "....#...", "...#....", "..#.....", ".#......", "#.......", "#####...", "........" ],
        // '>' and '-'.
        [ ".#......", "..#.....", "...#....", "....#...", "...#....", "..#.....", ".#......", "........" ],
        [ "........", "........", "........", ".####...", "........", "........", "........", "........" ],
        // '.' — the sentence full stop.
        [ "........", "........", "........", "........", "........", "...##...", "...##...", "........" ],
    ];
}
