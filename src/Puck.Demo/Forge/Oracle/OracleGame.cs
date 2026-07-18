using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The ORACLE cartridge: a spare, confident, text-only fortune-telling cart built on the SM83 game framework
/// (<see cref="Framework.GameFramework"/>). Its gimmick IS the engine's thesis — on a deterministic machine, fortunes
/// are always right. Two states: a title (the word ORACLE and a blinking PRESS A TO ASK) and a reading (one fortune
/// typed out with a typewriter reveal, then ASK AGAIN). The fortune index is the frame counter sampled at the instant
/// the A press registers, modulo the fortune count — it FEELS random to a human, yet under a replay it is provably
/// identical (same press tick → same fortune). A frame-perfect, power-on A press reveals the hidden thirteenth fortune.
/// </summary>
internal sealed class OracleGame {
    private const byte GameLcdc = Hw.LcdBackgroundAndObjects;

    /// <summary>The mystic palette: a deep indigo field (index 0) under pale, confident text (index 3); the two middle
    /// shades ride the ramp but no glyph uses them (the font paints index 3 on the index-0 background).</summary>
    private static HgbImage.Rgb[] Palette => [
        new HgbImage.Rgb(R: 12, G: 10, B: 28),
        new HgbImage.Rgb(R: 60, G: 62, B: 110),
        new HgbImage.Rgb(R: 140, G: 150, B: 200),
        new HgbImage.Rgb(R: 228, G: 236, B: 250),
    ];

    private readonly GameFramework m_fw;
    private readonly RomTable m_bgPalettes;
    private readonly RomTable m_objPalettes;
    private readonly RomTable m_tiles;
    private readonly RomTable m_bootMap;
    private readonly RomTable m_fortunePointers;
    private readonly RomTable m_strTitle;
    private readonly RomTable m_strPrompt;
    private readonly RomTable m_strPromptBlank;
    private readonly RomTable m_strAskAgain;
    private readonly int m_subMod12;
    private readonly int m_subSelectFortune;

    // The game's identity as a declarative manifest: the framework font into the tile bank (at base 0, so tile ids
    // never collide with the typewriter blob's newline/end markers), the mystic palette into both palette tables, and
    // one blank boot map (the state enters paint every screen directly).
    private static GameManifest BuildManifest() {
        var manifest = new GameManifest();

        manifest.DefineFontTiles();
        manifest.DefineBackgroundPalettes(name: "bg", paletteData: HgbImage.EncodePalette(palette: Palette));
        manifest.DefineObjectPalettes(name: "obj", paletteData: HgbImage.EncodePalette(palette: Palette));
        manifest.DefineScreen(name: "boot", cells: new byte[0x400], overlays: []);

        return manifest;
    }

    private OracleGame() {
        var manifest = BuildManifest();

        if (manifest.FontTileBase != 0) {
            throw new InvalidOperationException(message: $"The manifest landed the font at tile {manifest.FontTileBase}, not the pinned 0 (the typewriter blob assumes tile ids stay below the 0xFE/0xFF markers).");
        }

        // No sound (the cart is deliberately small — the reveal is the star) and a token save payload (the cart has no
        // battery state; the framework's boot save-load simply falls to these defaults and the game never reads them).
        m_fw = new GameFramework(fontTileBase: manifest.FontTileBase, saveDefaultPayload: [0x00], saveVersion: 1);

        var linked = manifest.Link(framework: m_fw);

        m_bgPalettes = linked.BackgroundPalettes;
        m_objPalettes = linked.ObjectPalettes;
        m_tiles = linked.TileBank;
        m_bootMap = linked.Screen(name: "boot").Map;

        // The thirteen fortunes as typewriter blobs, and a pointer table the reading enter indexes by fortune id.
        var pointers = new byte[(OracleProtocol.Fortunes.Length * 2)];

        for (var index = 0; (index < OracleProtocol.Fortunes.Length); index++) {
            var blob = m_fw.Data.Add(name: $"fortune-{index}", bytes: OracleProtocol.BuildFortuneBlob(text: m_fw.Text, fortune: OracleProtocol.Fortunes[index]));

            pointers[(index * 2)] = (byte)(blob.Address & 0xFF);
            pointers[((index * 2) + 1)] = (byte)((blob.Address >> 8) & 0xFF);
        }

        m_fortunePointers = m_fw.Data.Add(name: "fortune-pointers", bytes: pointers);
        m_strTitle = m_fw.Data.AddText(name: "str-title", text: OracleProtocol.TitleWord);
        m_strPrompt = m_fw.Data.AddText(name: "str-prompt", text: OracleProtocol.PromptText);
        m_strPromptBlank = m_fw.Data.AddText(name: "str-prompt-blank", text: new string(c: ' ', count: OracleProtocol.PromptText.Length));
        m_strAskAgain = m_fw.Data.AddText(name: "str-ask-again", text: OracleProtocol.AskAgainText);

        m_subMod12 = m_fw.Emitter.NewLabel();
        m_subSelectFortune = m_fw.Emitter.NewLabel();

        m_fw.States.DefineState(id: OracleProtocol.StateTitle, emitEnter: EmitTitleEnter, emitTick: EmitTitleTick);
        m_fw.States.DefineState(id: OracleProtocol.StateReading, emitEnter: EmitReadingEnter, emitTick: EmitReadingTick);
    }

    /// <summary>Assembles the cartridge.</summary>
    /// <param name="title">The header title.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(string title) {
        var game = new OracleGame();
        var spec = new FrameworkBootSpec(
            BgPalettes: game.m_bgPalettes,
            ObjPalettes: game.m_objPalettes,
            Tiles: game.m_tiles,
            TileByteCount: game.m_tiles.Length,
            InitialMap: game.m_bootMap,
            Lcdc: GameLcdc,
            InitialState: OracleProtocol.StateTitle
        );

        return game.m_fw.BuildRom(title: title, bootSpec: spec, emitGameLibrary: game.EmitGameLibrary);
    }

    // ==== The game library (shared subroutines). ========================================================================

    private void EmitGameLibrary(Sm83Emitter e) {
        EmitMod12(e: e);
        EmitSelectFortune(e: e);
    }

    // mod12: A := A mod 12 (0..11), by repeated subtraction. Touches only A and the flags — callers keep B/C across it.
    private void EmitMod12(Sm83Emitter e) {
        var loop = e.NewLabel();

        e.MarkLabel(label: m_subMod12);
        e.MarkLabel(label: loop);
        e.ArithmeticImmediate(op: AluOp.Compare, value: 12);
        e.Return(condition: Condition.Carry);            // A < 12 → done.
        e.ArithmeticImmediate(op: AluOp.Subtract, value: 12);
        e.JumpRelative(label: loop);
    }

    // selectFortune: FortuneIndex := FrameCounter16 mod 12, computed exactly from the 256 = 4 (mod 12) identity
    // (value = high*256 + low, so value mod 12 = (4*(high mod 12) + (low mod 12)) mod 12). Sampled at the instant of
    // the A press — the deterministic-entropy joke: it feels random, and under a replay it is provably identical.
    private void EmitSelectFortune(Sm83Emitter e) {
        e.MarkLabel(label: m_subSelectFortune);
        e.LoadAFromAddress(address: FrameworkMemoryMap.FrameCounterHigh);
        e.Call(label: m_subMod12);                       // A = high mod 12.
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);     // ×2.
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);     // ×4 (≤ 44).
        e.Call(label: m_subMod12);                       // A = (4*high) mod 12.
        e.Load(destination: Reg8.B, source: Reg8.A);     // B = the high-byte contribution.
        e.LoadAFromAddress(address: FrameworkMemoryMap.FrameCounter);
        e.Call(label: m_subMod12);                       // A = low mod 12.
        e.Arithmetic(op: AluOp.Add, source: Reg8.B);     // A = high-part + low-part (≤ 22).
        e.Call(label: m_subMod12);                       // A = the final index (0..11).
        e.StoreAToAddress(address: OracleProtocol.FortuneIndex);
        e.Return();
    }

    // DE := the background-map cell address of (CursorRow, CursorColumn) = 0x9800 + row*32 + column. Clobbers A, D, E,
    // H, L — but NOT C, so the caller can carry the glyph tile in C across it.
    private static void EmitComputeCursorCell(Sm83Emitter e) {
        e.LoadAFromAddress(address: OracleProtocol.CursorRow);
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.H, value: 0);
        e.AddToHl(pair: Reg16.Hl);   // ×2.
        e.AddToHl(pair: Reg16.Hl);   // ×4.
        e.AddToHl(pair: Reg16.Hl);   // ×8.
        e.AddToHl(pair: Reg16.Hl);   // ×16.
        e.AddToHl(pair: Reg16.Hl);   // ×32.
        e.LoadAFromAddress(address: OracleProtocol.CursorColumn);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.AddToHl(pair: Reg16.De);   // HL = row*32 + column.
        e.Load(destination: Reg8.A, source: Reg8.H);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)(Hw.VramBackgroundMap >> 8));
        e.Load(destination: Reg8.H, source: Reg8.A);
        e.Load(destination: Reg8.D, source: Reg8.H);
        e.Load(destination: Reg8.E, source: Reg8.L);
    }

    // ==== The two states. ================================================================================================

    private void EmitTitleEnter(Sm83Emitter e) {
        // Paint the title with the LCD off: ORACLE centred, and the prompt (shown, then the tick blinks it).
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        m_fw.Text.EmitPrintDirect(text: m_strTitle, row: OracleProtocol.TitleRow, column: OracleProtocol.TitleColumn);
        m_fw.Text.EmitPrintDirect(text: m_strPrompt, row: OracleProtocol.PromptRow, column: OracleProtocol.PromptColumn);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);

        e.XorA();
        e.StoreAToAddress(address: OracleProtocol.BlinkTimer);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: OracleProtocol.BlinkVisible);
    }
    private void EmitTitleTick(Sm83Emitter e) {
        var afterEgg = e.NewLabel();
        var noA = e.NewLabel();
        var drawBlank = e.NewLabel();
        var stay = e.NewLabel();

        // THE EASTER EGG. Fires ONLY on the first-ever title tick (PoweredOnce still 0 — power-on, frame zero) AND only
        // when A is ALREADY held on that first sampled frame. A scripted replay that holds A from reset hits this
        // frame-perfect window; a human — whose A press always arrives on a later frame, and who can never come back
        // through this branch once PoweredOnce latches — cannot. Bypasses the modulo for the hidden fortune.
        e.LoadAFromAddress(address: OracleProtocol.PoweredOnce);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: afterEgg);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: OracleProtocol.PoweredOnce);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputHeld);
        e.ArithmeticImmediate(op: AluOp.And, value: OracleProtocol.ButtonA);
        e.JumpRelative(condition: Condition.Zero, label: afterEgg);
        e.LoadAImmediate(value: OracleProtocol.HiddenFortuneIndex);
        e.StoreAToAddress(address: OracleProtocol.FortuneIndex);
        m_fw.States.EmitRequestState(id: OracleProtocol.StateReading);
        e.Return();

        // A normal A press → sample the press tick for the fortune, then read.
        e.MarkLabel(label: afterEgg);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.ArithmeticImmediate(op: AluOp.And, value: OracleProtocol.ButtonA);
        e.JumpRelative(condition: Condition.Zero, label: noA);
        e.Call(label: m_subSelectFortune);
        m_fw.States.EmitRequestState(id: OracleProtocol.StateReading);
        e.Return();

        // Otherwise, blink the prompt: every BlinkPeriod frames, toggle its visibility and redraw the row.
        e.MarkLabel(label: noA);
        e.LoadAFromAddress(address: OracleProtocol.BlinkTimer);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: OracleProtocol.BlinkTimer);
        e.ArithmeticImmediate(op: AluOp.Compare, value: OracleProtocol.BlinkPeriod);
        e.JumpRelative(condition: Condition.Carry, label: stay);   // Timer < period → keep the current phase.
        e.XorA();
        e.StoreAToAddress(address: OracleProtocol.BlinkTimer);
        e.LoadAFromAddress(address: OracleProtocol.BlinkVisible);
        e.ArithmeticImmediate(op: AluOp.Xor, value: 1);
        e.StoreAToAddress(address: OracleProtocol.BlinkVisible);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: drawBlank);
        m_fw.Text.EmitPrintQueued(text: m_strPrompt, row: OracleProtocol.PromptRow, column: OracleProtocol.PromptColumn);
        e.JumpRelative(label: stay);
        e.MarkLabel(label: drawBlank);
        m_fw.Text.EmitPrintQueued(text: m_strPromptBlank, row: OracleProtocol.PromptRow, column: OracleProtocol.PromptColumn);
        e.MarkLabel(label: stay);
    }
    private void EmitReadingEnter(Sm83Emitter e) {
        // Point the typewriter at the selected fortune's blob (pointer table indexed by FortuneIndex × 2).
        e.LoadAFromAddress(address: OracleProtocol.FortuneIndex);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A);   // index × 2.
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_fortunePointers.Address);
        e.AddToHl(pair: Reg16.De);
        e.LoadAFromHlIncrement();
        e.StoreAToAddress(address: OracleProtocol.FortunePointerLow);
        e.LoadAFromHlIncrement();
        e.StoreAToAddress(address: OracleProtocol.FortunePointerHigh);

        // Fresh reading state: cursor at the base, not yet done, first character due after the initial delay.
        e.LoadAImmediate(value: (byte)OracleProtocol.ReadingBaseRow);
        e.StoreAToAddress(address: OracleProtocol.CursorRow);
        e.LoadAImmediate(value: (byte)OracleProtocol.ReadingBaseColumn);
        e.StoreAToAddress(address: OracleProtocol.CursorColumn);
        e.XorA();
        e.StoreAToAddress(address: OracleProtocol.DoneFlag);
        e.LoadAImmediate(value: OracleProtocol.TypeDelayFrames);
        e.StoreAToAddress(address: OracleProtocol.TypeDelay);

        // Clear the screen for the reveal.
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
    }
    private void EmitReadingTick(Sm83Emitter e) {
        var handleDone = e.NewLabel();
        var emitChar = e.NewLabel();
        var typingDone = e.NewLabel();
        var newline = e.NewLabel();
        var askAgain = e.NewLabel();
        var toTitle = e.NewLabel();

        // Once the fortune is fully revealed, input is live: A asks again (a NEW fortune), B returns to the title.
        e.LoadAFromAddress(address: OracleProtocol.DoneFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: handleDone);

        // Still typing: wait out the per-character delay.
        e.LoadAFromAddress(address: OracleProtocol.TypeDelay);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: emitChar);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: OracleProtocol.TypeDelay);
        e.Return();

        // Reveal the next character: re-arm the delay, pull the next blob byte, and advance the stored pointer.
        e.MarkLabel(label: emitChar);
        e.LoadAImmediate(value: OracleProtocol.TypeDelayFrames);
        e.StoreAToAddress(address: OracleProtocol.TypeDelay);
        e.LoadAFromAddress(address: OracleProtocol.FortunePointerLow);
        e.Load(destination: Reg8.L, source: Reg8.A);
        e.LoadAFromAddress(address: OracleProtocol.FortunePointerHigh);
        e.Load(destination: Reg8.H, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.Memory);   // A = the blob byte at the pointer.
        e.Load(destination: Reg8.C, source: Reg8.A);        // Save it (the pointer advance clobbers A).
        e.Increment(pair: Reg16.Hl);
        e.Load(destination: Reg8.A, source: Reg8.L);
        e.StoreAToAddress(address: OracleProtocol.FortunePointerLow);
        e.Load(destination: Reg8.A, source: Reg8.H);
        e.StoreAToAddress(address: OracleProtocol.FortunePointerHigh);

        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Compare, value: OracleProtocol.EndMarker);
        e.JumpRelative(condition: Condition.Zero, label: typingDone);
        e.Load(destination: Reg8.A, source: Reg8.C);
        e.ArithmeticImmediate(op: AluOp.Compare, value: OracleProtocol.NewlineMarker);
        e.JumpRelative(condition: Condition.Zero, label: newline);

        // An ordinary glyph (tile id in C): queue it at the cursor, then advance the column.
        EmitComputeCursorCell(e: e);
        e.Load(destination: Reg8.A, source: Reg8.C);
        m_fw.Bg.EmitQueuePush();
        e.LoadAFromAddress(address: OracleProtocol.CursorColumn);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: OracleProtocol.CursorColumn);
        e.Return();

        // A wrapped-line break: drop to the next line and carriage-return to the base column.
        e.MarkLabel(label: newline);
        e.LoadAFromAddress(address: OracleProtocol.CursorRow);
        e.ArithmeticImmediate(op: AluOp.Add, value: (byte)OracleProtocol.ReadingLineStep);
        e.StoreAToAddress(address: OracleProtocol.CursorRow);
        e.LoadAImmediate(value: (byte)OracleProtocol.ReadingBaseColumn);
        e.StoreAToAddress(address: OracleProtocol.CursorColumn);
        e.Return();

        // The fortune is complete: latch done and post the ASK AGAIN prompt.
        e.MarkLabel(label: typingDone);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: OracleProtocol.DoneFlag);
        m_fw.Text.EmitPrintQueued(text: m_strAskAgain, row: OracleProtocol.AskAgainRow, column: OracleProtocol.AskAgainColumn);
        e.Return();

        e.MarkLabel(label: handleDone);
        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.TestBit(bit: 4, register: Reg8.B);   // A.
        e.JumpRelative(condition: Condition.NotZero, label: askAgain);
        e.TestBit(bit: 5, register: Reg8.B);   // B.
        e.JumpRelative(condition: Condition.NotZero, label: toTitle);
        e.Return();

        e.MarkLabel(label: askAgain);
        e.Call(label: m_subSelectFortune);
        m_fw.States.EmitRequestState(id: OracleProtocol.StateReading);
        e.Return();

        e.MarkLabel(label: toTitle);
        m_fw.States.EmitRequestState(id: OracleProtocol.StateTitle);
    }
}
