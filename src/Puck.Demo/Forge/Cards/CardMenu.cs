using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge.Cards;

/// <summary>One menu item: a linked string and the map cell its first character occupies. The cursor column is the
/// item column minus two (a <c>'&gt;'</c> glyph and a space).</summary>
/// <param name="Text">The linked string table.</param>
/// <param name="Row">The item's map row.</param>
/// <param name="Column">The item's map column.</param>
internal readonly record struct CardMenuItem(RomTable Text, int Row, int Column);

/// <summary>
/// The card layer's menu primitive over the framework's <see cref="TextModule"/>: a build-time item list, a
/// one-byte work-RAM cursor, direct item prints for a screen's enter (LCD off), and a per-frame tick that moves the
/// selection on Up/Down edges (with wrap), redraws the <c>'&gt;'</c> cursor through the background queue, and
/// dispatches a confirmed item (A or START edge) to the game's own emission callback. Shared by every card game's
/// title and dialog screens — declare items, wire callbacks, done.
/// </summary>
internal sealed class CardMenu {
    private readonly GameFramework m_fw;
    private readonly IReadOnlyList<CardMenuItem> m_items;
    private readonly ushort m_cursorAddress;

    /// <summary>Creates the menu.</summary>
    /// <param name="fw">The game's framework facade.</param>
    /// <param name="items">The items, top to bottom.</param>
    /// <param name="cursorAddress">The one-byte work-RAM cursor (0 .. items − 1; the enter should zero it).</param>
    public CardMenu(GameFramework fw, IReadOnlyList<CardMenuItem> items, ushort cursorAddress) {
        ArgumentNullException.ThrowIfNull(fw);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count < 1) {
            throw new ArgumentException(message: "A menu needs at least one item.", paramName: nameof(items));
        }

        m_cursorAddress = cursorAddress;
        m_fw = fw;
        m_items = items;
    }

    /// <summary>Emits the items' direct prints and zeroes the cursor (call from the screen's enter, LCD off).</summary>
    /// <param name="e">The routine emitter.</param>
    public void EmitEnterDraw(Sm83Emitter e) {
        ArgumentNullException.ThrowIfNull(e);

        foreach (var item in m_items) {
            m_fw.Text.EmitPrintDirect(text: item.Text, row: item.Row, column: item.Column);
        }

        e.XorA();
        e.StoreAToAddress(address: m_cursorAddress);
    }

    /// <summary>Emits the per-frame menu logic: Up/Down edges move the cursor with wrap, the cursor glyphs redraw
    /// through the queue, and an A/START edge dispatches the selected item. The confirm callback emits INSIDE the
    /// state's tick body — it may request a state and <c>ret</c>.</summary>
    /// <param name="e">The routine emitter.</param>
    /// <param name="emitConfirm">Emits one item's confirm behaviour (index in item order).</param>
    public void EmitTick(Sm83Emitter e, Action<Sm83Emitter, int> emitConfirm) {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(emitConfirm);

        EmitMove(e: e);
        EmitCursorDraw(e: e);
        EmitConfirm(e: e, emitConfirm: emitConfirm);
    }

    private void EmitMove(Sm83Emitter e) {
        var noUp = e.NewLabel();
        var upStore = e.NewLabel();
        var noDown = e.NewLabel();
        var downStore = e.NewLabel();
        var count = (byte)m_items.Count;

        // Up: cursor−1 with wrap to the last item.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 2, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noUp);
        e.LoadAFromAddress(address: m_cursorAddress);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: upStore);
        e.LoadAImmediate(value: count);
        e.MarkLabel(label: upStore);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: m_cursorAddress);
        e.MarkLabel(label: noUp);

        // Down: cursor+1 with wrap to the first item.
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 3, register: Reg8.B);
        e.JumpRelative(condition: Condition.Zero, label: noDown);
        e.LoadAFromAddress(address: m_cursorAddress);
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Compare, value: count);
        e.JumpRelative(condition: Condition.Carry, label: downStore);
        e.XorA();
        e.MarkLabel(label: downStore);
        e.StoreAToAddress(address: m_cursorAddress);
        e.MarkLabel(label: noDown);
    }

    private void EmitCursorDraw(Sm83Emitter e) {
        for (var index = 0; (index < m_items.Count); index++) {
            var isCursor = e.NewLabel();
            var push = e.NewLabel();

            e.LoadAFromAddress(address: m_cursorAddress);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)index);
            e.JumpRelative(condition: Condition.Zero, label: isCursor);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: ' '));
            e.JumpRelative(label: push);
            e.MarkLabel(label: isCursor);
            e.LoadAImmediate(value: m_fw.Text.TileFor(character: '>'));
            e.MarkLabel(label: push);
            m_fw.Bg.EmitQueueCell(row: m_items[index].Row, column: (m_items[index].Column - 2));
        }
    }

    private void EmitConfirm(Sm83Emitter e, Action<Sm83Emitter, int> emitConfirm) {
        var noConfirm = e.NewLabel();
        var confirmed = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 7, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: confirmed);
        e.TestBit(bit: 4, register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: confirmed);
        e.JumpAbsolute(label: noConfirm);
        e.MarkLabel(label: confirmed);

        for (var index = 0; (index < m_items.Count); index++) {
            var skip = e.NewLabel();

            e.LoadAFromAddress(address: m_cursorAddress);
            e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)index);
            e.JumpRelative(condition: Condition.NotZero, label: skip);
            emitConfirm(e, index);
            e.MarkLabel(label: skip);
        }

        e.MarkLabel(label: noConfirm);
    }
}
