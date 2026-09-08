namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The minimal polling kernel: direct boot with a zeroed BIOS means no BIOS SWI calls and no IRQ dispatch (the IRQ
/// vector at 0x00000018 sits in zeroed BIOS ROM), so the kernel is a polled main loop — V-blank sync by watching
/// VCOUNT, keypad held/pressed/previous edges read from KEYINPUT into the <see cref="AgbForgeMemoryMap"/> state
/// block, a frame counter, the house 16-bit LCG PRNG (state × 5 + 1, output = the state's high byte; seed = the
/// frame counter XOR 0xA5C3, sampled at an input edge — input entropy only, never a wall clock), and a mode-3
/// rectangle-fill draw helper. Emit the per-tick pieces inline and <see cref="EmitLibrary"/> once, anywhere control
/// flow cannot fall into it.
/// </summary>
public sealed class AgbForgeKernel {
    // The PRNG seed whitening constant (seed = FrameCounter16 XOR 0xA5C3, so an early press never seeds near zero).
    private const ushort SeedWhitenConstant = 0xA5C3;

    private readonly ThumbEmitter m_emitter;
    private readonly int m_fillRectLabel;
    private readonly int m_frameSyncLabel;
    private readonly int m_prngNextLabel;

    /// <summary>Creates the kernel over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    public AgbForgeKernel(ThumbEmitter emitter) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        m_emitter = emitter;
        m_fillRectLabel = emitter.NewLabel();
        m_frameSyncLabel = emitter.NewLabel();
        m_prngNextLabel = emitter.NewLabel();
    }

    /// <summary>Emits the boot prologue: zero-fills the framework state region and sets DISPCNT to mode 3 with BG2
    /// enabled. Clobbers r0–r2. The game's own screen clear and initial draw follow it.</summary>
    public void EmitBootPrologue() {
        var clearLoop = m_emitter.NewLabel();

        m_emitter.LoadConstant(destination: LowRegister.R0, value: AgbForgeMemoryMap.StateBase);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: (AgbForgeMemoryMap.FrameworkByteCount / 4));
        m_emitter.MoveImmediate(destination: LowRegister.R2, value: 0);
        m_emitter.MarkLabel(label: clearLoop);
        m_emitter.StoreWord(baseRegister: LowRegister.R0, byteOffset: 0, source: LowRegister.R2);
        m_emitter.AddImmediate(register: LowRegister.R0, value: 4);
        m_emitter.SubtractImmediate(register: LowRegister.R1, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: clearLoop);

        m_emitter.LoadConstant(destination: LowRegister.R0, value: AgbHw.DisplayControl);
        m_emitter.LoadConstant(destination: LowRegister.R1, value: AgbHw.Mode3WithBg2);
        m_emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: 0, source: LowRegister.R1);
    }
    /// <summary>Emits <c>bl frameSync</c> — one call per main-loop iteration. The routine blocks until the next
    /// V-blank starts, refreshes the held/pressed/previous input halfwords, and increments the frame counter.
    /// Clobbers r0–r2 (and lr).</summary>
    public void EmitFrameSyncCall() => m_emitter.Call(label: m_frameSyncLabel);
    /// <summary>Emits <c>bl fillRect</c> — fills a mode-3 rectangle. Register contract: r0 = x, r1 = y,
    /// r2 = width, r3 = height (all in pixels), r4 = BGR555 colour. Clobbers r1 and r3 (and lr); preserves
    /// r0, r2, r4–r7.</summary>
    public void EmitFillRectCall() => m_emitter.Call(label: m_fillRectLabel);
    /// <summary>Emits <c>bl prngNext</c> — advances the LCG and returns its output byte (0..255) in r0.
    /// Clobbers r0–r2 (and lr).</summary>
    public void EmitPrngNextCall() => m_emitter.Call(label: m_prngNextLabel);
    /// <summary>Emits the inline PRNG seeding: state = FrameCounter16 XOR 0xA5C3, sampled at the current instant
    /// (call it on the title screen's START press edge). Clobbers r0–r2.</summary>
    public void EmitPrngSeedFromFrameCounter() {
        m_emitter.LoadConstant(destination: LowRegister.R1, value: AgbForgeMemoryMap.StateBase);
        m_emitter.LoadWord(baseRegister: LowRegister.R1, byteOffset: 0, destination: LowRegister.R0);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: SeedWhitenConstant);
        m_emitter.Alu(destination: LowRegister.R0, op: ThumbAlu.ExclusiveOr, source: LowRegister.R2);
        m_emitter.StoreHalf(baseRegister: LowRegister.R1, byteOffset: AgbForgeMemoryMap.PrngStateOffset, source: LowRegister.R0);
    }
    /// <summary>Emits the kernel's three subroutines. Call exactly once, at a point control flow cannot reach by
    /// falling through (after the main loop's back edge).</summary>
    public void EmitLibrary() {
        EmitFrameSyncRoutine();
        EmitFillRectRoutine();
        EmitPrngNextRoutine();
    }

    // frameSync: waits out any in-progress V-blank, waits for the next one to start, polls KEYINPUT into the
    // held/pressed/previous halfwords (active-high), and increments the 32-bit frame counter.
    private void EmitFrameSyncRoutine() {
        var waitOut = m_emitter.NewLabel();
        var waitIn = m_emitter.NewLabel();

        m_emitter.MarkLabel(label: m_frameSyncLabel);
        m_emitter.LoadConstant(destination: LowRegister.R0, value: AgbHw.IoBase);
        m_emitter.MarkLabel(label: waitOut);
        m_emitter.LoadHalf(baseRegister: LowRegister.R0, byteOffset: AgbHw.VerticalCounterOffset, destination: LowRegister.R1);
        m_emitter.CompareImmediate(register: LowRegister.R1, value: ((byte)AgbHw.ScreenHeight));
        m_emitter.Branch(condition: ThumbCondition.CarrySet, label: waitOut);
        m_emitter.MarkLabel(label: waitIn);
        m_emitter.LoadHalf(baseRegister: LowRegister.R0, byteOffset: AgbHw.VerticalCounterOffset, destination: LowRegister.R1);
        m_emitter.CompareImmediate(register: LowRegister.R1, value: ((byte)AgbHw.ScreenHeight));
        m_emitter.Branch(condition: ThumbCondition.CarryClear, label: waitIn);

        m_emitter.LoadConstant(destination: LowRegister.R0, value: AgbHw.KeyInput);
        m_emitter.LoadHalf(baseRegister: LowRegister.R0, byteOffset: 0, destination: LowRegister.R1);
        m_emitter.Alu(destination: LowRegister.R1, op: ThumbAlu.MoveNegated, source: LowRegister.R1);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: AgbHw.KeyMask);
        m_emitter.Alu(destination: LowRegister.R1, op: ThumbAlu.And, source: LowRegister.R2);
        m_emitter.LoadConstant(destination: LowRegister.R0, value: AgbForgeMemoryMap.StateBase);
        m_emitter.LoadHalf(baseRegister: LowRegister.R0, byteOffset: AgbForgeMemoryMap.InputHeldOffset, destination: LowRegister.R2);
        m_emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: AgbForgeMemoryMap.InputPreviousOffset, source: LowRegister.R2);
        m_emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: AgbForgeMemoryMap.InputHeldOffset, source: LowRegister.R1);
        m_emitter.Alu(destination: LowRegister.R2, op: ThumbAlu.MoveNegated, source: LowRegister.R2);
        m_emitter.Alu(destination: LowRegister.R2, op: ThumbAlu.And, source: LowRegister.R1);
        m_emitter.StoreHalf(baseRegister: LowRegister.R0, byteOffset: AgbForgeMemoryMap.InputPressedOffset, source: LowRegister.R2);

        m_emitter.LoadWord(baseRegister: LowRegister.R0, byteOffset: 0, destination: LowRegister.R1);
        m_emitter.AddImmediate(register: LowRegister.R1, value: 1);
        m_emitter.StoreWord(baseRegister: LowRegister.R0, byteOffset: 0, source: LowRegister.R1);
        m_emitter.BranchExchange(source: CoreRegister.Lr);
    }
    // fillRect: r0 = x, r1 = y, r2 = width, r3 = height, r4 = BGR555 colour. dst = VRAM + (y*240 + x)*2; the row
    // stride skip is (240 - width)*2 bytes.
    private void EmitFillRectRoutine() {
        var rowLoop = m_emitter.NewLabel();
        var columnLoop = m_emitter.NewLabel();

        m_emitter.MarkLabel(label: m_fillRectLabel);
        m_emitter.Push(includeLinkRegister: false, registers: LowRegisterMask.R5 | LowRegisterMask.R6 | LowRegisterMask.R7);
        m_emitter.LoadConstant(destination: LowRegister.R5, value: AgbHw.VideoRam);
        m_emitter.MoveImmediate(destination: LowRegister.R6, value: ((byte)AgbHw.ScreenWidth));
        m_emitter.Alu(destination: LowRegister.R1, op: ThumbAlu.Multiply, source: LowRegister.R6);
        m_emitter.AddRegister(destination: LowRegister.R1, operand: LowRegister.R0, source: LowRegister.R1);
        m_emitter.ShiftImmediate(amount: 1, destination: LowRegister.R1, op: ThumbShift.LogicalLeft, source: LowRegister.R1);
        m_emitter.AddRegister(destination: LowRegister.R5, operand: LowRegister.R1, source: LowRegister.R5);
        m_emitter.MoveImmediate(destination: LowRegister.R6, value: ((byte)AgbHw.ScreenWidth));
        m_emitter.SubtractRegister(destination: LowRegister.R6, operand: LowRegister.R2, source: LowRegister.R6);
        m_emitter.ShiftImmediate(amount: 1, destination: LowRegister.R6, op: ThumbShift.LogicalLeft, source: LowRegister.R6);
        m_emitter.MarkLabel(label: rowLoop);
        m_emitter.MoveRegister(destination: LowRegister.R7, source: LowRegister.R2);
        m_emitter.MarkLabel(label: columnLoop);
        m_emitter.StoreHalf(baseRegister: LowRegister.R5, byteOffset: 0, source: LowRegister.R4);
        m_emitter.AddImmediate(register: LowRegister.R5, value: 2);
        m_emitter.SubtractImmediate(register: LowRegister.R7, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: columnLoop);
        m_emitter.AddRegister(destination: LowRegister.R5, operand: LowRegister.R6, source: LowRegister.R5);
        m_emitter.SubtractImmediate(register: LowRegister.R3, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: rowLoop);
        m_emitter.Pop(includeProgramCounter: false, registers: LowRegisterMask.R5 | LowRegisterMask.R6 | LowRegisterMask.R7);
        m_emitter.BranchExchange(source: CoreRegister.Lr);
    }
    // prngNext: state = state*5 + 1 (mod 2^16, the halfword store truncates); r0 = the state's high byte.
    private void EmitPrngNextRoutine() {
        m_emitter.MarkLabel(label: m_prngNextLabel);
        m_emitter.LoadConstant(destination: LowRegister.R1, value: AgbForgeMemoryMap.StateBase);
        m_emitter.LoadHalf(baseRegister: LowRegister.R1, byteOffset: AgbForgeMemoryMap.PrngStateOffset, destination: LowRegister.R0);
        m_emitter.ShiftImmediate(amount: 2, destination: LowRegister.R2, op: ThumbShift.LogicalLeft, source: LowRegister.R0);
        m_emitter.AddRegister(destination: LowRegister.R0, operand: LowRegister.R2, source: LowRegister.R0);
        m_emitter.AddImmediate(register: LowRegister.R0, value: 1);
        m_emitter.StoreHalf(baseRegister: LowRegister.R1, byteOffset: AgbForgeMemoryMap.PrngStateOffset, source: LowRegister.R0);
        m_emitter.LoadHalf(baseRegister: LowRegister.R1, byteOffset: AgbForgeMemoryMap.PrngStateOffset, destination: LowRegister.R0);
        m_emitter.ShiftImmediate(amount: 8, destination: LowRegister.R0, op: ThumbShift.LogicalRight, source: LowRegister.R0);
        m_emitter.BranchExchange(source: CoreRegister.Lr);
    }
}
