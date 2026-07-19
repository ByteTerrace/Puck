namespace Puck.Forge.Framework;

/// <summary>
/// Background-map plumbing: the VRAM write QUEUE (game code pushes cell writes at any point in the frame; the VBlank
/// handler drains them while VRAM is writable) plus the LCD-off bulk paints for full-screen transitions. The queue
/// subroutine takes D = address-high, E = address-low, A = tile, and silently drops the push when the
/// <see cref="FrameworkMemoryMap.VramQueueCapacity"/>-entry queue is full.
/// </summary>
internal sealed class BgModule {
    private readonly Sm83Emitter m_emitter;
    private readonly int m_queuePushLabel;

    /// <summary>Creates the module over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    public BgModule(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        m_emitter = emitter;
        m_queuePushLabel = emitter.NewLabel();
    }

    /// <summary>The queue-push subroutine's label (D = address-high, E = address-low, A = tile; clobbers A, B, C, H, L).
    /// Exposed so sibling modules (the text printer) can call it.</summary>
    public int QueuePushLabel => m_queuePushLabel;

    /// <summary>Emits a call to the queue-push subroutine (caller pre-loads D/E = cell address, A = tile).</summary>
    public void EmitQueuePush() => m_emitter.Call(label: m_queuePushLabel);

    /// <summary>Emits a queue push for a cell KNOWN at build time: loads DE with the cell address and calls the push
    /// subroutine. The caller pre-loads A with the tile id (the DE load does not clobber A).</summary>
    /// <param name="row">The map row.</param>
    /// <param name="column">The map column.</param>
    public void EmitQueueCell(int row, int column) {
        m_emitter.LoadImmediate(pair: Reg16.De, value: Hw.MapCell(row: row, column: column));
        m_emitter.Call(label: m_queuePushLabel);
    }

    /// <summary>Emits a queue reset (count = 0). Call inside an LCD-off repaint so stale queued cells from the previous
    /// screen never drain on top of the fresh paint (with the LCD off no VBlank fires, so the clear is race-free).</summary>
    public void EmitQueueClear() {
        m_emitter.XorA();
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.VramQueueCount);
    }

    /// <summary>Emits LCDC = 0 (LCD off; VRAM freely writable, no VBlank interrupts until re-enabled).</summary>
    public void EmitLcdOff() {
        m_emitter.XorA();
        m_emitter.StoreAToHighPage(port: Hw.PortLcdControl);
    }

    /// <summary>Emits LCDC = <paramref name="lcdc"/> (LCD back on with the caller's control bits).</summary>
    /// <param name="lcdc">The LCDC value.</param>
    public void EmitLcdOn(byte lcdc) {
        m_emitter.LoadAImmediate(value: lcdc);
        m_emitter.StoreAToHighPage(port: Hw.PortLcdControl);
    }

    /// <summary>Emits a full-screen paint: LCD off, queue cleared, the 1024-byte map copied to 0x9800, LCD back on.</summary>
    /// <param name="map">The 32×32 map table.</param>
    /// <param name="lcdc">The LCDC value to restore.</param>
    public void EmitFullPaint(RomTable map, byte lcdc) {
        if (map.Length != 0x400) {
            throw new ArgumentException(message: "A full-screen map is 32×32 = 1024 bytes.", paramName: nameof(map));
        }

        EmitLcdOff();
        EmitQueueClear();
        EmitCopyMap(sourceAddress: map.Address);
        EmitLcdOn(lcdc: lcdc);
    }

    /// <summary>Emits a raw 1024-byte copy from <paramref name="sourceAddress"/> to the background map. The caller
    /// owns the LCD state (call with the LCD off, or inside VBlank).</summary>
    /// <param name="sourceAddress">The source address (ROM or work RAM).</param>
    public void EmitCopyMap(ushort sourceAddress) =>
        FrameworkKernel.EmitBlockCopy(emitter: m_emitter, sourceAddress: sourceAddress, destinationAddress: Hw.VramBackgroundMap, byteCount: 0x400);

    /// <summary>Emits a map clear: every cell of the 1024-byte background map set to tile 0. LCD-off/VBlank only.</summary>
    public void EmitMapClear() =>
        FrameworkKernel.EmitBlockFill(emitter: m_emitter, destinationAddress: Hw.VramBackgroundMap, byteCount: 0x400, value: 0x00);

    /// <summary>Emits a rectangle paint: <paramref name="height"/> rows of <paramref name="width"/> tiles copied
    /// row-major from a contiguous source into the map at (<paramref name="row"/>, <paramref name="column"/>).
    /// LCD-off/VBlank only. Clobbers A, B, C, D, E, H, L.</summary>
    /// <param name="sourceAddress">The contiguous source (width × height bytes).</param>
    /// <param name="row">The destination's top map row.</param>
    /// <param name="column">The destination's left map column.</param>
    /// <param name="width">The rectangle width in tiles.</param>
    /// <param name="height">The rectangle height in tiles.</param>
    public void EmitPaintRect(ushort sourceAddress, int row, int column, int width, int height) {
        if ((width < 1) || (width > 32) || (height < 1)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(width), message: $"A {width}×{height} paint rectangle is out of the map's range.");
        }

        var rowLoop = m_emitter.NewLabel();
        var columnLoop = m_emitter.NewLabel();
        var noCarry = m_emitter.NewLabel();

        m_emitter.LoadImmediate(pair: Reg16.Hl, value: Hw.MapCell(row: row, column: column));
        m_emitter.LoadImmediate(pair: Reg16.De, value: sourceAddress);
        m_emitter.LoadImmediate(destination: Reg8.B, value: (byte)height);

        m_emitter.MarkLabel(label: rowLoop);
        m_emitter.LoadImmediate(destination: Reg8.C, value: (byte)width);

        m_emitter.MarkLabel(label: columnLoop);
        m_emitter.LoadAFromDe();
        m_emitter.Increment(pair: Reg16.De);
        m_emitter.StoreAToHlIncrement();
        m_emitter.Decrement(register: Reg8.C);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: columnLoop);

        // Advance HL from (row start + width) to the next row start: += (32 - width).
        m_emitter.Load(destination: Reg8.A, source: Reg8.L);
        m_emitter.ArithmeticImmediate(op: AluOp.Add, value: (byte)(32 - width));
        m_emitter.Load(destination: Reg8.L, source: Reg8.A);
        m_emitter.JumpRelative(condition: Condition.NoCarry, label: noCarry);
        m_emitter.Increment(register: Reg8.H);
        m_emitter.MarkLabel(label: noCarry);

        m_emitter.Decrement(register: Reg8.B);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: rowLoop);
    }

    /// <summary>Emits the scroll registers (SCX/SCY) as immediates.</summary>
    /// <param name="x">The horizontal scroll.</param>
    /// <param name="y">The vertical scroll.</param>
    public void EmitSetScroll(byte x, byte y) {
        m_emitter.LoadAImmediate(value: y);
        m_emitter.StoreAToHighPage(port: Hw.PortScrollY);
        m_emitter.LoadAImmediate(value: x);
        m_emitter.StoreAToHighPage(port: Hw.PortScrollX);
    }

    /// <summary>Emits the module's library subroutines (the queue push). Called once by the framework facade.</summary>
    public void EmitLibrary() {
        var full = m_emitter.NewLabel();

        // queuePush: D = address-high, E = address-low, A = tile. Clobbers A, B, C, H, L.
        m_emitter.MarkLabel(label: m_queuePushLabel);
        m_emitter.Load(destination: Reg8.C, source: Reg8.A);                                   // C = tile.
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.VramQueueCount);
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: FrameworkMemoryMap.VramQueueCapacity);
        m_emitter.JumpRelative(condition: Condition.NoCarry, label: full);                     // count >= capacity → drop.
        m_emitter.Load(destination: Reg8.B, source: Reg8.A);                                   // B = count.
        m_emitter.Arithmetic(op: AluOp.Add, source: Reg8.A);                                   // ×2
        m_emitter.Arithmetic(op: AluOp.Add, source: Reg8.B);                                   // ×3
        m_emitter.ArithmeticImmediate(op: AluOp.Add, value: (byte)(FrameworkMemoryMap.VramQueue & 0xFF));
        m_emitter.Load(destination: Reg8.L, source: Reg8.A);                                   // Entries never cross the page.
        m_emitter.LoadImmediate(destination: Reg8.H, value: (byte)(FrameworkMemoryMap.VramQueue >> 8));
        m_emitter.Load(destination: Reg8.Memory, source: Reg8.D);
        m_emitter.Increment(pair: Reg16.Hl);
        m_emitter.Load(destination: Reg8.Memory, source: Reg8.E);
        m_emitter.Increment(pair: Reg16.Hl);
        m_emitter.Load(destination: Reg8.Memory, source: Reg8.C);
        m_emitter.Load(destination: Reg8.A, source: Reg8.B);
        m_emitter.Increment(register: Reg8.A);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.VramQueueCount);
        m_emitter.MarkLabel(label: full);
        m_emitter.Return();
    }
}
