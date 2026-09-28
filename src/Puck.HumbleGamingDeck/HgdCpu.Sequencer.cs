namespace Puck.HumbleGamingDeck;

public sealed partial class HgdCpu<TBus> {
    private void Advance(byte value) {
        if (m_sequence == Sequence.Reset) {
            AdvanceReset(value: value);

            return;
        }
        if (IsJammed) {
            // Visual 6502's JAM trace reads $FFFF, $FFFE, $FFFE, then holds $FFFF until reset.
            m_jamPhase = m_jamPhase switch {
                JamPhase.FirstHighRead => JamPhase.FirstLowRead,
                JamPhase.FirstLowRead => JamPhase.SecondLowRead,
                _ => JamPhase.RepeatedHighRead,
            };

            return;
        }
        if (m_step == 0) {
            m_storeHalted = false;
            m_specialBus = m_precharge;
            if (m_polledIrq || m_polledNmi) {
                m_opcode = 0;
                m_sequence = (m_polledNmi ? Sequence.Nmi : Sequence.Irq);
                m_polledIrq = false;
                m_polledNmi = false;
            } else {
                m_opcode = value;
                ++m_pc;
            }
            m_step = 1;

            return;
        }
        if (CurrentMode == AddressMode.Special) {
            AdvanceSpecial(value: value);

            return;
        }
        switch (CurrentMode) {
            case AddressMode.Immediate:
                ++m_pc;
                Finish();
                ReadOperation(value: value);

                return;
            case AddressMode.Implied:
                Finish();
                ImpliedOperation();

                return;
            case AddressMode.Accumulator:
                Finish();
                m_a = Modify(value: m_a);

                return;
            case AddressMode.Relative:
                AdvanceBranch(value: value);

                return;
            case AddressMode.Indirect:
                AdvanceIndirectJump(value: value);

                return;
            case AddressMode.ZeroPage:
            case AddressMode.ZeroPageX:
            case AddressMode.ZeroPageY:
                AdvanceZeroPage(value: value);

                return;
            case AddressMode.Absolute:
            case AddressMode.AbsoluteX:
            case AddressMode.AbsoluteY:
                AdvanceAbsolute(value: value);

                return;
            case AddressMode.IndirectX:
            case AddressMode.IndirectY:
                AdvanceIndirect(value: value);

                return;
        }
    }
    private void AdvanceReset(byte value) {
        if (m_step is 2 or 3 or 4) {
            --m_s;
        } else if (m_step == 5) {
            m_operand = value;
            m_p |= Interrupt;
        } else if (m_step == 6) {
            m_pc = ((ushort)(m_operand | (value << 8)));
            Finish(poll: false);

            return;
        }
        ++m_step;
    }
    private void AdvanceZeroPage(byte value) {
        var indexed = (CurrentMode != AddressMode.ZeroPage);

        if (m_step == 1) {
            ++m_pc;
            m_address = value;
            m_baseAddress = value;
        } else if (indexed && (m_step == 2)) {
            m_address = ((byte)(m_baseAddress + ((CurrentMode == AddressMode.ZeroPageX) ? m_x : m_y)));
        } else {
            AdvanceTarget(firstStep: (indexed ? 3 : 2), value: value);

            return;
        }
        ++m_step;
    }
    private void AdvanceAbsolute(byte value) {
        if (m_step == 1) {
            m_operand = value;
            ++m_pc;
        } else if (m_step == 2) {
            ++m_pc;
            m_baseAddress = ((ushort)(m_operand | (value << 8)));
            if (CurrentOperation == Operation.Jmp) {
                m_pc = m_baseAddress;
                Finish();

                return;
            }
            IndexAddress(index: CurrentMode switch {
                AddressMode.AbsoluteX => m_x,
                AddressMode.AbsoluteY => m_y,
                _ => 0,
            });
        } else if ((CurrentMode != AddressMode.Absolute) && (m_step == 3)) {
            if (!m_crossed && !IsStore && !IsModify) {
                Finish();
                ReadOperation(value: value);

                return;
            }
        } else {
            AdvanceTarget(value: value, firstStep: ((CurrentMode == AddressMode.Absolute) ? 3 : 4));

            return;
        }
        ++m_step;
    }
    private void AdvanceIndirect(byte value) {
        var indexedX = (CurrentMode == AddressMode.IndirectX);

        if (m_step == 1) {
            m_pointer = value;
            ++m_pc;
        } else if (indexedX && (m_step == 2)) {
            m_pointer = ((byte)(m_pointer + m_x));
        } else if (m_step == (indexedX ? 3 : 2)) {
            m_operand = value;
        } else if (m_step == (indexedX ? 4 : 3)) {
            m_baseAddress = ((ushort)(m_operand | (value << 8)));
            IndexAddress(index: (indexedX ? 0 : m_y));
        } else if (!indexedX && (m_step == 4)) {
            if (!m_crossed && !IsStore && !IsModify) {
                Finish();
                ReadOperation(value: value);

                return;
            }
        } else {
            AdvanceTarget(firstStep: 5, value: value);

            return;
        }
        ++m_step;
    }
    private void IndexAddress(int index) {
        m_address = ((ushort)(m_baseAddress + index));
        m_crossed = ((m_address & 0xFF00) != (m_baseAddress & 0xFF00));
    }
    private void AdvanceTarget(byte value, int firstStep) {
        if (IsStore) {
            if (CurrentOperation == Operation.Tas) {
                m_s = ((byte)(m_a & m_x));
            }
            Finish();
        } else if (!IsModify) {
            Finish();
            ReadOperation(value: value);
        } else if (m_step == firstStep) {
            m_operand = value;
            ++m_step;
        } else if (m_step == (firstStep + 1)) {
            m_operand = Modify(value: m_operand);
            ++m_step;
        } else {
            Finish();
        }
    }
    private void AdvanceIndirectJump(byte value) {
        switch (m_step) {
            case 1:
                m_operand = value;
                ++m_pc;
                break;
            case 2:
                m_pointer = ((ushort)(m_operand | (value << 8)));
                ++m_pc;
                break;
            case 3:
                m_operand = value;
                break;
            case 4:
                m_pc = ((ushort)(m_operand | (value << 8)));
                Finish();

                return;
        }
        ++m_step;
    }
    private void AdvanceBranch(byte value) {
        if (m_step == 1) {
            // Branches poll at T1, and again at the page-fixup cycle only; a same-page taken branch delays a new IRQ.
            // https://www.nesdev.org/wiki/CPU_interrupts
            PollInterrupts();
            ++m_pc;
            if (!BranchTaken()) {
                Finish(poll: false);

                return;
            }
            m_baseAddress = m_pc;
            IndexAddress(index: ((sbyte)value));
        } else if ((m_step == 2) && !m_crossed) {
            m_pc = m_address;
            Finish(poll: false);

            return;
        } else if (m_step == 3) {
            m_pc = m_address;
            Finish();

            return;
        }
        ++m_step;
    }
    private bool BranchTaken() {
        return CurrentOperation switch {
            Operation.Bpl => ((m_p & Negative) == 0),
            Operation.Bmi => ((m_p & Negative) != 0),
            Operation.Bvc => ((m_p & Overflow) == 0),
            Operation.Bvs => ((m_p & Overflow) != 0),
            Operation.Bcc => ((m_p & Carry) == 0),
            Operation.Bcs => ((m_p & Carry) != 0),
            Operation.Bne => ((m_p & Zero) == 0),
            Operation.Beq => ((m_p & Zero) != 0),
            _ => false,
        };
    }
}
