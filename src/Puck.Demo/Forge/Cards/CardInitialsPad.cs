using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge.Cards;

/// <summary>
/// The card layer's three-initials entry primitive (the Brickfall score-entry pad generalized): Up/Down cycle the
/// active initial A↔Z with wrap, Left/Right move among the three slots with wrap, the glyphs and slot cursor redraw
/// through the background queue every frame, and A/START confirm into the game's emission callback. The glyph bytes
/// are letter INDICES (0 = A) — <see cref="TextModule.LetterTileBase"/> away from font tiles, exactly the shape
/// <see cref="GameManifest.BuildScoreTable"/> persists.
/// </summary>
internal sealed class CardInitialsPad {
    private const byte LetterCount = 26;
    private const byte SlotCount = 3;

    private readonly ushort m_cursorAddress;
    private readonly GameFramework m_fw;
    private readonly ushort m_glyphsAddress;
    private readonly int m_column;
    private readonly int m_row;

    /// <summary>Creates the pad.</summary>
    /// <param name="fw">The game's framework facade.</param>
    /// <param name="glyphsAddress">Three work-RAM bytes for the letter indices (must share one 256-byte page).</param>
    /// <param name="cursorAddress">One work-RAM byte for the active slot (0..2).</param>
    /// <param name="row">The map row the three initials render on (the slot cursor renders two rows below).</param>
    /// <param name="column">The map column of the first initial.</param>
    public CardInitialsPad(GameFramework fw, ushort glyphsAddress, ushort cursorAddress, int row, int column) {
        ArgumentNullException.ThrowIfNull(fw);

        if ((glyphsAddress & 0xFF) > 0xFD) {
            throw new ArgumentException(message: "The three glyph bytes must share one page (the pointer math is 8-bit).", paramName: nameof(glyphsAddress));
        }

        m_column = column;
        m_cursorAddress = cursorAddress;
        m_fw = fw;
        m_glyphsAddress = glyphsAddress;
        m_row = row;
    }

    /// <summary>Emits the pad reset (glyphs AAA, cursor on the first slot). Call from the screen's enter.</summary>
    /// <param name="e">The routine emitter.</param>
    public void EmitEnterReset(Sm83Emitter e) {
        ArgumentNullException.ThrowIfNull(e);

        e.XorA();
        e.StoreAToAddress(address: m_cursorAddress);

        for (var slot = 0; (slot < SlotCount); slot++) {
            e.StoreAToAddress(address: (ushort)(m_glyphsAddress + slot));
        }
    }

    /// <summary>Emits the per-frame pad logic. The confirm callback emits inside the state's tick body — the pad's
    /// draw is skipped on the confirming frame (the callback usually requests a state switch).</summary>
    /// <param name="e">The routine emitter.</param>
    /// <param name="emitConfirm">Emits the confirm behaviour (persist and leave, typically).</param>
    public void EmitTick(Sm83Emitter e, Action<Sm83Emitter> emitConfirm) {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(emitConfirm);

        var confirm = e.NewLabel();
        var done = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpAbsolute(condition: Condition.NotZero, label: confirm);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpAbsolute(condition: Condition.NotZero, label: confirm);

        EmitGlyphCycle(e: e);
        EmitSlotMove(e: e);
        EmitDraw(e: e);
        e.JumpAbsolute(label: done);

        e.MarkLabel(label: confirm);
        emitConfirm(e);
        e.MarkLabel(label: done);
    }

    private void EmitGlyphCycle(Sm83Emitter e) {
        var noUp = e.NewLabel();
        var upStore = e.NewLabel();
        var noDown = e.NewLabel();
        var downDecrement = e.NewLabel();

        // Up: the active initial cycles A → Z with wrap.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 2, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noUp);
        EmitGlyphPointer(e: e);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Compare, value: LetterCount);
        e.JumpRelative(condition: Condition.Carry, label: upStore);
        e.XorA();
        e.MarkLabel(label: upStore);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.MarkLabel(label: noUp);

        // Down: the other way (A wraps to Z).
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 3, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noDown);
        EmitGlyphPointer(e: e);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: downDecrement);
        e.LoadAImmediate(value: LetterCount);
        e.MarkLabel(label: downDecrement);
        e.Decrement(register: Reg8.A);
        e.Load(destination: Reg8.Memory, source: Reg8.A);
        e.MarkLabel(label: noDown);
    }
    private void EmitSlotMove(Sm83Emitter e) {
        var noLeft = e.NewLabel();
        var leftDecrement = e.NewLabel();
        var noRight = e.NewLabel();
        var rightStore = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 1, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noLeft);
        e.LoadAFromAddress(address: m_cursorAddress);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: leftDecrement);
        e.LoadAImmediate(value: SlotCount);
        e.MarkLabel(label: leftDecrement);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: m_cursorAddress);
        e.MarkLabel(label: noLeft);

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 0, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noRight);
        e.LoadAFromAddress(address: m_cursorAddress);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Compare, value: SlotCount);
        e.JumpRelative(condition: Condition.Carry, label: rightStore);
        e.XorA();
        e.MarkLabel(label: rightStore);
        e.StoreAToAddress(address: m_cursorAddress);
        e.MarkLabel(label: noRight);
    }
    private void EmitDraw(Sm83Emitter e) {
        // The three initials as font tiles, then the slot cursor markers, all through the queue (six pushes).
        for (var slot = 0; (slot < SlotCount); slot++) {
            e.LoadAFromAddress(address: (ushort)(m_glyphsAddress + slot));
            e.ArithmeticImmediate(op: AluOp.Add, value: m_fw.Text.LetterTileBase);
            m_fw.Bg.EmitQueueCell(row: m_row, column: (m_column + slot));
        }

        for (var slot = 0; (slot < SlotCount); slot++) {
            var isCursor = e.NewLabel();
            var push = e.NewLabel();

            e.LoadAFromAddress(address: m_cursorAddress);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)slot);
            e.JumpRelative(condition: Condition.Zero, label: isCursor);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: ' '));
            e.JumpRelative(label: push);
            e.MarkLabel(label: isCursor);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: '>'));
            e.MarkLabel(label: push);
            m_fw.Bg.EmitQueueCell(row: (m_row + 2), column: (m_column + slot));
        }
    }

    // HL := glyphs + cursor (the three glyph bytes share one page).
    private void EmitGlyphPointer(Sm83Emitter e) {
        e.LoadAFromAddress(address: m_cursorAddress);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(m_glyphsAddress & 0xFF));
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: (byte)(m_glyphsAddress >> 8));
    }
}
