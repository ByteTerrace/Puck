// The opcode dispatch tables are inherently large switch statements (one case per opcode); the maintainability and
// complexity analyzers do not apply meaningfully to a CPU decode table.
#pragma warning disable CA1502 // Avoid excessive complexity
#pragma warning disable CA1505 // Avoid unmaintainable code
#pragma warning disable CA1506 // Avoid excessive class coupling

namespace Puck.HumbleGamingBrick.Ares;

/// <summary>
/// The Sharp SM83 instruction implementations and decode tables, ported from ares
/// (<c>component/processor/sm83/instructions.cpp</c> and <c>instruction.cpp</c>). Eight-bit operands are passed by
/// reference; sixteen-bit register-pair operations are handled inline because the pairs are composed properties.
/// Each access already spreads its timing across the bus sub-cycles, so the cycle accounting here mirrors ares'
/// <c>idle()</c> placements exactly.
/// </summary>
public abstract partial class AresSm83 {
    /// <summary>Fetches and executes one instruction.</summary>
    protected void Instruction() {
        var opcode = Operand();

        switch (opcode) {
            case 0x00: InstructionNop(); break;
            case 0x01: BC = Operands(); break;
            case 0x02: Write(address: BC, data: A); break;
            case 0x03: Idle(); BC++; break;
            case 0x04: InstructionInc(ref B); break;
            case 0x05: InstructionDec(ref B); break;
            case 0x06: B = Operand(); break;
            case 0x07: InstructionRlca(); break;
            case 0x08: Store(address: Operands(), data: SP); break;
            case 0x09: InstructionAddHl(source: BC); break;
            case 0x0A: A = Read(address: BC); break;
            case 0x0B: Idle(); BC--; break;
            case 0x0C: InstructionInc(ref C); break;
            case 0x0D: InstructionDec(ref C); break;
            case 0x0E: C = Operand(); break;
            case 0x0F: InstructionRrca(); break;
            case 0x10: InstructionStop(); break;
            case 0x11: DE = Operands(); break;
            case 0x12: Write(address: DE, data: A); break;
            case 0x13: Idle(); DE++; break;
            case 0x14: InstructionInc(ref D); break;
            case 0x15: InstructionDec(ref D); break;
            case 0x16: D = Operand(); break;
            case 0x17: InstructionRla(); break;
            case 0x18: InstructionJrRelative(take: true); break;
            case 0x19: InstructionAddHl(source: DE); break;
            case 0x1A: A = Read(address: DE); break;
            case 0x1B: Idle(); DE--; break;
            case 0x1C: InstructionInc(ref E); break;
            case 0x1D: InstructionDec(ref E); break;
            case 0x1E: E = Operand(); break;
            case 0x1F: InstructionRra(); break;
            case 0x20: InstructionJrRelative(take: !ZF); break;
            case 0x21: HL = Operands(); break;
            case 0x22: Write(address: HL, data: A); HL++; break;
            case 0x23: Idle(); HL++; break;
            case 0x24: InstructionInc(ref H); break;
            case 0x25: InstructionDec(ref H); break;
            case 0x26: H = Operand(); break;
            case 0x27: InstructionDaa(); break;
            case 0x28: InstructionJrRelative(take: ZF); break;
            case 0x29: InstructionAddHl(source: HL); break;
            case 0x2A: A = Read(address: HL); HL++; break;
            case 0x2B: Idle(); HL--; break;
            case 0x2C: InstructionInc(ref L); break;
            case 0x2D: InstructionDec(ref L); break;
            case 0x2E: L = Operand(); break;
            case 0x2F: InstructionCpl(); break;
            case 0x30: InstructionJrRelative(take: !CF); break;
            case 0x31: SP = Operands(); break;
            case 0x32: Write(address: HL, data: A); HL--; break;
            case 0x33: Idle(); SP++; break;
            case 0x34: InstructionIncIndirect(address: HL); break;
            case 0x35: InstructionDecIndirect(address: HL); break;
            case 0x36: Write(address: HL, data: Operand()); break;
            case 0x37: InstructionScf(); break;
            case 0x38: InstructionJrRelative(take: CF); break;
            case 0x39: InstructionAddHl(source: SP); break;
            case 0x3A: A = Read(address: HL); HL--; break;
            case 0x3B: Idle(); SP--; break;
            case 0x3C: InstructionInc(ref A); break;
            case 0x3D: InstructionDec(ref A); break;
            case 0x3E: A = Operand(); break;
            case 0x3F: InstructionCcf(); break;

            case 0x40: break; // LD B,B
            case 0x41: B = C; break;
            case 0x42: B = D; break;
            case 0x43: B = E; break;
            case 0x44: B = H; break;
            case 0x45: B = L; break;
            case 0x46: B = Read(address: HL); break;
            case 0x47: B = A; break;
            case 0x48: C = B; break;
            case 0x49: break; // LD C,C
            case 0x4A: C = D; break;
            case 0x4B: C = E; break;
            case 0x4C: C = H; break;
            case 0x4D: C = L; break;
            case 0x4E: C = Read(address: HL); break;
            case 0x4F: C = A; break;
            case 0x50: D = B; break;
            case 0x51: D = C; break;
            case 0x52: break; // LD D,D
            case 0x53: D = E; break;
            case 0x54: D = H; break;
            case 0x55: D = L; break;
            case 0x56: D = Read(address: HL); break;
            case 0x57: D = A; break;
            case 0x58: E = B; break;
            case 0x59: E = C; break;
            case 0x5A: E = D; break;
            case 0x5B: break; // LD E,E
            case 0x5C: E = H; break;
            case 0x5D: E = L; break;
            case 0x5E: E = Read(address: HL); break;
            case 0x5F: E = A; break;
            case 0x60: H = B; break;
            case 0x61: H = C; break;
            case 0x62: H = D; break;
            case 0x63: H = E; break;
            case 0x64: break; // LD H,H
            case 0x65: H = L; break;
            case 0x66: H = Read(address: HL); break;
            case 0x67: H = A; break;
            case 0x68: L = B; break;
            case 0x69: L = C; break;
            case 0x6A: L = D; break;
            case 0x6B: L = E; break;
            case 0x6C: L = H; break;
            case 0x6D: break; // LD L,L
            case 0x6E: L = Read(address: HL); break;
            case 0x6F: L = A; break;
            case 0x70: Write(address: HL, data: B); break;
            case 0x71: Write(address: HL, data: C); break;
            case 0x72: Write(address: HL, data: D); break;
            case 0x73: Write(address: HL, data: E); break;
            case 0x74: Write(address: HL, data: H); break;
            case 0x75: Write(address: HL, data: L); break;
            case 0x76: InstructionHalt(); break;
            case 0x77: Write(address: HL, data: A); break;
            case 0x78: A = B; break;
            case 0x79: A = C; break;
            case 0x7A: A = D; break;
            case 0x7B: A = E; break;
            case 0x7C: A = H; break;
            case 0x7D: A = L; break;
            case 0x7E: A = Read(address: HL); break;
            case 0x7F: break; // LD A,A

            case 0x80: A = Add(target: A, source: B); break;
            case 0x81: A = Add(target: A, source: C); break;
            case 0x82: A = Add(target: A, source: D); break;
            case 0x83: A = Add(target: A, source: E); break;
            case 0x84: A = Add(target: A, source: H); break;
            case 0x85: A = Add(target: A, source: L); break;
            case 0x86: A = Add(target: A, source: Read(address: HL)); break;
            case 0x87: A = Add(target: A, source: A); break;
            case 0x88: A = Add(target: A, source: B, carry: CF); break;
            case 0x89: A = Add(target: A, source: C, carry: CF); break;
            case 0x8A: A = Add(target: A, source: D, carry: CF); break;
            case 0x8B: A = Add(target: A, source: E, carry: CF); break;
            case 0x8C: A = Add(target: A, source: H, carry: CF); break;
            case 0x8D: A = Add(target: A, source: L, carry: CF); break;
            case 0x8E: A = Add(target: A, source: Read(address: HL), carry: CF); break;
            case 0x8F: A = Add(target: A, source: A, carry: CF); break;
            case 0x90: A = Sub(target: A, source: B); break;
            case 0x91: A = Sub(target: A, source: C); break;
            case 0x92: A = Sub(target: A, source: D); break;
            case 0x93: A = Sub(target: A, source: E); break;
            case 0x94: A = Sub(target: A, source: H); break;
            case 0x95: A = Sub(target: A, source: L); break;
            case 0x96: A = Sub(target: A, source: Read(address: HL)); break;
            case 0x97: A = Sub(target: A, source: A); break;
            case 0x98: A = Sub(target: A, source: B, carry: CF); break;
            case 0x99: A = Sub(target: A, source: C, carry: CF); break;
            case 0x9A: A = Sub(target: A, source: D, carry: CF); break;
            case 0x9B: A = Sub(target: A, source: E, carry: CF); break;
            case 0x9C: A = Sub(target: A, source: H, carry: CF); break;
            case 0x9D: A = Sub(target: A, source: L, carry: CF); break;
            case 0x9E: A = Sub(target: A, source: Read(address: HL), carry: CF); break;
            case 0x9F: A = Sub(target: A, source: A, carry: CF); break;
            case 0xA0: A = And(target: A, source: B); break;
            case 0xA1: A = And(target: A, source: C); break;
            case 0xA2: A = And(target: A, source: D); break;
            case 0xA3: A = And(target: A, source: E); break;
            case 0xA4: A = And(target: A, source: H); break;
            case 0xA5: A = And(target: A, source: L); break;
            case 0xA6: A = And(target: A, source: Read(address: HL)); break;
            case 0xA7: A = And(target: A, source: A); break;
            case 0xA8: A = Xor(target: A, source: B); break;
            case 0xA9: A = Xor(target: A, source: C); break;
            case 0xAA: A = Xor(target: A, source: D); break;
            case 0xAB: A = Xor(target: A, source: E); break;
            case 0xAC: A = Xor(target: A, source: H); break;
            case 0xAD: A = Xor(target: A, source: L); break;
            case 0xAE: A = Xor(target: A, source: Read(address: HL)); break;
            case 0xAF: A = Xor(target: A, source: A); break;
            case 0xB0: A = Or(target: A, source: B); break;
            case 0xB1: A = Or(target: A, source: C); break;
            case 0xB2: A = Or(target: A, source: D); break;
            case 0xB3: A = Or(target: A, source: E); break;
            case 0xB4: A = Or(target: A, source: H); break;
            case 0xB5: A = Or(target: A, source: L); break;
            case 0xB6: A = Or(target: A, source: Read(address: HL)); break;
            case 0xB7: A = Or(target: A, source: A); break;
            case 0xB8: Cp(target: A, source: B); break;
            case 0xB9: Cp(target: A, source: C); break;
            case 0xBA: Cp(target: A, source: D); break;
            case 0xBB: Cp(target: A, source: E); break;
            case 0xBC: Cp(target: A, source: H); break;
            case 0xBD: Cp(target: A, source: L); break;
            case 0xBE: Cp(target: A, source: Read(address: HL)); break;
            case 0xBF: Cp(target: A, source: A); break;

            case 0xC0: InstructionRetCondition(take: !ZF); break;
            case 0xC1: BC = Pop(); break;
            case 0xC2: InstructionJpCondition(take: !ZF); break;
            case 0xC3: InstructionJpCondition(take: true); break;
            case 0xC4: InstructionCallCondition(take: !ZF); break;
            case 0xC5: Idle(); Push(data: BC); break;
            case 0xC6: A = Add(target: A, source: Operand()); break;
            case 0xC7: InstructionRst(vector: 0x00); break;
            case 0xC8: InstructionRetCondition(take: ZF); break;
            case 0xC9: InstructionRet(); break;
            case 0xCA: InstructionJpCondition(take: ZF); break;
            case 0xCB: InstructionCb(); break;
            case 0xCC: InstructionCallCondition(take: ZF); break;
            case 0xCD: InstructionCallCondition(take: true); break;
            case 0xCE: A = Add(target: A, source: Operand(), carry: CF); break;
            case 0xCF: InstructionRst(vector: 0x08); break;
            case 0xD0: InstructionRetCondition(take: !CF); break;
            case 0xD1: DE = Pop(); break;
            case 0xD2: InstructionJpCondition(take: !CF); break;
            case 0xD4: InstructionCallCondition(take: !CF); break;
            case 0xD5: Idle(); Push(data: DE); break;
            case 0xD6: A = Sub(target: A, source: Operand()); break;
            case 0xD7: InstructionRst(vector: 0x10); break;
            case 0xD8: InstructionRetCondition(take: CF); break;
            case 0xD9: InstructionReti(); break;
            case 0xDA: InstructionJpCondition(take: CF); break;
            case 0xDC: InstructionCallCondition(take: CF); break;
            case 0xDE: A = Sub(target: A, source: Operand(), carry: CF); break;
            case 0xDF: InstructionRst(vector: 0x18); break;
            case 0xE0: Write(address: (ushort)(0xFF00 | Operand()), data: A); break;
            case 0xE1: HL = Pop(); break;
            case 0xE2: Write(address: (ushort)(0xFF00 | C), data: A); break;
            case 0xE5: Idle(); Push(data: HL); break;
            case 0xE6: A = And(target: A, source: Operand()); break;
            case 0xE7: InstructionRst(vector: 0x20); break;
            case 0xE8: InstructionAddSpRelative(); break;
            case 0xE9: PC = HL; break;
            case 0xEA: Write(address: Operands(), data: A); break;
            case 0xEE: A = Xor(target: A, source: Operand()); break;
            case 0xEF: InstructionRst(vector: 0x28); break;
            case 0xF0: A = Read(address: (ushort)(0xFF00 | Operand())); break;
            case 0xF1: AF = Pop(); break;
            case 0xF2: A = Read(address: (ushort)(0xFF00 | C)); break;
            case 0xF3: RegisterIme = false; break;
            case 0xF5: Idle(); Push(data: AF); break;
            case 0xF6: A = Or(target: A, source: Operand()); break;
            case 0xF7: InstructionRst(vector: 0x30); break;
            case 0xF8: InstructionLdHlSpRelative(); break;
            case 0xF9: Idle(); SP = HL; break;
            case 0xFA: A = Read(address: Operands()); break;
            case 0xFB: RegisterEi = true; break;
            case 0xFE: Cp(target: A, source: Operand()); break;
            case 0xFF: InstructionRst(vector: 0x38); break;
            default: break;
        }
    }

    private void InstructionCb() {
        var opcode = Operand();
        var index = ((opcode >> 3) & 7);

        switch (opcode) {
            case 0x00: B = Rlc(target: B); break;
            case 0x01: C = Rlc(target: C); break;
            case 0x02: D = Rlc(target: D); break;
            case 0x03: E = Rlc(target: E); break;
            case 0x04: H = Rlc(target: H); break;
            case 0x05: L = Rlc(target: L); break;
            case 0x06: Write(address: HL, data: Rlc(target: Read(address: HL))); break;
            case 0x07: A = Rlc(target: A); break;
            case 0x08: B = Rrc(target: B); break;
            case 0x09: C = Rrc(target: C); break;
            case 0x0A: D = Rrc(target: D); break;
            case 0x0B: E = Rrc(target: E); break;
            case 0x0C: H = Rrc(target: H); break;
            case 0x0D: L = Rrc(target: L); break;
            case 0x0E: Write(address: HL, data: Rrc(target: Read(address: HL))); break;
            case 0x0F: A = Rrc(target: A); break;
            case 0x10: B = Rl(target: B); break;
            case 0x11: C = Rl(target: C); break;
            case 0x12: D = Rl(target: D); break;
            case 0x13: E = Rl(target: E); break;
            case 0x14: H = Rl(target: H); break;
            case 0x15: L = Rl(target: L); break;
            case 0x16: Write(address: HL, data: Rl(target: Read(address: HL))); break;
            case 0x17: A = Rl(target: A); break;
            case 0x18: B = Rr(target: B); break;
            case 0x19: C = Rr(target: C); break;
            case 0x1A: D = Rr(target: D); break;
            case 0x1B: E = Rr(target: E); break;
            case 0x1C: H = Rr(target: H); break;
            case 0x1D: L = Rr(target: L); break;
            case 0x1E: Write(address: HL, data: Rr(target: Read(address: HL))); break;
            case 0x1F: A = Rr(target: A); break;
            case 0x20: B = Sla(target: B); break;
            case 0x21: C = Sla(target: C); break;
            case 0x22: D = Sla(target: D); break;
            case 0x23: E = Sla(target: E); break;
            case 0x24: H = Sla(target: H); break;
            case 0x25: L = Sla(target: L); break;
            case 0x26: Write(address: HL, data: Sla(target: Read(address: HL))); break;
            case 0x27: A = Sla(target: A); break;
            case 0x28: B = Sra(target: B); break;
            case 0x29: C = Sra(target: C); break;
            case 0x2A: D = Sra(target: D); break;
            case 0x2B: E = Sra(target: E); break;
            case 0x2C: H = Sra(target: H); break;
            case 0x2D: L = Sra(target: L); break;
            case 0x2E: Write(address: HL, data: Sra(target: Read(address: HL))); break;
            case 0x2F: A = Sra(target: A); break;
            case 0x30: B = Swap(target: B); break;
            case 0x31: C = Swap(target: C); break;
            case 0x32: D = Swap(target: D); break;
            case 0x33: E = Swap(target: E); break;
            case 0x34: H = Swap(target: H); break;
            case 0x35: L = Swap(target: L); break;
            case 0x36: Write(address: HL, data: Swap(target: Read(address: HL))); break;
            case 0x37: A = Swap(target: A); break;
            case 0x38: B = Srl(target: B); break;
            case 0x39: C = Srl(target: C); break;
            case 0x3A: D = Srl(target: D); break;
            case 0x3B: E = Srl(target: E); break;
            case 0x3C: H = Srl(target: H); break;
            case 0x3D: L = Srl(target: L); break;
            case 0x3E: Write(address: HL, data: Srl(target: Read(address: HL))); break;
            case 0x3F: A = Srl(target: A); break;
            default: InstructionCbBitGroup(opcode: opcode, index: index); break;
        }
    }

    // BIT/RES/SET (opcodes 0x40-0xFF).
    private void InstructionCbBitGroup(byte opcode, int index) {
        var operation = ((opcode >> 6) & 3); // 1 = BIT, 2 = RES, 3 = SET
        var register = (opcode & 7);

        if (operation == 1) {
            Bit(index: index, target: ReadCbRegister(register: register));

            return;
        }

        var value = ReadCbRegister(register: register);

        value = operation == 2 ? (byte)(value & ~(1 << index)) : (byte)(value | (1 << index));

        WriteCbRegister(register: register, value: value);
    }

    private byte ReadCbRegister(int register) =>
        register switch {
            0 => B,
            1 => C,
            2 => D,
            3 => E,
            4 => H,
            5 => L,
            6 => Read(address: HL),
            _ => A,
        };

    private void WriteCbRegister(int register, byte value) {
        switch (register) {
            case 0: B = value; break;
            case 1: C = value; break;
            case 2: D = value; break;
            case 3: E = value; break;
            case 4: H = value; break;
            case 5: L = value; break;
            case 6: Write(address: HL, data: value); break;
            default: A = value; break;
        }
    }

    // === Instruction helpers ===

    private static void InstructionNop() {
    }

    private void InstructionInc(ref byte data) =>
        data = Inc(target: data);

    private void InstructionDec(ref byte data) =>
        data = Dec(target: data);

    private void InstructionIncIndirect(ushort address) =>
        Write(address: address, data: Inc(target: Read(address: address)));

    private void InstructionDecIndirect(ushort address) =>
        Write(address: address, data: Dec(target: Read(address: address)));

    private void InstructionAddHl(ushort source) {
        Idle();

        var x = (HL + source);
        var y = ((HL & 0x0FFF) + (source & 0x0FFF));

        HL = (ushort)x;
        CF = (x > 0xFFFF);
        HF = (y > 0x0FFF);
        NF = false;
    }

    private void InstructionAddSpRelative() {
        var data = Operand();

        Idle();
        Idle();
        CF = (((SP & 0xFF) + data) > 0xFF);
        HF = (((SP & 0x0F) + (data & 0x0F)) > 0x0F);
        NF = false;
        ZF = false;
        SP = (ushort)(SP + (sbyte)data);
    }

    private void InstructionLdHlSpRelative() {
        var data = Operand();

        Idle();
        CF = (((SP & 0xFF) + data) > 0xFF);
        HF = (((SP & 0x0F) + (data & 0x0F)) > 0x0F);
        NF = false;
        ZF = false;
        HL = (ushort)(SP + (sbyte)data);
    }

    private void InstructionCcf() {
        CF = !CF;
        HF = false;
        NF = false;
    }

    private void InstructionCpl() {
        A = (byte)~A;
        HF = true;
        NF = true;
    }

    private void InstructionDaa() {
        var a = (int)A;

        if (!NF) {
            if (HF || ((a & 0x0F) > 0x09)) {
                a += 0x06;
            }

            if (CF || (a > 0x99)) {
                a += 0x60;
                CF = true;
            }
        }
        else {
            if (HF) {
                a -= 0x06;
            }

            if (CF) {
                a -= 0x60;
            }
        }

        A = (byte)a;
        HF = false;
        ZF = (A == 0);
    }

    private void InstructionScf() {
        CF = true;
        HF = false;
        NF = false;
    }

    private void InstructionHalt() {
        RegisterHalt = true;
        HaltBugTrigger();

        while (RegisterHalt) {
            Halt();
        }
    }

    private void InstructionStop() {
        if (!Stoppable()) {
            return;
        }

        RegisterStop = true;

        while (RegisterStop) {
            Stop();
        }
    }

    private void InstructionJpCondition(bool take) {
        var address = Operands();

        if (!take) {
            return;
        }

        Idle();
        PC = address;
    }

    private void InstructionJrRelative(bool take) {
        var data = Operand();

        if (!take) {
            return;
        }

        Idle();
        PC = (ushort)(PC + (sbyte)data);
    }

    private void InstructionCallCondition(bool take) {
        var address = Operands();

        if (!take) {
            return;
        }

        Idle();
        Push(data: PC);
        PC = address;
    }

    private void InstructionRet() {
        var address = Pop();

        Idle();
        PC = address;
    }

    private void InstructionRetCondition(bool take) {
        Idle();

        if (!take) {
            return;
        }

        PC = Pop();
        Idle();
    }

    private void InstructionReti() {
        var address = Pop();

        Idle();
        PC = address;
        RegisterIme = true;
    }

    private void InstructionRst(byte vector) {
        Idle();
        Push(data: PC);
        PC = vector;
    }

    private void InstructionRlca() {
        A = Rlc(target: A);
        ZF = false;
    }

    private void InstructionRrca() {
        A = Rrc(target: A);
        ZF = false;
    }

    private void InstructionRla() {
        A = Rl(target: A);
        ZF = false;
    }

    private void InstructionRra() {
        A = Rr(target: A);
        ZF = false;
    }
}
