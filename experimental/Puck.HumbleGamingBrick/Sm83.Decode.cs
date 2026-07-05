namespace Puck.HumbleGamingBrick;

/// <summary>
/// The SM83 instruction fetch and dispatch, plus the bus access primitives. The regular blocks — register-to-register
/// loads, accumulator ALU, immediate and increment forms, RST, and the CB-prefixed bit operations — are decoded by
/// their bit fields; the remaining opcodes are dispatched in four 32-entry range groups.
/// </summary>
public sealed partial class Sm83 {
    // A memory access spans one machine cycle = four CPU T-cycles, each ticking every component once in domain-aware
    // lockstep through the component clock. Where inside those four T-cycles the bus actually settles is the access
    // dot-phase, expressed here as the number of T-cycles ticked BEFORE the bus is touched: a read latches late (the
    // CPU samples the bus on the access's final T-cycle), a write commits early (the value is driven on the first).
    // These are Model B's starting hypothesis only; the exact phase is DERIVED and tuned against the hardware-accurate
    // memory-timing verdicts once the timer makes sub-instruction timing observable (Stage 3b step 3). They are not numbers copied from any reference.
    private const int CpuTCyclesPerMachineCycle = 4;
    // The access dot-phase: how many of an access's four T-cycles tick BEFORE the bus is touched. A read latches at T2
    // and a write commits on the first T-cycle. These were DERIVED by sweeping against the POST's hardware-accurate
    // timing verdicts (experimental/Puck.HumbleGamingBrick.Post): read=2 clears the whole call/jp/ret/reti/push-pop/add_sp/
    // ld_hl_sp/oam_dma timing family while keeping the timer conformance and the CPU-instruction/instruction-timing/memory-timing
    // suites green. (read=3 was the earlier Model B hypothesis — it ran one T-cycle late.)
    private const int LeadingTCyclesBeforeRead = 2;
    private const int LeadingTCyclesBeforeWrite = 0;

    private byte ReadCycle(ushort address) {
        AdvanceTCycles(count: LeadingTCyclesBeforeRead);

        var value = m_bus.ReadByte(address: address);

        AdvanceTCycles(count: (CpuTCyclesPerMachineCycle - LeadingTCyclesBeforeRead));

        return value;
    }
    private void WriteCycle(ushort address, byte value) {
        AdvanceTCycles(count: LeadingTCyclesBeforeWrite);
        m_bus.WriteByte(address: address, value: value);
        AdvanceTCycles(count: (CpuTCyclesPerMachineCycle - LeadingTCyclesBeforeWrite));
    }
    private void InternalCycle() =>
        AdvanceTCycles(count: CpuTCyclesPerMachineCycle);
    private void AdvanceTCycles(int count) {
        for (var remaining = count; (remaining != 0); --remaining) {
            m_componentClock.AdvanceCpuTCycle();
        }
    }
    private void ExecuteStop() {
        // STOP is a two-byte opcode; consume the (normally zero) operand. On Color, an armed speed switch begins here:
        // KEY1 opens the hardware-measured stall and the CPU then idles through it one machine cycle per step (see
        // StepInstruction), staying steppable at instruction granularity for the whole re-gear. Without an armed switch
        // (or on a monochrome machine) STOP parks the machine: stop mode on Color, a plain halt-alike on monochrome.
        _ = ReadNextByte();

        if (m_supportsColor && m_key1.IsSwitchArmed) {
            m_key1.BeginSwitch();
        }
        else if (m_supportsColor) {
            m_key1.EnterStop();
        }
        else {
            m_halted = true;

            m_hdma.OnCpuHalted();
        }
    }
    private byte ReadNextByte() {
        var value = ReadCycle(address: m_programCounter);

        m_programCounter = (ushort)(m_programCounter + 1);

        return value;
    }
    private ushort ReadNextWord() {
        var low = ReadNextByte();
        var high = ReadNextByte();

        return (ushort)((high << 8) | low);
    }
    private void PushWord(ushort value) {
        m_stackPointer = (ushort)(m_stackPointer - 1);
        WriteCycle(address: m_stackPointer, value: (byte)(value >> 8));
        m_stackPointer = (ushort)(m_stackPointer - 1);
        WriteCycle(address: m_stackPointer, value: (byte)value);
    }
    private ushort PopWord() {
        var low = ReadCycle(address: m_stackPointer);

        m_stackPointer = (ushort)(m_stackPointer + 1);

        var high = ReadCycle(address: m_stackPointer);

        m_stackPointer = (ushort)(m_stackPointer + 1);

        return (ushort)((high << 8) | low);
    }
    private byte ReadOperand(int index) =>
        index switch {
            0 => m_b,
            1 => m_c,
            2 => m_d,
            3 => m_e,
            4 => m_h,
            5 => m_l,
            6 => ReadCycle(address: Hl),
            _ => m_a,
        };
    private void WriteOperand(int index, byte value) {
        switch (index) {
            case 0: m_b = value; break;
            case 1: m_c = value; break;
            case 2: m_d = value; break;
            case 3: m_e = value; break;
            case 4: m_h = value; break;
            case 5: m_l = value; break;
            case 6: WriteCycle(address: Hl, value: value); break;
            default: m_a = value; break;
        }
    }
    private bool ConditionMet(int condition) =>
        condition switch {
            0 => !ZeroFlagSet,
            1 => ZeroFlagSet,
            2 => !CarryFlagSet,
            _ => CarryFlagSet,
        };

    private void Execute(byte opcode) {
        if (opcode == 0xCB) {
            ExecuteBitOperation(opcode: ReadNextByte());

            return;
        }

        if ((opcode >= 0x40) && (opcode <= 0x7F)) {
            if (opcode == 0x76) {
                // HALT with interrupts disabled while a line is already pending does not halt at all — it arms the HALT
                // bug, making the next opcode fetch fail to advance PC. An EI whose delayed enable is still counting
                // down escapes the bug: IME lands during the halt and the interrupt is serviced normally.
                if (!m_interruptMasterEnable && (m_interruptEnableCountdown == 0) && (m_interrupts.Pending != InterruptKind.None)) {
                    m_haltBug = true;
                }
                else {
                    m_halted = true;

                    m_hdma.OnCpuHalted();
                }

                return;
            }

            WriteOperand(index: ((opcode >> 3) & 7), value: ReadOperand(index: (opcode & 7)));

            return;
        }

        if ((opcode >= 0x80) && (opcode <= 0xBF)) {
            AluA(operation: ((opcode >> 3) & 7), value: ReadOperand(index: (opcode & 7)));

            return;
        }

        if ((opcode & 0xC7) == 0x04) {
            var index = ((opcode >> 3) & 7);

            WriteOperand(index: index, value: IncByte(value: ReadOperand(index: index)));

            return;
        }

        if ((opcode & 0xC7) == 0x05) {
            var index = ((opcode >> 3) & 7);

            WriteOperand(index: index, value: DecByte(value: ReadOperand(index: index)));

            return;
        }

        if ((opcode & 0xC7) == 0x06) {
            WriteOperand(index: ((opcode >> 3) & 7), value: ReadNextByte());

            return;
        }

        if ((opcode & 0xC7) == 0xC6) {
            AluA(operation: ((opcode >> 3) & 7), value: ReadNextByte());

            return;
        }

        if ((opcode & 0xC7) == 0xC7) {
            Restart(vector: (ushort)(opcode & 0x38));

            return;
        }

        if (opcode < 0x20) {
            ExecuteLowGroup0(opcode: opcode);
        }
        else if (opcode < 0x40) {
            ExecuteLowGroup1(opcode: opcode);
        }
        else if (opcode < 0xE0) {
            ExecuteControlGroup(opcode: opcode);
        }
        else {
            ExecuteHighPageGroup(opcode: opcode);
        }
    }
    private void ExecuteLowGroup0(byte opcode) {
        switch (opcode) {
            case 0x00: break;                                                            // NOP
            case 0x10: ExecuteStop(); break;                                             // STOP
            case 0x01: Bc = ReadNextWord(); break;
            case 0x11: De = ReadNextWord(); break;
            case 0x02: WriteCycle(address: Bc, value: m_a); break;
            case 0x12: WriteCycle(address: De, value: m_a); break;
            case 0x0A: m_a = ReadCycle(address: Bc); break;
            case 0x1A: m_a = ReadCycle(address: De); break;
            case 0x03: Bc = (ushort)(Bc + 1); InternalCycle(); break;
            case 0x13: De = (ushort)(De + 1); InternalCycle(); break;
            case 0x0B: Bc = (ushort)(Bc - 1); InternalCycle(); break;
            case 0x1B: De = (ushort)(De - 1); InternalCycle(); break;
            case 0x07: RotateAccumulatorLeftCircular(); break;
            case 0x0F: RotateAccumulatorRightCircular(); break;
            case 0x17: RotateAccumulatorLeft(); break;
            case 0x1F: RotateAccumulatorRight(); break;
            case 0x08: WriteStackPointerToMemory(); break;
            case 0x09: AddHl(value: Bc); break;
            case 0x19: AddHl(value: De); break;
            default: JumpRelative(taken: true); break;                                   // 0x18 JR e
        }
    }
    private void ExecuteLowGroup1(byte opcode) {
        switch (opcode) {
            case 0x21: Hl = ReadNextWord(); break;
            case 0x31: m_stackPointer = ReadNextWord(); break;
            case 0x22: WriteCycle(address: Hl, value: m_a); Hl = (ushort)(Hl + 1); break;
            case 0x32: WriteCycle(address: Hl, value: m_a); Hl = (ushort)(Hl - 1); break;
            case 0x2A: m_a = ReadCycle(address: Hl); Hl = (ushort)(Hl + 1); break;
            case 0x3A: m_a = ReadCycle(address: Hl); Hl = (ushort)(Hl - 1); break;
            case 0x23: Hl = (ushort)(Hl + 1); InternalCycle(); break;
            case 0x33: m_stackPointer = (ushort)(m_stackPointer + 1); InternalCycle(); break;
            case 0x2B: Hl = (ushort)(Hl - 1); InternalCycle(); break;
            case 0x3B: m_stackPointer = (ushort)(m_stackPointer - 1); InternalCycle(); break;
            case 0x27: DecimalAdjustAccumulator(); break;
            case 0x2F: ComplementAccumulator(); break;
            case 0x37: SetCarryFlag(); break;
            case 0x3F: ComplementCarryFlag(); break;
            case 0x29: AddHl(value: Hl); break;
            case 0x39: AddHl(value: m_stackPointer); break;
            default: JumpRelative(taken: ConditionMet(condition: ((opcode >> 3) & 3))); break; // 0x20/28/30/38 JR cc
        }
    }
    private void ExecuteControlGroup(byte opcode) {
        switch (opcode) {
            case 0xC0: case 0xC8: case 0xD0: case 0xD8: ReturnConditional(taken: ConditionMet(condition: ((opcode >> 3) & 3))); break;
            case 0xC9: m_programCounter = PopWord(); InternalCycle(); break;
            case 0xD9: m_programCounter = PopWord(); m_interruptMasterEnable = true; InternalCycle(); break;
            case 0xC1: Bc = PopWord(); break;
            case 0xD1: De = PopWord(); break;
            case 0xC5: InternalCycle(); PushWord(value: Bc); break;
            case 0xD5: InternalCycle(); PushWord(value: De); break;
            case 0xC2: case 0xCA: case 0xD2: case 0xDA: JumpAbsolute(taken: ConditionMet(condition: ((opcode >> 3) & 3))); break;
            case 0xC3: JumpAbsolute(taken: true); break;
            case 0xC4: case 0xCC: case 0xD4: case 0xDC: CallAbsolute(taken: ConditionMet(condition: ((opcode >> 3) & 3))); break;
            case 0xCD: CallAbsolute(taken: true); break;
            default: LockUp(); break;                                                     // 0xD3/0xDB/0xDD/0xDC-region illegal opcodes
        }
    }
    private void ExecuteHighPageGroup(byte opcode) {
        switch (opcode) {
            case 0xE0: WriteCycle(address: (ushort)(0xFF00 + ReadNextByte()), value: m_a); break;
            case 0xF0: m_a = ReadCycle(address: (ushort)(0xFF00 + ReadNextByte())); break;
            case 0xE1: Hl = PopWord(); break;
            case 0xF1: Af = PopWord(); break;
            case 0xE5: InternalCycle(); PushWord(value: Hl); break;
            case 0xF5: InternalCycle(); PushWord(value: Af); break;
            case 0xE2: WriteCycle(address: (ushort)(0xFF00 + m_c), value: m_a); break;
            case 0xF2: m_a = ReadCycle(address: (ushort)(0xFF00 + m_c)); break;
            case 0xE9: m_programCounter = Hl; break;
            case 0xEA: WriteCycle(address: ReadNextWord(), value: m_a); break;
            case 0xFA: m_a = ReadCycle(address: ReadNextWord()); break;
            case 0xE8: m_stackPointer = AddStackPointerOffset(offset: (sbyte)ReadNextByte()); InternalCycle(); InternalCycle(); break;
            case 0xF8: Hl = AddStackPointerOffset(offset: (sbyte)ReadNextByte()); InternalCycle(); break;
            case 0xF9: m_stackPointer = Hl; InternalCycle(); break;
            case 0xF3: m_interruptMasterEnable = false; m_interruptEnableCountdown = 0; break;
            case 0xFB: m_interruptEnableCountdown = 2; break;
            default: LockUp(); break;                                                     // 0xE3/0xE4/0xEB/0xEC/0xED/0xF4/0xFC/0xFD illegal opcodes
        }
    }
    // An undefined opcode wedges the SM83 permanently, exactly as the hardware does: it stops fetching and executing but
    // the machine's clock keeps running (see StepInstruction), so a demo that lands on a bad opcode hangs gracefully
    // rather than crashing the host. Only building a fresh machine clears the lock.
    private void LockUp() =>
        m_lockedUp = true;
    private void ExecuteBitOperation(byte opcode) {
        var operation = ((opcode >> 3) & 7);
        var index = (opcode & 7);

        switch (opcode >> 6) {
            case 0: // rotates and shifts
                WriteOperand(index: index, value: RotateOrShift(operation: operation, value: ReadOperand(index: index)));

                break;
            case 1: // BIT b, r
                TestBit(bit: operation, value: ReadOperand(index: index));

                break;
            case 2: // RES b, r
                WriteOperand(index: index, value: (byte)(ReadOperand(index: index) & ~(1 << operation)));

                break;
            default: // SET b, r
                WriteOperand(index: index, value: (byte)(ReadOperand(index: index) | (1 << operation)));

                break;
        }
    }
    private void WriteStackPointerToMemory() {
        var address = ReadNextWord();

        WriteCycle(address: address, value: (byte)m_stackPointer);
        WriteCycle(address: (ushort)(address + 1), value: (byte)(m_stackPointer >> 8));
    }
    private void JumpRelative(bool taken) {
        var offset = (sbyte)ReadNextByte();

        if (taken) {
            m_programCounter = (ushort)(m_programCounter + offset);

            InternalCycle();
        }
    }
    private void JumpAbsolute(bool taken) {
        var address = ReadNextWord();

        if (taken) {
            m_programCounter = address;

            InternalCycle();
        }
    }
    private void CallAbsolute(bool taken) {
        var address = ReadNextWord();

        if (taken) {
            InternalCycle();
            PushWord(value: m_programCounter);

            m_programCounter = address;
        }
    }
    private void ReturnConditional(bool taken) {
        InternalCycle();

        if (taken) {
            m_programCounter = PopWord();

            InternalCycle();
        }
    }
    private void Restart(ushort vector) {
        InternalCycle();
        PushWord(value: m_programCounter);

        m_programCounter = vector;
    }
}
