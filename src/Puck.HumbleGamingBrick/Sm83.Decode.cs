namespace Puck.HumbleGamingBrick;

/// <summary>
/// The SM83 instruction fetch and dispatch, plus the bus access primitives. The regular blocks — register-to-register
/// loads, accumulator ALU, immediate and increment forms, RST, and the CB-prefixed bit operations — are decoded by
/// their bit fields; the remaining opcodes are dispatched in four 32-entry range groups.
/// <para>
/// The bus primitives carry the access's phase, not just its cost. A machine cycle's four T-cycles hold two bus
/// instants: the drive instant at its start, where the address and a write's data reach the peripheral, and the latch
/// instant two T-cycles later, where the CPU samples the data lines. Cycles are ticked lazily against
/// <c>m_busCycleDebt</c> so a write can settle before its own drive instant, which several I/O registers do; anything
/// that samples component state outside an access settles the debt first.
/// </para>
/// </summary>
public sealed partial class Sm83 {
    // A memory access spans one machine cycle = four CPU T-cycles, each ticking every component once in domain-aware
    // lockstep through the component clock. An M-cycle has two distinct bus instants, not one: the DRIVE instant at the
    // machine cycle's start, when the address and — for a write — the data are on the pins, and the LATCH instant two
    // T-cycles later, when the CPU samples the data lines. A write reaches the peripheral on the drive instant; a read
    // takes the peripheral's value on the latch instant.
    private const int CpuTCyclesPerMachineCycle = 4;
    // The read latch's offset from its machine cycle's drive instant. The whole call/jp/ret/reti/push-pop/add_sp/
    // ld_hl_sp/oam_dma timing family and the timer conformance suite pin it: those cases distinguish which machine
    // cycle an access lands in, and this is what places the sample inside it.
    private const int LeadingTCyclesBeforeRead = 2;
    // The control register's display-enable bit, and the one control bit a monochrome control write presents on its
    // settling T-cycle (the background enable).
    private const byte LcdEnableBit = 0x80;
    private const byte MonochromeLcdControlSettlingMask = 0x01;
    // Cycles of the machine cycles already begun that have not been ticked yet — the distance from the clock's current
    // position to the NEXT access's drive instant. A read leaves two (its own latch already ticked two of its four); a
    // write leaves the four it did not tick, an internal cycle adds a whole four. Deferring them is what lets a write
    // commit BEFORE its own drive instant, which the I/O write conflicts need and which a tick-as-you-go accounting
    // cannot express. It is always zero at an instruction boundary, so no snapshot carries it.
    private int m_busCycleDebt;

    private byte ReadCycle(ushort address) {
        FlushBusCycles();
        AdvanceTCycles(count: LeadingTCyclesBeforeRead);

        var value = m_bus.ReadByte(address: address);

        m_busCycleDebt = (CpuTCyclesPerMachineCycle - LeadingTCyclesBeforeRead);

        return value;
    }
    // Where inside its machine cycle an I/O write actually reaches the component behind the register. Most writes
    // commit on the drive instant and the component reads the OLD value for that cycle. A handful of PPU registers sit
    // on the display's own path and settle earlier or later than the pins do; a monochrome palette register settles
    // over two T-cycles, the first of which drives the OR of the old and new values onto it. Whichever phase applies,
    // the NEXT access's drive instant stays four T-cycles after this one's.
    private enum WriteConflict {
        None,
        OneTCycleEarly,
        TwoTCyclesEarly,
        MonochromePalette,
        MonochromeStatus,
        MonochromeLcdControl,
    }

    private void WriteCycle(ushort address, byte value) {
        var conflict = ResolveWriteConflict(
            address: address,
            value: value
        );

        if (conflict == WriteConflict.None) {
            FlushBusCycles();
            m_bus.WriteByte(
                address: address,
                value: value
            );

            m_busCycleDebt = CpuTCyclesPerMachineCycle;

            return;
        }

        WriteCycleWithConflict(
            address: address,
            conflict: conflict,
            value: value
        );
    }
    private void WriteCycleWithConflict(ushort address, byte value, WriteConflict conflict) {
        var lead = (conflict switch {
            WriteConflict.OneTCycleEarly => -1,
            WriteConflict.TwoTCyclesEarly or WriteConflict.MonochromePalette or WriteConflict.MonochromeLcdControl => -2,
            _ => 0,
        });
        // A write that settles before its own drive instant needs that much of the previous access still unticked.
        // Every write reached through an instruction has it (a read leaves two, a write four, an internal cycle four
        // more); a write that does not falls back to the pins' own instant rather than travelling backwards.
        if ((m_busCycleDebt + lead) < 0) {
            lead = 0;
        }

        AdvanceTCycles(count: (m_busCycleDebt + lead));

        m_busCycleDebt = 0;

        // A register that settles presents an intermediate value for one T-cycle, then takes the written one. The
        // T-cycle is part of the phase, so it is spent even when the two values happen to coincide.
        if (conflict is WriteConflict.MonochromePalette or WriteConflict.MonochromeStatus or WriteConflict.MonochromeLcdControl) {
            m_bus.SettleIoWrite(
                address: address,
                value: (conflict switch {
                    // A monochrome palette register settles through the OR of the old and new values.
                    WriteConflict.MonochromePalette => ((byte)(value | m_bus.PeekIoRegister(address: address))),
                    // The monochrome status register reads as all ones for the settling T-cycle.
                    WriteConflict.MonochromeStatus => ((byte)0xFF),
                    // Only the background-enable bit of a monochrome control register reaches the display on the
                    // settling T-cycle; every other bit holds its old value across it.
                    _ => ((byte)(m_bus.PeekIoRegister(address: address) | (value & MonochromeLcdControlSettlingMask))),
                })
            );
            AdvanceTCycles(count: 1);

            ++lead;
        }

        m_bus.WriteByte(
            address: address,
            value: value
        );

        m_busCycleDebt = (CpuTCyclesPerMachineCycle - lead);
    }
    private WriteConflict ResolveWriteConflict(ushort address, byte value) {
        if (address is not (>= MemoryMap.LcdControl and <= MemoryMap.WindowX)) {
            return WriteConflict.None;
        }

        if (m_supportsColor) {
            // Color palette-index registers reach the display a machine cycle's worth of pins ahead of the write; from
            // revision D the display samples them one T-cycle earlier still.
            return (address switch {
                MemoryMap.BackgroundPalette or MemoryMap.ObjectPalette0 or MemoryMap.ObjectPalette1 => (m_samplesPaletteEarly
                    ? WriteConflict.TwoTCyclesEarly
                    : WriteConflict.OneTCycleEarly),
                _ => WriteConflict.None,
            });
        }

        return (address switch {
            // The enable edge is the one control write with no settling phase: the display's start-up chain, and every
            // dot of the first line's transient with it, is measured from the pins.
            MemoryMap.LcdControl => (((m_bus.PeekIoRegister(address: address) ^ value) & LcdEnableBit) == 0
                ? WriteConflict.MonochromeLcdControl
                : WriteConflict.None),
            MemoryMap.LcdStatus => WriteConflict.MonochromeStatus,
            MemoryMap.ScrollY => WriteConflict.OneTCycleEarly,
            MemoryMap.ScrollX => WriteConflict.TwoTCyclesEarly,
            MemoryMap.BackgroundPalette or MemoryMap.ObjectPalette0 or MemoryMap.ObjectPalette1 => WriteConflict.MonochromePalette,
            _ => WriteConflict.None,
        });
    }
    private void InternalCycle() =>
        m_busCycleDebt += CpuTCyclesPerMachineCycle;
    // A machine cycle the CPU spends without touching the bus at all — the locked-up fetchless spin, the speed-switch
    // stall, stop and halt, and a DMA stall. Nothing can commit inside it, so it settles the debt as it goes.
    private void IdleMachineCycle() {
        FlushBusCycles();
        AdvanceTCycles(count: CpuTCyclesPerMachineCycle);
    }
    // Ticks the deferred cycles through, bringing the clock to the next access's drive instant. Every read of a
    // component's state that is not itself a bus access has to settle the debt first, or it samples the past.
    private void FlushBusCycles() {
        var debt = m_busCycleDebt;

        m_busCycleDebt = 0;

        AdvanceTCycles(count: debt);
    }
    private void AdvanceTCycles(int count) {
        for (var remaining = count; (remaining > 0); --remaining) {
            m_componentClock.AdvanceCpuTCycle();
        }
    }
    private void ExecuteStop() {
        // STOP is encoded two bytes (assemblers emit 10 00) and consumes the pad byte ONLY when no interrupt is already
        // pending (SameBoy sm83_cpu.c stop(), ~line 397: `interrupt_pending = gb->interrupt_enable & gb->io_registers
        // [GB_IO_IF] & 0x1F;` — IE & IF, independent of IME — and ~line 405: `if (!interrupt_pending) cycle_read(gb,
        // gb->pc++);`). With a line already latched, PC stays past the opcode (this dispatch's PC+1) and the pad byte
        // executes as the very next instruction — mooneye's ei_sequence and the hardware-derived speed-switch path both
        // back the no-interrupt PC+2 consumption; only the pending-interrupt edge takes PC+1. This core reads IE/IF the
        // same way every other pending check does: m_interrupts.Pending.
        // On Color an armed speed switch begins here: KEY1 opens the hardware-measured stall and the CPU idles through
        // it one machine cycle per step (see StepInstruction), staying steppable at instruction granularity for the
        // whole re-gear. Without an armed switch (or on a monochrome machine) STOP parks the machine: stop mode on
        // Color, a plain halt-alike on monochrome.
        FlushBusCycles();

        if (m_interrupts.Pending == InterruptKind.None) {
            _ = ReadNextByte();
        }

        // The speed-switch stall and the halt latch are measured from the end of the opcode, not from the pad byte's
        // own latch, so the pad read's remaining cycles settle before either is armed.
        FlushBusCycles();

        if (
            m_supportsColor &&
            m_key1.IsSwitchArmed
        ) {
            m_key1.BeginSwitch();
        } else if (m_supportsColor) {
            m_key1.EnterStop();
        } else {
            m_halted = true;

            m_hdma.OnCpuHalted();
        }
    }
    private byte ReadNextByte() {
        var value = ReadCycle(address: m_programCounter);

        m_programCounter = ((ushort)(m_programCounter + 1));

        return value;
    }
    private ushort ReadNextWord() {
        var low = ReadNextByte();
        var high = ReadNextByte();

        return ((ushort)((high << 8) | low));
    }
    // The implicit SP move behind PUSH's two-byte write reports to the OAM corruption bug once, against SP's value
    // before either decrement (see PushWord) — a plain register-bump write-corruption trigger. The two byte writes
    // that follow are each a direct CPU write in their own right, which the bus arms for the SAME bug independently
    // (NoteBlockedOamWrite off SystemBus.WriteByte) if SP has landed in OAM range by then.
    private void PushStackByte(byte value) {
        m_stackPointer = ((ushort)(m_stackPointer - 1));
        WriteCycle(
            address: m_stackPointer,
            value: value
        );
    }
    // The implicit SP move's bus report happens BEFORE the internal delay cycle PUSH/CALL/RST spend ahead of their
    // first write, not after: on this hardware, that delay cycle IS the register-bump's own machine cycle (the IDU
    // drives the address bus at its start, the same way a bare INC/DEC's InternalCycle does), so the report has to
    // land before InternalCycle ticks the clock past it, or it would sample the row the scan reaches only after the
    // delay has already elapsed.
    private void PushWord(ushort value) {
        NoteOamCorruption(preValue: m_stackPointer);
        InternalCycle();
        PushStackByte(value: ((byte)(value >> 8)));
        PushStackByte(value: ((byte)value));
    }
    // POP's implicit SP++ carries no register-bump trigger of its own on this hardware — its share of the OAM
    // corruption bug comes entirely from each byte's own read (NoteBlockedOamRead off SystemBus.ReadByte) landing in
    // OAM range.
    private byte PopStackByte() {
        var value = ReadCycle(address: m_stackPointer);

        m_stackPointer = ((ushort)(m_stackPointer + 1));

        return value;
    }
    private ushort PopWord() {
        var low = PopStackByte();
        var high = PopStackByte();

        return ((ushort)((high << 8) | low));
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
            case 6:
                WriteCycle(
                    address: Hl,
                    value: value
                ); break;
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

        if (
            (opcode >= 0x40) &&
            (opcode <= 0x7F)
        ) {
            if (opcode == 0x76) {
                // A line already pending at HALT's own dispatch never enters halt at all. With IME clear that arms the
                // HALT bug (the next opcode fetch fails to advance PC, so that byte executes twice). With IME already
                // set — including an EI delay that lands on this very dispatch, since StepInstruction applies it before
                // reaching here — the CPU does not halt either: PC snaps back onto this opcode so the pending line
                // dispatches on the very next step with the return address pointing at this HALT, which re-executes
                // once the handler returns.
                FlushBusCycles();

                if (m_interrupts.Pending != InterruptKind.None) {
                    if (m_interruptMasterEnable) {
                        m_programCounter = ((ushort)(m_programCounter - 1));
                    } else {
                        m_haltBug = true;
                    }
                } else {
                    m_halted = true;

                    m_hdma.OnCpuHalted();
                }

                return;
            }

            WriteOperand(
                index: (opcode >> 3) & 7,
                value: ReadOperand(index: opcode & 7)
            );

            return;
        }

        if (
            (opcode >= 0x80) &&
            (opcode <= 0xBF)
        ) {
            AluA(
                operation: (opcode >> 3) & 7,
                value: ReadOperand(index: opcode & 7)
            );

            return;
        }

        if ((opcode & 0xC7) == 0x04) {
            var index = (opcode >> 3) & 7;

            WriteOperand(
                index: index,
                value: IncByte(value: ReadOperand(index: index))
            );

            return;
        }

        if ((opcode & 0xC7) == 0x05) {
            var index = (opcode >> 3) & 7;

            WriteOperand(
                index: index,
                value: DecByte(value: ReadOperand(index: index))
            );

            return;
        }

        if ((opcode & 0xC7) == 0x06) {
            WriteOperand(
                index: (opcode >> 3) & 7,
                value: ReadNextByte()
            );

            return;
        }

        if ((opcode & 0xC7) == 0xC6) {
            AluA(
                operation: (opcode >> 3) & 7,
                value: ReadNextByte()
            );

            return;
        }

        if ((opcode & 0xC7) == 0xC7) {
            Restart(vector: ((ushort)(opcode & 0x38)));

            return;
        }

        if (opcode < 0x20) {
            ExecuteLowGroup0(opcode: opcode);
        } else if (opcode < 0x40) {
            ExecuteLowGroup1(opcode: opcode);
        } else if (opcode < 0xE0) {
            ExecuteControlGroup(opcode: opcode);
        } else {
            ExecuteHighPageGroup(opcode: opcode);
        }
    }
    private void ExecuteLowGroup0(byte opcode) {
        switch (opcode) {
            case 0x00: break;                                                            // NOP
            case 0x10: ExecuteStop(); break;                                             // STOP
            case 0x01: Bc = ReadNextWord(); break;
            case 0x11: De = ReadNextWord(); break;
            case 0x02:
                WriteCycle(
                    address: Bc,
                    value: m_a
                ); break;
            case 0x12:
                WriteCycle(
                    address: De,
                    value: m_a
                ); break;
            case 0x0A: m_a = ReadCycle(address: Bc); break;
            case 0x1A: m_a = ReadCycle(address: De); break;
            case 0x03: NoteOamCorruption(preValue: Bc); Bc = ((ushort)(Bc + 1)); InternalCycle(); break;
            case 0x13: NoteOamCorruption(preValue: De); De = ((ushort)(De + 1)); InternalCycle(); break;
            case 0x0B: NoteOamCorruption(preValue: Bc); Bc = ((ushort)(Bc - 1)); InternalCycle(); break;
            case 0x1B: NoteOamCorruption(preValue: De); De = ((ushort)(De - 1)); InternalCycle(); break;
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
            case 0x22:
                WriteCycle(
                    address: Hl,
                    value: m_a
                ); Hl = ((ushort)(Hl + 1)); break;
            case 0x32:
                WriteCycle(
                    address: Hl,
                    value: m_a
                ); Hl = ((ushort)(Hl - 1)); break;
            case 0x2A: m_a = ReadCycle(address: Hl); Hl = ((ushort)(Hl + 1)); break;
            case 0x3A: m_a = ReadCycle(address: Hl); Hl = ((ushort)(Hl - 1)); break;
            case 0x23: NoteOamCorruption(preValue: Hl); Hl = ((ushort)(Hl + 1)); InternalCycle(); break;
            case 0x33: NoteOamCorruption(preValue: m_stackPointer); m_stackPointer = ((ushort)(m_stackPointer + 1)); InternalCycle(); break;
            case 0x2B: NoteOamCorruption(preValue: Hl); Hl = ((ushort)(Hl - 1)); InternalCycle(); break;
            case 0x3B: NoteOamCorruption(preValue: m_stackPointer); m_stackPointer = ((ushort)(m_stackPointer - 1)); InternalCycle(); break;
            case 0x27: DecimalAdjustAccumulator(); break;
            case 0x2F: ComplementAccumulator(); break;
            case 0x37: SetCarryFlag(); break;
            case 0x3F: ComplementCarryFlag(); break;
            case 0x29: AddHl(value: Hl); break;
            case 0x39: AddHl(value: m_stackPointer); break;
            default: JumpRelative(taken: ConditionMet(condition: (opcode >> 3) & 3)); break; // 0x20/28/30/38 JR cc
        }
    }
    private void ExecuteControlGroup(byte opcode) {
        switch (opcode) {
            case 0xC0: case 0xC8: case 0xD0: case 0xD8: ReturnConditional(taken: ConditionMet(condition: (opcode >> 3) & 3)); break;
            case 0xC9: m_programCounter = PopWord(); InternalCycle(); break;
            case 0xD9: m_programCounter = PopWord(); m_interruptMasterEnable = true; InternalCycle(); break;
            case 0xC1: Bc = PopWord(); break;
            case 0xD1: De = PopWord(); break;
            case 0xC5: PushWord(value: Bc); break;
            case 0xD5: PushWord(value: De); break;
            case 0xC2: case 0xCA: case 0xD2: case 0xDA: JumpAbsolute(taken: ConditionMet(condition: (opcode >> 3) & 3)); break;
            case 0xC3: JumpAbsolute(taken: true); break;
            case 0xC4: case 0xCC: case 0xD4: case 0xDC: CallAbsolute(taken: ConditionMet(condition: (opcode >> 3) & 3)); break;
            case 0xCD: CallAbsolute(taken: true); break;
            default: LockUp(); break;                                                     // 0xD3/0xDB/0xDD/0xDC-region illegal opcodes
        }
    }
    private void ExecuteHighPageGroup(byte opcode) {
        switch (opcode) {
            case 0xE0:
                WriteCycle(
                    address: ((ushort)(0xFF00 + ReadNextByte())),
                    value: m_a
                ); break;
            case 0xF0: m_a = ReadCycle(address: ((ushort)(0xFF00 + ReadNextByte()))); break;
            case 0xE1: Hl = PopWord(); break;
            case 0xF1: Af = PopWord(); break;
            case 0xE5: PushWord(value: Hl); break;
            case 0xF5: PushWord(value: Af); break;
            case 0xE2:
                WriteCycle(
                    address: ((ushort)(0xFF00 + m_c)),
                    value: m_a
                ); break;
            case 0xF2: m_a = ReadCycle(address: ((ushort)(0xFF00 + m_c))); break;
            case 0xE9: m_programCounter = Hl; break;
            case 0xEA:
                WriteCycle(
                    address: ReadNextWord(),
                    value: m_a
                ); break;
            case 0xFA: m_a = ReadCycle(address: ReadNextWord()); break;
            case 0xE8: m_stackPointer = AddStackPointerOffset(offset: ((sbyte)ReadNextByte())); InternalCycle(); InternalCycle(); break;
            case 0xF8: Hl = AddStackPointerOffset(offset: ((sbyte)ReadNextByte())); InternalCycle(); break;
            case 0xF9: NoteOamCorruption(preValue: Hl); m_stackPointer = Hl; InternalCycle(); break;
            // DI clears IME immediately (undelayed, even on Color) and cancels any in-flight EI enable, so EI;DI leaves
            // interrupts disabled. One hardware-derived reference implementation clears IME only and lets a pending
            // enable flip-then-be-overwritten the same step — same net result; clearing the countdown here is the
            // direct equivalent.
            case 0xF3: m_interruptMasterEnable = false; m_interruptEnableCountdown = 0; break;
            // EI arms the one-instruction enable delay only when IME isn't already set and no enable is in flight — the
            // same guard a hardware-derived reference implementation uses. A second EI landing in a first EI's delay
            // slot (or on an already-enabled IME) is a no-op, never a re-arm that pushes the enable out by another
            // instruction. The single-step test corpus's EI-sequence case pins this: 18 back-to-back EIs must still
            // service the interrupt on the first EI's schedule (asserts B=$01, C=$A2); an always-re-arm model fails it.
            // One savestate-conformance corpus re-arms even with IME already set (its internal ei-pending flag),
            // disagreeing with both the hardware-derived reference and the single-step test corpus — recorded as the
            // fb evidence-conflict skip in Sm83SstStage.
            case 0xFB:
                if (
                    !m_interruptMasterEnable &&
                    (m_interruptEnableCountdown == 0)
                ) {
                    m_interruptEnableCountdown = 1;
                }

                break;
            default: LockUp(); break;                                                     // 0xE3/0xE4/0xEB/0xEC/0xED/0xF4/0xFC/0xFD illegal opcodes
        }
    }
    // An undefined opcode wedges the SM83 permanently, exactly as the hardware does: it stops fetching and executing but
    // the machine's clock keeps running (see StepInstruction), so a demo that lands on a bad opcode hangs gracefully
    // rather than crashing the host. Only building a fresh machine clears the lock.
    private void LockUp() =>
        m_lockedUp = true;
    private void ExecuteBitOperation(byte opcode) {
        var operation = (opcode >> 3) & 7;
        var index = opcode & 7;

        switch (opcode >> 6) {
            case 0: // rotates and shifts
                WriteOperand(
                    index: index,
                    value: RotateOrShift(
                        operation: operation,
                        value: ReadOperand(index: index)
                    )
                );

                break;
            case 1: // BIT b, r
                TestBit(
                    bit: operation,
                    value: ReadOperand(index: index)
                );

                break;
            case 2: // RES b, r
                WriteOperand(
                    index: index,
                    value: ((byte)(ReadOperand(index: index) & ~(1 << operation)))
                );

                break;
            default: // SET b, r
                WriteOperand(
                    index: index,
                    value: ((byte)(ReadOperand(index: index) | (1 << operation)))
                );

                break;
        }
    }
    private void WriteStackPointerToMemory() {
        var address = ReadNextWord();

        WriteCycle(
            address: address,
            value: ((byte)m_stackPointer)
        );
        WriteCycle(
            address: ((ushort)(address + 1)),
            value: ((byte)(m_stackPointer >> 8))
        );
    }
    private void JumpRelative(bool taken) {
        var offset = ((sbyte)ReadNextByte());

        if (taken) {
            m_programCounter = ((ushort)(m_programCounter + offset));

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
        PushWord(value: m_programCounter);

        m_programCounter = vector;
    }
}
