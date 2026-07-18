namespace Puck.Demo.Forge.Framework;

/// <summary>What the kernel needs to bring the video hardware up at boot: the palette/tile/map tables, how many tile
/// bytes to copy to VRAM, the LCDC preset for the running game, and the state machine's initial state.</summary>
/// <param name="BgPalettes">The Color background palette table (8 bytes per palette, up to 64).</param>
/// <param name="ObjPalettes">The Color object palette table.</param>
/// <param name="Tiles">The 2bpp tile-graphics table.</param>
/// <param name="TileByteCount">How many tile bytes to copy to VRAM 0x8000 (usually <c>Tiles.Length</c>).</param>
/// <param name="InitialMap">The 1024-byte background map shown at boot (typically the title screen).</param>
/// <param name="Lcdc">The LCDC value the boot turns the screen on with.</param>
/// <param name="InitialState">The state machine's boot state (requested before the first frame).</param>
internal readonly record struct FrameworkBootSpec(RomTable BgPalettes, RomTable ObjPalettes, RomTable Tiles, int TileByteCount, RomTable InitialMap, byte Lcdc, byte InitialState);

/// <summary>
/// The interrupt-driven kernel (design D1): the fixed 3-byte <c>jp boot</c> prologue that pins the VBlank handler at
/// <see cref="Hw.VBlankHandlerAddress"/>; the handler itself (save registers → <c>call 0xFF80</c> for the HRAM OAM-DMA
/// trampoline → drain the background write queue → advance the 16-bit frame counter → restore, <c>reti</c> — about
/// 560 of the 1140 available VBlank machine cycles at worst case); the boot sequence (stack, work-RAM clear,
/// trampoline install, LCD-off video bring-up from a <see cref="FrameworkBootSpec"/>, interrupt arming); and the
/// <c>halt</c>-until-VBlank main-loop wait. Block copy/fill helpers live here too — the framework's own, so the
/// folder stays self-contained.
/// </summary>
internal static class FrameworkKernel {
    // The HRAM trampoline: ld a, 0xC1; ldh (0x46), a; ld a, 40; dec a; jr nz, -2; ret. Started from HRAM because the
    // OAM DMA gates the rest of the bus; the countdown outlasts the 160-machine-cycle transfer.
    private static readonly byte[] DmaTrampolineBlob = [0x3E, 0xC1, 0xE0, 0x46, 0x3E, 0x28, 0x3D, 0x20, 0xFD, 0xC9];

    /// <summary>Builds the 10-byte OAM-DMA trampoline blob (baked into ROM, copied to HRAM at boot).</summary>
    /// <returns>The trampoline bytes.</returns>
    public static byte[] BuildDmaTrampolineBlob() => [.. DmaTrampolineBlob];

    /// <summary>Emits the fixed prologue: <c>jp boot</c> at <see cref="Hw.EntryAddress"/> (exactly 3 bytes), then the
    /// whole VBlank handler at <see cref="Hw.VBlankHandlerAddress"/> — the address the cartridge's 0x0040 vector jumps
    /// to. Must be the FIRST emission into the routine.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="bootLabel">The boot label the prologue jumps to (marked later by the boot emission).</param>
    public static void EmitPrologue(Sm83Emitter emitter, int bootLabel) {
        ArgumentNullException.ThrowIfNull(emitter);

        if (emitter.Length != 0) {
            throw new InvalidOperationException(message: "The prologue must be the first emission (the VBlank handler's address is fixed at 0x0153).");
        }

        emitter.JumpAbsolute(label: bootLabel);

        if (emitter.Length != 3) {
            throw new InvalidOperationException(message: $"The prologue jump is {emitter.Length} bytes; the handler must land at 0x0153.");
        }

        EmitVBlankHandler(emitter: emitter);
    }

    /// <summary>Emits the boot sequence's hardware half: interrupts off, stack, framework + game work RAM cleared,
    /// the DMA trampoline installed in HRAM, and the LCD-off video bring-up (palettes, tiles, initial map, cleared
    /// attribute bank). Leaves the LCD OFF; the facade runs the sound/save hooks and then <see cref="EmitBootEpilogue"/>.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="spec">The boot spec.</param>
    /// <param name="dmaTrampoline">The baked trampoline table.</param>
    public static void EmitBootPrologue(Sm83Emitter emitter, FrameworkBootSpec spec, RomTable dmaTrampoline) {
        ArgumentNullException.ThrowIfNull(emitter);

        emitter.DisableInterrupts();
        emitter.LoadStackPointer(value: FrameworkMemoryMap.StackTop);

        // A clean slate: the framework page, the shadow OAM (Y = 0 hides every sprite), and the first game pages — but
        // SPLIT AROUND the 16-byte victory-share source slot (0xC0F0..0xC0FF), which the host seeds BEFORE the game
        // boots (a per-cabinet poke). Clearing it here would wipe the seed, so the fill covers 0xC000..0xC0EF and
        // 0xC100..0xC3FF and steps over the reserved slot. Everything else in the 0xC000..0xC3FF span is still zeroed.
        EmitBlockFill(emitter: emitter, destinationAddress: FrameworkMemoryMap.FrameCounter, byteCount: (ushort)(FrameworkMemoryMap.VictoryShareSource - FrameworkMemoryMap.FrameCounter), value: 0x00);
        EmitBlockFill(emitter: emitter, destinationAddress: FrameworkMemoryMap.ShadowOam, byteCount: (ushort)((FrameworkMemoryMap.FrameCounter + 0x0400) - FrameworkMemoryMap.ShadowOam), value: 0x00);

        // The HRAM OAM-DMA trampoline.
        EmitBlockCopy(emitter: emitter, sourceAddress: dmaTrampoline.Address, destinationAddress: FrameworkMemoryMap.DmaTrampoline, byteCount: (ushort)dmaTrampoline.Length);

        // Video bring-up with the LCD off (VRAM freely writable).
        emitter.XorA();
        emitter.StoreAToHighPage(port: Hw.PortLcdControl);
        emitter.StoreAToHighPage(port: Hw.PortScrollY);
        emitter.StoreAToHighPage(port: Hw.PortScrollX);
        emitter.StoreAToHighPage(port: Hw.PortVramBank);

        emitter.LoadAImmediate(value: Hw.PaletteAutoIncrement);
        emitter.StoreAToHighPage(port: Hw.PortBgPaletteIndex);
        EmitPaletteCopy(emitter: emitter, sourceAddress: spec.BgPalettes.Address, dataPort: Hw.PortBgPaletteData, byteCount: spec.BgPalettes.Length);
        emitter.LoadAImmediate(value: Hw.PaletteAutoIncrement);
        emitter.StoreAToHighPage(port: Hw.PortObjPaletteIndex);
        EmitPaletteCopy(emitter: emitter, sourceAddress: spec.ObjPalettes.Address, dataPort: Hw.PortObjPaletteData, byteCount: spec.ObjPalettes.Length);

        EmitBlockCopy(emitter: emitter, sourceAddress: spec.Tiles.Address, destinationAddress: Hw.VramTiles, byteCount: (ushort)spec.TileByteCount);
        EmitBlockCopy(emitter: emitter, sourceAddress: spec.InitialMap.Address, destinationAddress: Hw.VramBackgroundMap, byteCount: 0x0400);

        // Bank 1: every cell's attributes → palette 0.
        emitter.LoadAImmediate(value: 0x01);
        emitter.StoreAToHighPage(port: Hw.PortVramBank);
        EmitBlockFill(emitter: emitter, destinationAddress: Hw.VramBackgroundMap, byteCount: 0x0400, value: 0x00);
        emitter.XorA();
        emitter.StoreAToHighPage(port: Hw.PortVramBank);
    }

    /// <summary>Emits the boot sequence's tail: request the initial state, turn the LCD on, clear the post-boot stale
    /// IF (the seeded handoff leaves VBlank requested), enable ONLY the VBlank interrupt, and <c>ei</c>.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="spec">The boot spec.</param>
    public static void EmitBootEpilogue(Sm83Emitter emitter, FrameworkBootSpec spec) {
        ArgumentNullException.ThrowIfNull(emitter);

        emitter.LoadAImmediate(value: spec.InitialState);
        emitter.StoreAToAddress(address: FrameworkMemoryMap.PendingState);
        emitter.LoadAImmediate(value: GameStateMachine.NoPendingState);
        emitter.StoreAToAddress(address: FrameworkMemoryMap.GameState);

        emitter.LoadAImmediate(value: spec.Lcdc);
        emitter.StoreAToHighPage(port: Hw.PortLcdControl);

        emitter.XorA();
        emitter.StoreAToHighPage(port: Hw.PortInterruptFlag);
        emitter.LoadAImmediate(value: Hw.InterruptVBlankBit);
        emitter.StoreAToHighPage(port: Hw.PortInterruptEnable);
        emitter.EnableInterrupts();
    }

    /// <summary>Emits the main loop's frame wait: <c>halt</c> until the VBlank handler advances the frame counter past
    /// the loop's last-seen value (a spurious wake just halts again). Clobbers A and B.</summary>
    /// <param name="emitter">The routine emitter.</param>
    public static void EmitHaltWait(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var wait = emitter.NewLabel();

        emitter.MarkLabel(label: wait);
        emitter.Halt();
        emitter.LoadAFromAddress(address: FrameworkMemoryMap.FrameCounter);
        emitter.Load(destination: Reg8.B, source: Reg8.A);
        emitter.LoadAFromAddress(address: FrameworkMemoryMap.LastFrame);
        emitter.Arithmetic(op: AluOp.Compare, source: Reg8.B);
        emitter.JumpRelative(condition: Condition.Zero, label: wait);
        emitter.Load(destination: Reg8.A, source: Reg8.B);
        emitter.StoreAToAddress(address: FrameworkMemoryMap.LastFrame);
    }

    /// <summary>Copies <paramref name="byteCount"/> bytes from <paramref name="sourceAddress"/> to
    /// <paramref name="destinationAddress"/> (a 16-bit block copy via HL/DE/BC). Clobbers A, B, C, D, E, H, L.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="sourceAddress">The source address.</param>
    /// <param name="destinationAddress">The destination address.</param>
    /// <param name="byteCount">The byte count (≥ 1).</param>
    public static void EmitBlockCopy(Sm83Emitter emitter, ushort sourceAddress, ushort destinationAddress, ushort byteCount) {
        ArgumentNullException.ThrowIfNull(emitter);

        var loop = emitter.NewLabel();

        emitter.LoadImmediate(pair: Reg16.Hl, value: sourceAddress);
        emitter.LoadImmediate(pair: Reg16.De, value: destinationAddress);
        emitter.LoadImmediate(pair: Reg16.Bc, value: byteCount);
        emitter.MarkLabel(label: loop);
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToDe();
        emitter.Increment(pair: Reg16.De);
        emitter.Decrement(pair: Reg16.Bc);
        emitter.Load(destination: Reg8.A, source: Reg8.B);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.C);
        emitter.JumpRelative(condition: Condition.NotZero, label: loop);
    }

    /// <summary>Fills <paramref name="byteCount"/> bytes at <paramref name="destinationAddress"/> with
    /// <paramref name="value"/>. Clobbers A, B, C, D, H, L.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="destinationAddress">The destination address.</param>
    /// <param name="byteCount">The byte count (≥ 1).</param>
    /// <param name="value">The fill byte.</param>
    public static void EmitBlockFill(Sm83Emitter emitter, ushort destinationAddress, ushort byteCount, byte value) {
        ArgumentNullException.ThrowIfNull(emitter);

        var loop = emitter.NewLabel();

        emitter.LoadImmediate(pair: Reg16.Hl, value: destinationAddress);
        emitter.LoadImmediate(pair: Reg16.Bc, value: byteCount);
        emitter.LoadImmediate(destination: Reg8.D, value: value);
        emitter.MarkLabel(label: loop);
        emitter.Load(destination: Reg8.A, source: Reg8.D);
        emitter.StoreAToHlIncrement();
        emitter.Decrement(pair: Reg16.Bc);
        emitter.Load(destination: Reg8.A, source: Reg8.B);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.C);
        emitter.JumpRelative(condition: Condition.NotZero, label: loop);
    }

    // The VBlank handler (fixed at 0x0153): registers saved, shadow OAM DMA-copied via the HRAM trampoline, the
    // background write queue drained while VRAM is open, the frame counter advanced, registers restored, reti.
    private static void EmitVBlankHandler(Sm83Emitter emitter) {
        var noQueue = emitter.NewLabel();
        var drainLoop = emitter.NewLabel();
        var noCounterHigh = emitter.NewLabel();

        emitter.Push(pair: StackPair.Af);
        emitter.Push(pair: StackPair.Bc);
        emitter.Push(pair: StackPair.De);
        emitter.Push(pair: StackPair.Hl);

        emitter.Call(address: FrameworkMemoryMap.DmaTrampoline);

        emitter.LoadAFromAddress(address: FrameworkMemoryMap.VramQueueCount);
        emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        emitter.JumpRelative(condition: Condition.Zero, label: noQueue);
        emitter.Load(destination: Reg8.B, source: Reg8.A);
        emitter.LoadImmediate(pair: Reg16.Hl, value: FrameworkMemoryMap.VramQueue);
        emitter.MarkLabel(label: drainLoop);
        emitter.LoadAFromHlIncrement();
        emitter.Load(destination: Reg8.D, source: Reg8.A);
        emitter.LoadAFromHlIncrement();
        emitter.Load(destination: Reg8.E, source: Reg8.A);
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToDe();
        emitter.Decrement(register: Reg8.B);
        emitter.JumpRelative(condition: Condition.NotZero, label: drainLoop);
        emitter.XorA();
        emitter.StoreAToAddress(address: FrameworkMemoryMap.VramQueueCount);
        emitter.MarkLabel(label: noQueue);

        emitter.LoadAFromAddress(address: FrameworkMemoryMap.FrameCounter);
        emitter.Increment(register: Reg8.A);
        emitter.StoreAToAddress(address: FrameworkMemoryMap.FrameCounter);
        emitter.JumpRelative(condition: Condition.NotZero, label: noCounterHigh);
        emitter.LoadAFromAddress(address: FrameworkMemoryMap.FrameCounterHigh);
        emitter.Increment(register: Reg8.A);
        emitter.StoreAToAddress(address: FrameworkMemoryMap.FrameCounterHigh);
        emitter.MarkLabel(label: noCounterHigh);

        emitter.Pop(pair: StackPair.Hl);
        emitter.Pop(pair: StackPair.De);
        emitter.Pop(pair: StackPair.Bc);
        emitter.Pop(pair: StackPair.Af);
        emitter.ReturnFromInterrupt();
    }
    private static void EmitPaletteCopy(Sm83Emitter emitter, ushort sourceAddress, byte dataPort, int byteCount) {
        var loop = emitter.NewLabel();

        emitter.LoadImmediate(pair: Reg16.Hl, value: sourceAddress);
        emitter.LoadImmediate(destination: Reg8.B, value: (byte)byteCount);
        emitter.MarkLabel(label: loop);
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToHighPage(port: dataPort);
        emitter.Decrement(register: Reg8.B);
        emitter.JumpRelative(condition: Condition.NotZero, label: loop);
    }
}
