namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>Specifies one of the eight low registers (r0–r7) every 16-bit Thumb data/memory format can address
/// directly; the high registers are reachable only through the hi-register operations (<see cref="CoreRegister"/>).</summary>
public enum LowRegister : byte { R0 = 0, R1 = 1, R2 = 2, R3 = 3, R4 = 4, R5 = 5, R6 = 6, R7 = 7 }
/// <summary>Specifies any of the sixteen core registers (encoded 0..15) for the hi-register operations and
/// <c>bx</c> — the only Thumb-1 instructions that can name r8–r15 directly.</summary>
public enum CoreRegister : byte {
    R0 = 0, R1 = 1, R2 = 2, R3 = 3, R4 = 4, R5 = 5, R6 = 6, R7 = 7,
    R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12, Sp = 13, Lr = 14, Pc = 15,
}
/// <summary>Specifies the immediate-shift operation of Thumb format 1 (encoded 0..2; slot 3 is the add/subtract
/// format, so it is deliberately unrepresentable here).</summary>
public enum ThumbShift : byte { LogicalLeft = 0, LogicalRight = 1, ArithmeticRight = 2 }
/// <summary>Specifies one of the sixteen register-to-register ALU operations of Thumb format 4 (encoded 0..15).
/// <see cref="Test"/>, <see cref="Compare"/>, and <see cref="CompareNegated"/> set flags without writing the
/// destination; <see cref="MoveNegated"/> writes the bitwise complement of the source.</summary>
public enum ThumbAlu : byte {
    And = 0, ExclusiveOr = 1, LogicalLeft = 2, LogicalRight = 3,
    ArithmeticRight = 4, AddWithCarry = 5, SubtractWithCarry = 6, RotateRight = 7,
    Test = 8, Negate = 9, Compare = 10, CompareNegated = 11,
    Or = 12, Multiply = 13, BitClear = 14, MoveNegated = 15,
}
/// <summary>Specifies one of the fourteen usable branch conditions of Thumb format 16 (encoded 0..13; slot 14 is
/// undefined and slot 15 is <c>swi</c>, so neither is representable here). Carry conditions double as the unsigned
/// comparisons: <see cref="CarrySet"/> is unsigned ≥, <see cref="CarryClear"/> is unsigned &lt;.</summary>
public enum ThumbCondition : byte {
    Equal = 0, NotEqual = 1, CarrySet = 2, CarryClear = 3,
    Minus = 4, Plus = 5, OverflowSet = 6, OverflowClear = 7,
    UnsignedHigher = 8, UnsignedLowerOrSame = 9, SignedGreaterOrEqual = 10, SignedLess = 11,
    SignedGreater = 12, SignedLessOrEqual = 13,
}
/// <summary>Specifies a set of low registers for <see cref="ThumbEmitter.Push"/>/<see cref="ThumbEmitter.Pop"/>
/// (bit n = rn, exactly the instruction's register-list byte).</summary>
[Flags]
public enum LowRegisterMask : byte {
    /// <summary>No low registers (a bare <c>push {lr}</c>/<c>pop {pc}</c>).</summary>
    None = 0,
    /// <summary>r0.</summary>
    R0 = (1 << 0),
    /// <summary>r1.</summary>
    R1 = (1 << 1),
    /// <summary>r2.</summary>
    R2 = (1 << 2),
    /// <summary>r3.</summary>
    R3 = (1 << 3),
    /// <summary>r4.</summary>
    R4 = (1 << 4),
    /// <summary>r5.</summary>
    R5 = (1 << 5),
    /// <summary>r6.</summary>
    R6 = (1 << 6),
    /// <summary>r7.</summary>
    R7 = (1 << 7),
}
/// <summary>
/// An ARM7TDMI Thumb-1 assembler mirroring <c>Sm83Emitter</c>'s shape: typed instruction methods over the regular
/// 16-bit encodings, a one-pass label fixup for the three branch families (conditional, unconditional, and
/// <c>bl</c>), and PC-relative literal pools for 32-bit constants (<see cref="LoadConstant"/> records the value,
/// <see cref="EmitLiteralPool"/> — or <see cref="ToArray"/>'s automatic final flush — places the pool and patches
/// the loads). Output is byte-exact and deterministic: the same emission sequence always yields the same bytes.
/// Encodings follow the ARM7TDMI Thumb instruction set exactly; the emulator core
/// (<c>src/Puck.AdvancedGamingBrick/Arm7Tdmi.Thumb.cs</c>) is the oracle the encodings are verified against.
/// </summary>
public sealed class ThumbEmitter {
    private readonly List<byte> m_code = [];
    private readonly Dictionary<int, int> m_labelOffsets = [];
    private readonly List<(int PatchOffset, int Label)> m_shortBranchFixups = [];
    private readonly List<(int PatchOffset, int Label)> m_longBranchFixups = [];
    private readonly List<(int PatchOffset, int Label)> m_callFixups = [];
    private readonly List<(int PatchOffset, uint Value)> m_pendingLiterals = [];

    private int m_nextLabel;

    /// <summary>The current byte length of the emitted stream (used to place data that trails the routine).</summary>
    public int Length => m_code.Count;

    // --- Labels. --------------------------------------------------------------------------------------------------------
    /// <summary>Allocates an unbound label id; bind it with <see cref="MarkLabel"/> at the target instruction.</summary>
    public int NewLabel() => m_nextLabel++;
    /// <summary>Binds <paramref name="label"/> to the current position in the stream.</summary>
    public void MarkLabel(int label) => m_labelOffsets[label] = m_code.Count;
    // --- Format 1: shift by immediate (also the canonical low-register mov). --------------------------------------------
    /// <summary>&lt;shift&gt; rd, rs, #amount — shift a low register by an immediate. Per the architecture, an
    /// <paramref name="amount"/> of 0 means "shift by 32" for <see cref="ThumbShift.LogicalRight"/> and
    /// <see cref="ThumbShift.ArithmeticRight"/> (and a plain move for <see cref="ThumbShift.LogicalLeft"/>).</summary>
    public void ShiftImmediate(ThumbShift op, LowRegister destination, LowRegister source, int amount) {
        if ((amount < 0) || (amount > 31)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(amount), message: "A Thumb immediate shift amount is 0..31.");
        }

        EmitHalfWord(value: ((ushort)((((byte)op) << 11) | (amount << 6) | (((byte)source) << 3) | ((byte)destination))));
    }
    /// <summary>mov rd, rs — copy one low register to another (encoded as <c>lsl rd, rs, #0</c>, the canonical
    /// Thumb-1 low-register move; it sets the N/Z flags, unlike the hi-register <see cref="MoveHigh"/>).</summary>
    public void MoveRegister(LowRegister destination, LowRegister source) =>
        ShiftImmediate(amount: 0, destination: destination, op: ThumbShift.LogicalLeft, source: source);
    // --- Format 2: three-register add/subtract and the 3-bit immediates. ------------------------------------------------
    /// <summary>add rd, rs, rn — rd = rs + rn.</summary>
    public void AddRegister(LowRegister destination, LowRegister source, LowRegister operand) =>
        EmitHalfWord(value: ((ushort)(0x1800 | (((byte)operand) << 6) | (((byte)source) << 3) | ((byte)destination))));
    /// <summary>sub rd, rs, rn — rd = rs − rn.</summary>
    public void SubtractRegister(LowRegister destination, LowRegister source, LowRegister operand) =>
        EmitHalfWord(value: ((ushort)(0x1A00 | (((byte)operand) << 6) | (((byte)source) << 3) | ((byte)destination))));
    /// <summary>add rd, rs, #imm3 — rd = rs + a 3-bit immediate (0..7).</summary>
    public void AddImmediate3(LowRegister destination, LowRegister source, int value) =>
        EmitHalfWord(value: ((ushort)(0x1C00 | (ValidateImmediate3(value: value) << 6) | (((byte)source) << 3) | ((byte)destination))));
    /// <summary>sub rd, rs, #imm3 — rd = rs − a 3-bit immediate (0..7).</summary>
    public void SubtractImmediate3(LowRegister destination, LowRegister source, int value) =>
        EmitHalfWord(value: ((ushort)(0x1E00 | (ValidateImmediate3(value: value) << 6) | (((byte)source) << 3) | ((byte)destination))));
    // --- Format 3: mov/cmp/add/sub with an 8-bit immediate. -------------------------------------------------------------
    /// <summary>mov rd, #imm8 — load an 8-bit immediate.</summary>
    public void MoveImmediate(LowRegister destination, byte value) =>
        EmitHalfWord(value: ((ushort)(0x2000 | (((byte)destination) << 8) | value)));
    /// <summary>cmp rd, #imm8 — compare against an 8-bit immediate.</summary>
    public void CompareImmediate(LowRegister register, byte value) =>
        EmitHalfWord(value: ((ushort)(0x2800 | (((byte)register) << 8) | value)));
    /// <summary>add rd, #imm8 — add an 8-bit immediate in place.</summary>
    public void AddImmediate(LowRegister register, byte value) =>
        EmitHalfWord(value: ((ushort)(0x3000 | (((byte)register) << 8) | value)));
    /// <summary>sub rd, #imm8 — subtract an 8-bit immediate in place.</summary>
    public void SubtractImmediate(LowRegister register, byte value) =>
        EmitHalfWord(value: ((ushort)(0x3800 | (((byte)register) << 8) | value)));
    // --- Format 4: the register-to-register ALU grid. -------------------------------------------------------------------
    /// <summary>&lt;op&gt; rd, rs — a format-4 ALU operation (rd ∘= rs, or a flag-only test/compare).</summary>
    public void Alu(ThumbAlu op, LowRegister destination, LowRegister source) =>
        EmitHalfWord(value: ((ushort)(0x4000 | (((byte)op) << 6) | (((byte)source) << 3) | ((byte)destination))));
    // --- Format 5: hi-register operations and bx. -----------------------------------------------------------------------
    /// <summary>add rd, rs — a hi-register add (either operand may be r8–r15; no flags are set).</summary>
    public void AddHigh(CoreRegister destination, CoreRegister source) =>
        EmitHalfWord(value: BuildHiRegister(destination: destination, operation: 0, source: source));
    /// <summary>cmp rd, rs — a hi-register compare (flags only).</summary>
    public void CompareHigh(CoreRegister destination, CoreRegister source) =>
        EmitHalfWord(value: BuildHiRegister(destination: destination, operation: 1, source: source));
    /// <summary>mov rd, rs — a hi-register move (no flags; the way to read or write r8–r15, sp, or lr).</summary>
    public void MoveHigh(CoreRegister destination, CoreRegister source) =>
        EmitHalfWord(value: BuildHiRegister(destination: destination, operation: 2, source: source));
    /// <summary>bx rs — branch to the address in <paramref name="source"/>, switching state by its bit 0
    /// (<c>bx lr</c> is the standard subroutine return).</summary>
    public void BranchExchange(CoreRegister source) =>
        EmitHalfWord(value: ((ushort)(0x4700 | ((((byte)source) & 0xF) << 3))));
    // --- Format 6: PC-relative load (literal pools). --------------------------------------------------------------------
    /// <summary>ldr rd, =value — load a 32-bit constant from a PC-relative literal pool. The pool itself is placed
    /// by the next <see cref="EmitLiteralPool"/> (or by <see cref="ToArray"/>'s automatic final flush); equal values
    /// pending for the same pool share one slot. The pool must land within 1020 bytes after this instruction.</summary>
    public void LoadConstant(LowRegister destination, uint value) {
        m_pendingLiterals.Add(item: (m_code.Count, value));
        EmitHalfWord(value: ((ushort)(0x4800 | (((byte)destination) << 8))));
    }
    // --- Formats 7 and 8: load/store with a register offset. ------------------------------------------------------------
    /// <summary>ldr rd, [rb, ro] — load a word.</summary>
    public void LoadWordRegister(LowRegister destination, LowRegister baseRegister, LowRegister offsetRegister) =>
        EmitHalfWord(value: BuildRegisterOffset(baseRegister: baseRegister, offsetRegister: offsetRegister, opcode: 0x5800, register: destination));
    /// <summary>str rd, [rb, ro] — store a word.</summary>
    public void StoreWordRegister(LowRegister source, LowRegister baseRegister, LowRegister offsetRegister) =>
        EmitHalfWord(value: BuildRegisterOffset(baseRegister: baseRegister, offsetRegister: offsetRegister, opcode: 0x5000, register: source));
    /// <summary>ldrb rd, [rb, ro] — load a zero-extended byte.</summary>
    public void LoadByteRegister(LowRegister destination, LowRegister baseRegister, LowRegister offsetRegister) =>
        EmitHalfWord(value: BuildRegisterOffset(baseRegister: baseRegister, offsetRegister: offsetRegister, opcode: 0x5C00, register: destination));
    /// <summary>strb rd, [rb, ro] — store a byte.</summary>
    public void StoreByteRegister(LowRegister source, LowRegister baseRegister, LowRegister offsetRegister) =>
        EmitHalfWord(value: BuildRegisterOffset(baseRegister: baseRegister, offsetRegister: offsetRegister, opcode: 0x5400, register: source));
    /// <summary>ldrh rd, [rb, ro] — load a zero-extended halfword.</summary>
    public void LoadHalfRegister(LowRegister destination, LowRegister baseRegister, LowRegister offsetRegister) =>
        EmitHalfWord(value: BuildRegisterOffset(baseRegister: baseRegister, offsetRegister: offsetRegister, opcode: 0x5A00, register: destination));
    /// <summary>strh rd, [rb, ro] — store a halfword.</summary>
    public void StoreHalfRegister(LowRegister source, LowRegister baseRegister, LowRegister offsetRegister) =>
        EmitHalfWord(value: BuildRegisterOffset(baseRegister: baseRegister, offsetRegister: offsetRegister, opcode: 0x5200, register: source));
    /// <summary>ldrsb rd, [rb, ro] — load a sign-extended byte.</summary>
    public void LoadSignedByteRegister(LowRegister destination, LowRegister baseRegister, LowRegister offsetRegister) =>
        EmitHalfWord(value: BuildRegisterOffset(baseRegister: baseRegister, offsetRegister: offsetRegister, opcode: 0x5600, register: destination));
    /// <summary>ldrsh rd, [rb, ro] — load a sign-extended halfword.</summary>
    public void LoadSignedHalfRegister(LowRegister destination, LowRegister baseRegister, LowRegister offsetRegister) =>
        EmitHalfWord(value: BuildRegisterOffset(baseRegister: baseRegister, offsetRegister: offsetRegister, opcode: 0x5E00, register: destination));
    // --- Formats 9 and 10: load/store with an immediate offset. ---------------------------------------------------------
    /// <summary>ldr rd, [rb, #offset] — load a word from a byte offset (0..124, a multiple of 4).</summary>
    public void LoadWord(LowRegister destination, LowRegister baseRegister, int byteOffset) =>
        EmitHalfWord(value: ((ushort)(0x6800 | (ValidateScaledOffset(byteOffset: byteOffset, scale: 4) << 6) | (((byte)baseRegister) << 3) | ((byte)destination))));
    /// <summary>str rd, [rb, #offset] — store a word to a byte offset (0..124, a multiple of 4).</summary>
    public void StoreWord(LowRegister source, LowRegister baseRegister, int byteOffset) =>
        EmitHalfWord(value: ((ushort)(0x6000 | (ValidateScaledOffset(byteOffset: byteOffset, scale: 4) << 6) | (((byte)baseRegister) << 3) | ((byte)source))));
    /// <summary>ldrb rd, [rb, #offset] — load a zero-extended byte from a byte offset (0..31).</summary>
    public void LoadByte(LowRegister destination, LowRegister baseRegister, int byteOffset) =>
        EmitHalfWord(value: ((ushort)(0x7800 | (ValidateScaledOffset(byteOffset: byteOffset, scale: 1) << 6) | (((byte)baseRegister) << 3) | ((byte)destination))));
    /// <summary>strb rd, [rb, #offset] — store a byte to a byte offset (0..31).</summary>
    public void StoreByte(LowRegister source, LowRegister baseRegister, int byteOffset) =>
        EmitHalfWord(value: ((ushort)(0x7000 | (ValidateScaledOffset(byteOffset: byteOffset, scale: 1) << 6) | (((byte)baseRegister) << 3) | ((byte)source))));
    /// <summary>ldrh rd, [rb, #offset] — load a zero-extended halfword from a byte offset (0..62, even).</summary>
    public void LoadHalf(LowRegister destination, LowRegister baseRegister, int byteOffset) =>
        EmitHalfWord(value: ((ushort)(0x8800 | (ValidateScaledOffset(byteOffset: byteOffset, scale: 2) << 6) | (((byte)baseRegister) << 3) | ((byte)destination))));
    /// <summary>strh rd, [rb, #offset] — store a halfword to a byte offset (0..62, even).</summary>
    public void StoreHalf(LowRegister source, LowRegister baseRegister, int byteOffset) =>
        EmitHalfWord(value: ((ushort)(0x8000 | (ValidateScaledOffset(byteOffset: byteOffset, scale: 2) << 6) | (((byte)baseRegister) << 3) | ((byte)source))));
    // --- Format 11: SP-relative load/store. -----------------------------------------------------------------------------
    /// <summary>ldr rd, [sp, #offset] — load a word from an SP-relative byte offset (0..1020, a multiple of 4).</summary>
    public void LoadSpRelative(LowRegister destination, int byteOffset) =>
        EmitHalfWord(value: ((ushort)(0x9800 | (((byte)destination) << 8) | ValidateSpOffset(byteOffset: byteOffset))));
    /// <summary>str rd, [sp, #offset] — store a word to an SP-relative byte offset (0..1020, a multiple of 4).</summary>
    public void StoreSpRelative(LowRegister source, int byteOffset) =>
        EmitHalfWord(value: ((ushort)(0x9000 | (((byte)source) << 8) | ValidateSpOffset(byteOffset: byteOffset))));
    // --- Format 13: add an offset to SP. --------------------------------------------------------------------------------
    /// <summary>add sp, #offset — adjust SP by a signed byte offset (−508..508, a multiple of 4).</summary>
    public void AddToStackPointer(int byteOffset) {
        var magnitude = Math.Abs(value: byteOffset);

        if ((magnitude > 508) || ((magnitude & 3) != 0)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(byteOffset), message: "An SP adjustment is a multiple of 4 in -508..508.");
        }

        EmitHalfWord(value: ((ushort)(0xB000 | ((byteOffset < 0) ? 0x80 : 0x00) | (magnitude / 4))));
    }
    // --- Format 14: push/pop. -------------------------------------------------------------------------------------------
    /// <summary>push {mask, lr?} — push a low-register set (and optionally lr) onto the full-descending stack.</summary>
    public void Push(LowRegisterMask registers, bool includeLinkRegister) {
        if ((registers == LowRegisterMask.None) && !includeLinkRegister) {
            throw new ArgumentException(message: "A push needs at least one register.", paramName: nameof(registers));
        }

        EmitHalfWord(value: ((ushort)(0xB400 | (includeLinkRegister ? 0x100 : 0x000) | ((byte)registers))));
    }
    /// <summary>pop {mask, pc?} — pop a low-register set (and optionally pc; on ARMv4T a popped pc stays in Thumb).</summary>
    public void Pop(LowRegisterMask registers, bool includeProgramCounter) {
        if ((registers == LowRegisterMask.None) && !includeProgramCounter) {
            throw new ArgumentException(message: "A pop needs at least one register.", paramName: nameof(registers));
        }

        EmitHalfWord(value: ((ushort)(0xBC00 | (includeProgramCounter ? 0x100 : 0x000) | ((byte)registers))));
    }
    // --- Formats 16, 18, 19: label-resolved control flow. ---------------------------------------------------------------
    /// <summary>b&lt;cond&gt; label — conditional branch (±256-byte reach).</summary>
    public void Branch(ThumbCondition condition, int label) {
        m_shortBranchFixups.Add(item: (m_code.Count, label));
        EmitHalfWord(value: ((ushort)(0xD000 | (((byte)condition) << 8))));
    }
    /// <summary>b label — unconditional branch (±2 KiB reach).</summary>
    public void Branch(int label) {
        m_longBranchFixups.Add(item: (m_code.Count, label));
        EmitHalfWord(value: 0xE000);
    }
    /// <summary>bl label — long branch with link (the two-halfword pair; lr receives the return address with its
    /// Thumb bit set, so <c>bx lr</c> or <c>pop {pc}</c> returns).</summary>
    public void Call(int label) {
        m_callFixups.Add(item: (m_code.Count, label));
        EmitHalfWord(value: 0xF000);
        EmitHalfWord(value: 0xF800);
    }
    // --- Literal pools and finalization. --------------------------------------------------------------------------------
    /// <summary>Places the literal pool for every <see cref="LoadConstant"/> emitted since the previous pool: aligns
    /// the stream to a word boundary (padding with one zero halfword when needed), writes each distinct pending value
    /// once, and patches the loads. Place pools only where control flow cannot fall into them (after an unconditional
    /// branch or return); <see cref="ToArray"/> flushes any remainder at the very end of the stream.</summary>
    public void EmitLiteralPool() {
        if (m_pendingLiterals.Count == 0) {
            return;
        }

        if ((m_code.Count & 3) != 0) {
            EmitHalfWord(value: 0x0000);
        }

        var slotOffsets = new Dictionary<uint, int>();

        foreach (var (patchOffset, value) in m_pendingLiterals) {
            if (!slotOffsets.TryGetValue(key: value, value: out var literalOffset)) {
                literalOffset = m_code.Count;
                slotOffsets[value] = literalOffset;

                EmitHalfWord(value: ((ushort)(value & 0xFFFF)));
                EmitHalfWord(value: ((ushort)((value >> 16) & 0xFFFF)));
            }

            // word8 = (pool slot − ((load address + 4) & ~3)) / 4; the load's low byte is the word8 field.
            var anchor = (patchOffset + 4) & ~3;
            var delta = (literalOffset - anchor);

            if ((delta < 0) || (delta > 1020)) {
                throw new InvalidOperationException(message: $"A literal load at offset 0x{patchOffset:X} cannot reach its pool slot at 0x{literalOffset:X} (delta {delta}); call EmitLiteralPool closer to the load.");
            }

            m_code[patchOffset] = ((byte)(delta / 4));
        }

        m_pendingLiterals.Clear();
    }
    /// <summary>Flushes any remaining literal pool, resolves every branch fixup, and returns the finished machine
    /// code. <paramref name="baseAddress"/> is the address the routine will be loaded at; it must be word-aligned
    /// (the PC-relative literal anchor depends on it). Call it once per emitter.</summary>
    public byte[] ToArray(uint baseAddress) {
        if ((baseAddress & 3u) != 0u) {
            throw new ArgumentException(message: "The Thumb routine's base address must be word-aligned (PC-relative literal anchors depend on it).", paramName: nameof(baseAddress));
        }

        EmitLiteralPool();
        ResolveShortBranches();
        ResolveLongBranches();
        ResolveCalls();

        return m_code.ToArray();
    }

    private void ResolveShortBranches() {
        foreach (var (patchOffset, label) in m_shortBranchFixups) {
            var delta = (ResolveLabel(kind: "b<cond>", label: label) - (patchOffset + 4));
            var halfSteps = (delta >> 1);

            if ((halfSteps < -128) || (halfSteps > 127)) {
                throw new InvalidOperationException(message: $"A conditional branch's delta {delta} exceeds the ±256-byte reach; restructure with an unconditional branch.");
            }

            m_code[patchOffset] = ((byte)((sbyte)halfSteps));
        }
    }
    private void ResolveLongBranches() {
        foreach (var (patchOffset, label) in m_longBranchFixups) {
            var delta = (ResolveLabel(kind: "b", label: label) - (patchOffset + 4));
            var halfSteps = (delta >> 1);

            if ((halfSteps < -1024) || (halfSteps > 1023)) {
                throw new InvalidOperationException(message: $"An unconditional branch's delta {delta} exceeds the ±2 KiB reach; use Call.");
            }

            m_code[patchOffset] = ((byte)(halfSteps & 0xFF));
            m_code[(patchOffset + 1)] = ((byte)(0xE0 | ((halfSteps >> 8) & 0x07)));
        }
    }
    private void ResolveCalls() {
        foreach (var (patchOffset, label) in m_callFixups) {
            var delta = (ResolveLabel(kind: "bl", label: label) - (patchOffset + 4));

            if ((delta < -0x400000) || (delta > 0x3FFFFE)) {
                throw new InvalidOperationException(message: $"A bl delta {delta} exceeds the ±4 MiB reach.");
            }

            var high = (delta >> 12) & 0x7FF;
            var low = (delta >> 1) & 0x7FF;

            m_code[patchOffset] = ((byte)(high & 0xFF));
            m_code[(patchOffset + 1)] = ((byte)(0xF0 | ((high >> 8) & 0x07)));
            m_code[(patchOffset + 2)] = ((byte)(low & 0xFF));
            m_code[(patchOffset + 3)] = ((byte)(0xF8 | ((low >> 8) & 0x07)));
        }
    }
    private int ResolveLabel(string kind, int label) {
        if (!m_labelOffsets.TryGetValue(key: label, value: out var target)) {
            throw new InvalidOperationException(message: $"{kind} targets an unbound label {label}.");
        }

        return target;
    }
    private void EmitHalfWord(ushort value) {
        m_code.Add(item: ((byte)(value & 0xFF)));
        m_code.Add(item: ((byte)((value >> 8) & 0xFF)));
    }
    private static ushort BuildHiRegister(int operation, CoreRegister destination, CoreRegister source) =>
        ((ushort)(0x4400 | (operation << 8) | ((((byte)destination) & 0x8) << 4) | ((((byte)source) & 0xF) << 3) | (((byte)destination) & 0x7)));
    private static ushort BuildRegisterOffset(ushort opcode, LowRegister register, LowRegister baseRegister, LowRegister offsetRegister) =>
        ((ushort)(opcode | (((byte)offsetRegister) << 6) | (((byte)baseRegister) << 3) | ((byte)register)));
    private static int ValidateImmediate3(int value) {
        if ((value < 0) || (value > 7)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(value), message: "A 3-bit immediate is 0..7.");
        }

        return value;
    }
    private static int ValidateScaledOffset(int byteOffset, int scale) {
        if ((byteOffset < 0) || ((byteOffset % scale) != 0) || ((byteOffset / scale) > 31)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(byteOffset), message: $"The offset must be a multiple of {scale} in 0..{(31 * scale)}.");
        }

        return (byteOffset / scale);
    }
    private static int ValidateSpOffset(int byteOffset) {
        if ((byteOffset < 0) || ((byteOffset & 3) != 0) || (byteOffset > 1020)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(byteOffset), message: "An SP-relative offset is a multiple of 4 in 0..1020.");
        }

        return (byteOffset / 4);
    }
}
