namespace Puck.Forge;

/// <summary>Specifies one of the SM83's eight 8-bit operand slots (encoded 0..7). <see cref="Memory"/> is the
/// <c>(hl)</c> indirection that occupies slot 6 of every register/ALU-grid instruction.</summary>
internal enum Reg8 : byte { B = 0, C = 1, D = 2, E = 3, H = 4, L = 5, Memory = 6, A = 7 }

/// <summary>Specifies a 16-bit register pair of the arithmetic/immediate group — <c>ld rr, nn</c>, <c>inc/dec rr</c>,
/// <c>add hl, rr</c> — encoded 0..3 (slot 3 is <c>sp</c>).</summary>
internal enum Reg16 : byte { Bc = 0, De = 1, Hl = 2, Sp = 3 }

/// <summary>Specifies a 16-bit register pair of the stack group — <c>push</c>/<c>pop</c> — encoded 0..3, where slot 3 is
/// <c>af</c> rather than <c>sp</c> (the one place the pair table differs).</summary>
internal enum StackPair : byte { Bc = 0, De = 1, Hl = 2, Af = 3 }

/// <summary>Specifies one of the eight accumulator ALU operations (encoded 0..7), shared by the register form
/// (<c>0x80 + op*8 + src</c>) and the immediate form (<c>0xC6 + op*8</c>).</summary>
internal enum AluOp : byte {
    Add = 0, AddWithCarry = 1, Subtract = 2, SubtractWithCarry = 3, And = 4, Xor = 5, Or = 6, Compare = 7,
}

/// <summary>Specifies one of the eight CB-prefixed rotate/shift/swap operations (encoded 0..7 in the low CB block).</summary>
internal enum ShiftOp : byte {
    RotateLeftCircular = 0, RotateRightCircular = 1, RotateLeft = 2, RotateRight = 3,
    ShiftLeftArithmetic = 4, ShiftRightArithmetic = 5, Swap = 6, ShiftRightLogical = 7,
}

/// <summary>Specifies one of the four branch conditions (encoded 0..3) for <c>jr</c>/<c>jp</c>/<c>call</c>/<c>ret</c>.</summary>
internal enum Condition : byte { NotZero = 0, Zero = 1, NoCarry = 2, Carry = 3 }

/// <summary>
/// A table-driven SM83 (the brick's CPU) assembler that emits the FULL instruction set from the chip's regular encoding
/// rather than a hand-picked opcode list: the register/ALU grid, the CB block, and the branch family are generated from
/// operand enums (<see cref="Reg8"/>, <see cref="Reg16"/>, <see cref="StackPair"/>, <see cref="AluOp"/>,
/// <see cref="ShiftOp"/>, <see cref="Condition"/>), so a future author reaches any instruction without editing this
/// class. The genuinely irregular opcodes (high-page and absolute memory access, the accumulator rotates, stack-pointer
/// arithmetic, and the control singletons) are exposed as named methods; a one-pass label fixup resolves both the
/// relative <c>jr</c> back-edges and the absolute <c>jp</c>/<c>call</c> targets. It is private to the forge, expected to
/// be lifted into a reusable <c>Puck.HumbleGamingBrickRom</c> toolkit later. Opcodes are the standard SM83 encoding (see
/// the emulator's decoder, <c>experimental/Puck.HumbleGamingBrick/Sm83.Decode.cs</c>).
/// </summary>
internal sealed class Sm83Emitter {
    private readonly List<byte> m_code = [];
    private readonly Dictionary<int, int> m_labelOffsets = [];
    private readonly List<(int PatchOffset, int Label)> m_relativeFixups = [];
    private readonly List<(int PatchOffset, int Label)> m_absoluteFixups = [];
    private int m_nextLabel;

    // --- Labels. --------------------------------------------------------------------------------------------------------
    /// <summary>Allocates an unbound label id; bind it with <see cref="MarkLabel"/> at the target instruction.</summary>
    public int NewLabel() => m_nextLabel++;

    /// <summary>Binds <paramref name="label"/> to the current position in the stream.</summary>
    public void MarkLabel(int label) => m_labelOffsets[label] = m_code.Count;

    // --- The 8-bit register/ALU grid (0x40..0xBF): generated from the operand slot 0..7. --------------------------------
    /// <summary>ld dst, src — copy one 8-bit register (or <c>(hl)</c>) to another. The <c>(hl)→(hl)</c> slot is
    /// <c>halt</c>, not a load — call <see cref="Halt"/> for it.</summary>
    public void Load(Reg8 destination, Reg8 source) {
        if ((destination == Reg8.Memory) && (source == Reg8.Memory)) {
            throw new ArgumentException(message: "ld (hl), (hl) does not exist — that opcode slot is halt; call Halt().");
        }

        m_code.Add(item: (byte)((0x40 + ((byte)destination * 8)) + (byte)source));
    }

    /// <summary>ld dst, n — load an 8-bit immediate into a register (or store it to <c>(hl)</c>).</summary>
    public void LoadImmediate(Reg8 destination, byte value) {
        m_code.Add(item: (byte)(0x06 + ((byte)destination * 8)));
        m_code.Add(item: value);
    }

    /// <summary>inc r — increment an 8-bit register (or <c>(hl)</c>).</summary>
    public void Increment(Reg8 register) => m_code.Add(item: (byte)(0x04 + ((byte)register * 8)));
    /// <summary>dec r — decrement an 8-bit register (or <c>(hl)</c>).</summary>
    public void Decrement(Reg8 register) => m_code.Add(item: (byte)(0x05 + ((byte)register * 8)));

    /// <summary>&lt;op&gt; a, src — an accumulator ALU op against an 8-bit register (or <c>(hl)</c>).</summary>
    public void Arithmetic(AluOp op, Reg8 source) => m_code.Add(item: (byte)((0x80 + ((byte)op * 8)) + (byte)source));
    /// <summary>&lt;op&gt; a, n — an accumulator ALU op against an 8-bit immediate.</summary>
    public void ArithmeticImmediate(AluOp op, byte value) {
        m_code.Add(item: (byte)(0xC6 + ((byte)op * 8)));
        m_code.Add(item: value);
    }

    // --- 16-bit register pairs. -----------------------------------------------------------------------------------------
    /// <summary>ld rr, nn — load a 16-bit immediate into a register pair.</summary>
    public void LoadImmediate(Reg16 pair, ushort value) => EmitImmediate16(opcode: (byte)(0x01 + ((byte)pair * 16)), value: value);
    /// <summary>inc rr — increment a 16-bit register pair.</summary>
    public void Increment(Reg16 pair) => m_code.Add(item: (byte)(0x03 + ((byte)pair * 16)));
    /// <summary>dec rr — decrement a 16-bit register pair.</summary>
    public void Decrement(Reg16 pair) => m_code.Add(item: (byte)(0x0B + ((byte)pair * 16)));
    /// <summary>add hl, rr — add a 16-bit register pair to HL.</summary>
    public void AddToHl(Reg16 pair) => m_code.Add(item: (byte)(0x09 + ((byte)pair * 16)));
    /// <summary>push rr — push a 16-bit stack pair.</summary>
    public void Push(StackPair pair) => m_code.Add(item: (byte)(0xC5 + ((byte)pair * 16)));
    /// <summary>pop rr — pop a 16-bit stack pair.</summary>
    public void Pop(StackPair pair) => m_code.Add(item: (byte)(0xC1 + ((byte)pair * 16)));

    // --- CB-prefixed rotate/shift/swap and single-bit test/reset/set. ---------------------------------------------------
    /// <summary>&lt;op&gt; r — a CB-prefixed rotate/shift/swap on an 8-bit register (or <c>(hl)</c>).</summary>
    public void Shift(ShiftOp op, Reg8 register) {
        m_code.Add(item: 0xCB);
        m_code.Add(item: (byte)(((byte)op * 8) + (byte)register));
    }

    /// <summary>bit n, r — sets the zero flag to the COMPLEMENT of bit <paramref name="bit"/> of <paramref name="register"/>
    /// (Z = 1 when the bit is 0, i.e. when an active-low joypad line reads as PRESSED).</summary>
    public void TestBit(int bit, Reg8 register) => EmitBitOp(baseOpcode: 0x40, bit: bit, register: register);
    /// <summary>res n, r — clear bit <paramref name="bit"/> of an 8-bit register (or <c>(hl)</c>).</summary>
    public void ResetBit(int bit, Reg8 register) => EmitBitOp(baseOpcode: 0x80, bit: bit, register: register);
    /// <summary>set n, r — set bit <paramref name="bit"/> of an 8-bit register (or <c>(hl)</c>).</summary>
    public void SetBit(int bit, Reg8 register) => EmitBitOp(baseOpcode: 0xC0, bit: bit, register: register);

    // --- Relative and absolute control flow (label-resolved in ToArray). ------------------------------------------------
    /// <summary>jr label — unconditional relative branch.</summary>
    public void JumpRelative(int label) { m_code.Add(item: 0x18); EmitRelativeFixup(label: label); }
    /// <summary>jr cc, label — relative branch taken when <paramref name="condition"/> holds (e.g. <c>nz</c>, or <c>c</c>
    /// while LY &lt; 144).</summary>
    public void JumpRelative(Condition condition, int label) { m_code.Add(item: (byte)(0x20 + ((byte)condition * 8))); EmitRelativeFixup(label: label); }

    /// <summary>jp label — unconditional ABSOLUTE jump (3 bytes). Use for a long back-edge a relative <c>jr</c> cannot
    /// reach (over ±127 bytes); requires the routine's load address passed to <see cref="ToArray"/> so the label resolves
    /// to a real 16-bit address rather than a signed offset.</summary>
    public void JumpAbsolute(int label) => EmitAbsolute(opcode: 0xC3, label: label);
    /// <summary>jp cc, label — absolute jump taken when <paramref name="condition"/> holds.</summary>
    public void JumpAbsolute(Condition condition, int label) => EmitAbsolute(opcode: (byte)(0xC2 + ((byte)condition * 8)), label: label);
    /// <summary>jp (hl) — jump to the address in HL (a computed jump for dispatch tables).</summary>
    public void JumpToHl() => m_code.Add(item: 0xE9);

    /// <summary>jp nn — unconditional absolute jump to a target KNOWN at build time (no label fixup), e.g. a fixed
    /// hardware address. Distinguished from <see cref="JumpAbsolute(int)"/> by the parameter name: pass
    /// <c>address:</c> for a literal target, <c>label:</c> for a fixup-resolved one.</summary>
    public void JumpAbsolute(ushort address) => EmitImmediate16(opcode: 0xC3, value: address);

    /// <summary>call label — push the return address and jump to the ABSOLUTE address of <paramref name="label"/> (3
    /// bytes); requires the routine's load address passed to <see cref="ToArray"/>, exactly like <see cref="JumpAbsolute(int)"/>.</summary>
    public void Call(int label) => EmitAbsolute(opcode: 0xCD, label: label);
    /// <summary>call cc, label — conditional absolute call.</summary>
    public void Call(Condition condition, int label) => EmitAbsolute(opcode: (byte)(0xC4 + ((byte)condition * 8)), label: label);
    /// <summary>call nn — push the return address and call a target KNOWN at build time (no label fixup), e.g. the
    /// HRAM OAM-DMA trampoline at <c>0xFF80</c>. Distinguished from <see cref="Call(int)"/> by the parameter name:
    /// pass <c>address:</c> for a literal target, <c>label:</c> for a fixup-resolved one.</summary>
    public void Call(ushort address) => EmitImmediate16(opcode: 0xCD, value: address);

    /// <summary>ret — return from a subroutine.</summary>
    public void Return() => m_code.Add(item: 0xC9);
    /// <summary>ret cc — conditional return.</summary>
    public void Return(Condition condition) => m_code.Add(item: (byte)(0xC0 + ((byte)condition * 8)));
    /// <summary>reti — return from an interrupt handler (enabling interrupts).</summary>
    public void ReturnFromInterrupt() => m_code.Add(item: 0xD9);
    /// <summary>rst n — call the fixed restart vector <paramref name="vector"/> (one of 0x00, 0x08, … 0x38).</summary>
    public void RestartVector(int vector) {
        if ((vector < 0) || (vector > 0x38) || ((vector & ~0x38) != 0)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(vector), message: "A restart vector is one of 0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38.");
        }

        m_code.Add(item: (byte)(0xC7 + vector));
    }

    // --- Accumulator / memory access outside the register grid (each its own opcode). -----------------------------------
    /// <summary>ldh (n), a — write A to the high page (0xFF00 + <paramref name="port"/>).</summary>
    public void StoreAToHighPage(byte port) { m_code.Add(item: 0xE0); m_code.Add(item: port); }
    /// <summary>ldh a, (n) — read A from the high page (0xFF00 + <paramref name="port"/>).</summary>
    public void LoadAFromHighPage(byte port) { m_code.Add(item: 0xF0); m_code.Add(item: port); }
    /// <summary>ld (c), a — write A to the high page addressed by C (0xFF00 + C).</summary>
    public void StoreAToHighPageC() => m_code.Add(item: 0xE2);
    /// <summary>ld a, (c) — read A from the high page addressed by C (0xFF00 + C).</summary>
    public void LoadAFromHighPageC() => m_code.Add(item: 0xF2);

    /// <summary>ld (nn), a — write A to an absolute address (e.g. an OAM byte).</summary>
    public void StoreAToAddress(ushort address) => EmitImmediate16(opcode: 0xEA, value: address);
    /// <summary>ld a, (nn) — read A from an absolute address (e.g. a work-RAM sensor byte).</summary>
    public void LoadAFromAddress(ushort address) => EmitImmediate16(opcode: 0xFA, value: address);

    /// <summary>ld a, (bc)</summary>
    public void LoadAFromBc() => m_code.Add(item: 0x0A);
    /// <summary>ld a, (de)</summary>
    public void LoadAFromDe() => m_code.Add(item: 0x1A);
    /// <summary>ld (bc), a</summary>
    public void StoreAToBc() => m_code.Add(item: 0x02);
    /// <summary>ld (de), a</summary>
    public void StoreAToDe() => m_code.Add(item: 0x12);
    /// <summary>ld a, (hl+) — read from HL, then increment HL.</summary>
    public void LoadAFromHlIncrement() => m_code.Add(item: 0x2A);
    /// <summary>ld (hl+), a — write A to HL, then increment HL.</summary>
    public void StoreAToHlIncrement() => m_code.Add(item: 0x22);
    /// <summary>ld a, (hl-) — read from HL, then decrement HL.</summary>
    public void LoadAFromHlDecrement() => m_code.Add(item: 0x3A);
    /// <summary>ld (hl-), a — write A to HL, then decrement HL.</summary>
    public void StoreAToHlDecrement() => m_code.Add(item: 0x32);

    // --- Stack-pointer specials. ----------------------------------------------------------------------------------------
    /// <summary>ld (nn), sp — write SP to an absolute address.</summary>
    public void StoreStackPointerToAddress(ushort address) => EmitImmediate16(opcode: 0x08, value: address);
    /// <summary>ld sp, hl — copy HL into SP.</summary>
    public void LoadStackPointerFromHl() => m_code.Add(item: 0xF9);
    /// <summary>ld hl, sp+e — load HL with SP plus a signed offset.</summary>
    public void LoadHlFromStackPointerOffset(sbyte offset) { m_code.Add(item: 0xF8); m_code.Add(item: (byte)offset); }
    /// <summary>add sp, e — add a signed offset to SP.</summary>
    public void AddToStackPointer(sbyte offset) { m_code.Add(item: 0xE8); m_code.Add(item: (byte)offset); }

    // --- Accumulator rotates (distinct from the CB forms: these always clear the zero flag). ----------------------------
    /// <summary>rlca — rotate A left, bit 7 into carry and bit 0.</summary>
    public void RotateLeftCircularA() => m_code.Add(item: 0x07);
    /// <summary>rrca — rotate A right, bit 0 into carry and bit 7.</summary>
    public void RotateRightCircularA() => m_code.Add(item: 0x0F);
    /// <summary>rla — rotate A left through the carry.</summary>
    public void RotateLeftA() => m_code.Add(item: 0x17);
    /// <summary>rra — rotate A right through the carry.</summary>
    public void RotateRightA() => m_code.Add(item: 0x1F);
    /// <summary>daa — decimal-adjust A after a BCD add/subtract.</summary>
    public void DecimalAdjustA() => m_code.Add(item: 0x27);
    /// <summary>cpl — one's-complement A (flip every bit); turns an active-low joypad read into an active-high mask.</summary>
    public void ComplementA() => m_code.Add(item: 0x2F);
    /// <summary>scf — set the carry flag.</summary>
    public void SetCarryFlag() => m_code.Add(item: 0x37);
    /// <summary>ccf — complement the carry flag.</summary>
    public void ComplementCarryFlag() => m_code.Add(item: 0x3F);

    // --- Control singletons. --------------------------------------------------------------------------------------------
    /// <summary>nop</summary>
    public void Nop() => m_code.Add(item: 0x00);
    /// <summary>halt — stop the CPU until an interrupt.</summary>
    public void Halt() => m_code.Add(item: 0x76);
    /// <summary>stop — the two-byte 0x10 0x00 low-power/speed-switch instruction.</summary>
    public void Stop() { m_code.Add(item: 0x10); m_code.Add(item: 0x00); }
    /// <summary>di — disable interrupts.</summary>
    public void DisableInterrupts() => m_code.Add(item: 0xF3);
    /// <summary>ei — enable interrupts.</summary>
    public void EnableInterrupts() => m_code.Add(item: 0xFB);

    // --- Ergonomic sugar for the handful of grid ops that read better named than spelled out. ---------------------------
    /// <summary>xor a — A = 0, the cheapest zero (<see cref="Arithmetic"/> with <see cref="AluOp.Xor"/> and
    /// <see cref="Reg8.A"/>).</summary>
    public void XorA() => Arithmetic(op: AluOp.Xor, source: Reg8.A);
    /// <summary>ld a, n — the pervasive "load a constant into A".</summary>
    public void LoadAImmediate(byte value) => LoadImmediate(destination: Reg8.A, value: value);
    /// <summary>ld sp, nn — set the stack pointer (a named <see cref="LoadImmediate(Reg16, ushort)"/> for readability).</summary>
    public void LoadStackPointer(ushort value) => LoadImmediate(pair: Reg16.Sp, value: value);

    /// <summary>The current byte length of the emitted stream (used to place data that trails the routine).</summary>
    public int Length => m_code.Count;

    /// <summary>Resolves the fixups and returns the finished machine code. <paramref name="baseAddress"/> is the address
    /// the routine will be LOADED at (0 for a position-independent routine); absolute jumps add it to the label offset.</summary>
    public byte[] ToArray(ushort baseAddress = 0) {
        foreach (var (patchOffset, label) in m_relativeFixups) {
            if (!m_labelOffsets.TryGetValue(key: label, value: out var target)) {
                throw new InvalidOperationException(message: $"jr targets an unbound label {label}.");
            }

            // A relative jump is measured from the address of the instruction AFTER the offset byte.
            var delta = (target - (patchOffset + 1));

            if ((delta < -128) || (delta > 127)) {
                throw new InvalidOperationException(message: $"jr delta {delta} is out of the signed-byte range; use JumpAbsolute.");
            }

            m_code[patchOffset] = (byte)(sbyte)delta;
        }

        foreach (var (patchOffset, label) in m_absoluteFixups) {
            if (!m_labelOffsets.TryGetValue(key: label, value: out var target)) {
                throw new InvalidOperationException(message: $"jp targets an unbound label {label}.");
            }

            var address = (baseAddress + target);

            m_code[patchOffset] = (byte)(address & 0xFF);
            m_code[(patchOffset + 1)] = (byte)((address >> 8) & 0xFF);
        }

        return m_code.ToArray();
    }

    private void EmitBitOp(byte baseOpcode, int bit, Reg8 register) {
        if ((bit < 0) || (bit > 7)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(bit));
        }

        m_code.Add(item: 0xCB);
        m_code.Add(item: (byte)((baseOpcode + (bit * 8)) + (byte)register));
    }
    private void EmitAbsolute(byte opcode, int label) {
        m_code.Add(item: opcode);
        m_absoluteFixups.Add(item: (m_code.Count, label));
        m_code.Add(item: 0x00); // low byte placeholder, patched in ToArray
        m_code.Add(item: 0x00); // high byte placeholder
    }
    private void EmitImmediate16(byte opcode, ushort value) {
        m_code.Add(item: opcode);
        m_code.Add(item: (byte)(value & 0xFF));
        m_code.Add(item: (byte)((value >> 8) & 0xFF));
    }
    private void EmitRelativeFixup(int label) {
        m_relativeFixups.Add(item: (m_code.Count, label));
        m_code.Add(item: 0x00); // Placeholder; patched in ToArray.
    }
}
