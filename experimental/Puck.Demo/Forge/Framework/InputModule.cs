namespace Puck.Demo.Forge.Framework;

/// <summary>
/// The per-frame input pipeline: reads the hardware joypad into an active-high raw byte, applies an optional ATTRACT
/// SCRIPT override ((buttons, frames) pairs read from ROM, <c>0xFF</c>-terminated → <see cref="FrameworkMemoryMap.ScriptEnded"/>),
/// and derives the held / newly-pressed / previous bytes the game logic consumes. The raw byte always carries the REAL
/// buttons, so an attract loop can watch for a human press while the script drives the game.
/// </summary>
internal sealed class InputModule {
    /// <summary>The Right bit of the active-high input bytes.</summary>
    public const byte ButtonRight = 0x01;
    /// <summary>The Left bit.</summary>
    public const byte ButtonLeft = 0x02;
    /// <summary>The Up bit.</summary>
    public const byte ButtonUp = 0x04;
    /// <summary>The Down bit.</summary>
    public const byte ButtonDown = 0x08;
    /// <summary>The A bit.</summary>
    public const byte ButtonA = 0x10;
    /// <summary>The B bit.</summary>
    public const byte ButtonB = 0x20;
    /// <summary>The Select bit.</summary>
    public const byte ButtonSelect = 0x40;
    /// <summary>The Start bit.</summary>
    public const byte ButtonStart = 0x80;

    private readonly Sm83Emitter m_emitter;
    private readonly int m_tickLabel;

    /// <summary>Creates the module over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    public InputModule(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        m_emitter = emitter;
        m_tickLabel = emitter.NewLabel();
    }

    /// <summary>Emits the once-per-frame tick call (raw read → script override → held/pressed/previous).</summary>
    public void EmitTick() => m_emitter.Call(label: m_tickLabel);

    /// <summary>Emits a script start: arms the override with the script's first pair due immediately.</summary>
    /// <param name="script">The (buttons, frames) pair table, <c>0xFF</c>-terminated.</param>
    public void EmitScriptStart(RomTable script) {
        m_emitter.LoadAImmediate(value: (byte)(script.Address & 0xFF));
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptPointer);
        m_emitter.LoadAImmediate(value: (byte)((script.Address >> 8) & 0xFF));
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptPointerHigh);
        m_emitter.XorA();
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptFramesLeft);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptEnded);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptButtons);
        m_emitter.Increment(register: Reg8.A);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptOverride);
    }

    /// <summary>Emits a script stop: the next tick reads the real joypad into the held byte again.</summary>
    public void EmitScriptStop() {
        m_emitter.XorA();
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptOverride);
    }

    /// <summary>Emits the module's library subroutines (the input tick). Called once by the framework facade.</summary>
    public void EmitLibrary() {
        var useRaw = m_emitter.NewLabel();
        var scriptLoad = m_emitter.NewLabel();
        var scriptAdvance = m_emitter.NewLabel();
        var scriptHeld = m_emitter.NewLabel();
        var storeHeld = m_emitter.NewLabel();

        m_emitter.MarkLabel(label: m_tickLabel);
        EmitRawJoypadRead();

        // Script override?
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.ScriptOverride);
        m_emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        m_emitter.JumpRelative(condition: Condition.Zero, label: useRaw);

        // Scripted: consume the current pair's frames, loading the next pair when they run out.
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.ScriptFramesLeft);
        m_emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        m_emitter.JumpRelative(condition: Condition.Zero, label: scriptLoad);
        m_emitter.Decrement(register: Reg8.A);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptFramesLeft);
        m_emitter.JumpRelative(label: scriptHeld);

        m_emitter.MarkLabel(label: scriptLoad);
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.ScriptPointer);
        m_emitter.Load(destination: Reg8.L, source: Reg8.A);
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.ScriptPointerHigh);
        m_emitter.Load(destination: Reg8.H, source: Reg8.A);
        m_emitter.Load(destination: Reg8.A, source: Reg8.Memory);
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: 0xFF);
        m_emitter.JumpRelative(condition: Condition.NotZero, label: scriptAdvance);

        // Terminator: flag the end and hold nothing (the pointer stays parked on the terminator).
        m_emitter.LoadAImmediate(value: 1);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptEnded);
        m_emitter.XorA();
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptButtons);
        m_emitter.JumpRelative(label: scriptHeld);

        m_emitter.MarkLabel(label: scriptAdvance);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptButtons);
        m_emitter.Increment(pair: Reg16.Hl);
        m_emitter.Load(destination: Reg8.A, source: Reg8.Memory); // The pair's frame count (≥ 1); this frame consumes one.
        m_emitter.Decrement(register: Reg8.A);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptFramesLeft);
        m_emitter.Increment(pair: Reg16.Hl);
        m_emitter.Load(destination: Reg8.A, source: Reg8.L);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptPointer);
        m_emitter.Load(destination: Reg8.A, source: Reg8.H);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.ScriptPointerHigh);

        m_emitter.MarkLabel(label: scriptHeld);
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.ScriptButtons);
        m_emitter.JumpRelative(label: storeHeld);

        m_emitter.MarkLabel(label: useRaw);
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.InputRaw);

        m_emitter.MarkLabel(label: storeHeld);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.InputHeld);

        // Edges: pressed = held & ~previous; previous = held.
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.InputPrevious);
        m_emitter.ComplementA();
        m_emitter.Load(destination: Reg8.B, source: Reg8.A);
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.InputHeld);
        m_emitter.Arithmetic(op: AluOp.And, source: Reg8.B);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.InputPressed);
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.InputHeld);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.InputPrevious);
        m_emitter.Return();
    }

    // Reads the joypad matrix into the ACTIVE-HIGH raw byte (1 = pressed): d-pad in the low nibble, buttons in the
    // high, the Right/Left/Up/Down/A/B/Select/Start bit order of the host's JoypadButtons enum. Clobbers A and B.
    private void EmitRawJoypadRead() {
        // Direction keys (P14 low): read, settle, invert to active-high, keep the low nibble in B.
        m_emitter.LoadAImmediate(value: 0x20);
        m_emitter.StoreAToHighPage(port: Hw.PortJoypad);
        m_emitter.LoadAFromHighPage(port: Hw.PortJoypad);
        m_emitter.LoadAFromHighPage(port: Hw.PortJoypad);
        m_emitter.ComplementA();
        m_emitter.ArithmeticImmediate(op: AluOp.And, value: 0x0F);
        m_emitter.Load(destination: Reg8.B, source: Reg8.A);

        // Action buttons (P15 low): read, settle (the button matrix needs a touch longer), invert, high nibble.
        m_emitter.LoadAImmediate(value: 0x10);
        m_emitter.StoreAToHighPage(port: Hw.PortJoypad);
        m_emitter.LoadAFromHighPage(port: Hw.PortJoypad);
        m_emitter.LoadAFromHighPage(port: Hw.PortJoypad);
        m_emitter.LoadAFromHighPage(port: Hw.PortJoypad);
        m_emitter.LoadAFromHighPage(port: Hw.PortJoypad);
        m_emitter.ComplementA();
        m_emitter.ArithmeticImmediate(op: AluOp.And, value: 0x0F);
        m_emitter.Shift(op: ShiftOp.Swap, register: Reg8.A);
        m_emitter.Arithmetic(op: AluOp.Or, source: Reg8.B);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.InputRaw);

        // Deselect (both lines high) so a later read starts clean.
        m_emitter.LoadAImmediate(value: 0x30);
        m_emitter.StoreAToHighPage(port: Hw.PortJoypad);
    }
}
