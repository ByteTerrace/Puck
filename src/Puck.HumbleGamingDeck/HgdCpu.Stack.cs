namespace Puck.HumbleGamingDeck;

public sealed partial class HgdCpu<TBus> {
    private void AdvanceSpecial(byte value) {
        switch (CurrentOperation) {
            case Operation.Jam:
                m_jamPhase = JamPhase.FirstHighRead;

                return;
            case Operation.Brk:
                AdvanceInterrupt(value: value);

                return;
            case Operation.Jsr:
                if (m_step == 1) {
                    m_operand = value;
                    ++m_pc;
                } else if (m_step is 3 or 4) {
                    --m_s;
                } else if (m_step == 5) {
                    m_pc = ((ushort)(m_operand | (value << 8)));
                    Finish();

                    return;
                }
                break;
            case Operation.Pha:
            case Operation.Php:
                if (m_step == 2) {
                    --m_s;
                    Finish();

                    return;
                }
                break;
            case Operation.Pla:
            case Operation.Plp:
                if (m_step == 2) {
                    ++m_s;
                } else if (m_step == 3) {
                    Finish();
                    if (CurrentOperation == Operation.Pla) {
                        m_a = SetNz(value: value);
                    } else {
                        P = value;
                    }

                    return;
                }
                break;
            case Operation.Rts:
            case Operation.Rti:
                AdvanceReturn(value: value);

                return;
        }
        ++m_step;
    }
    private void AdvanceInterrupt(byte value) {
        if ((m_step == 1) && (m_sequence == Sequence.Instruction)) {
            ++m_pc;
        } else if (m_step is 2 or 3) {
            --m_s;
        } else if (m_step == 4) {
            --m_s;
            // NMI may hijack BRK/IRQ before vector selection without changing the pushed PC or B bit.
            // https://www.nesdev.org/wiki/CPU_interrupts#Interrupt_hijacking
            var nmi = ((m_sequence == Sequence.Nmi) || m_nmiSample);

            m_vector = (nmi ? (ushort)0xFFFA : (ushort)0xFFFE);
            if (nmi) {
                m_nmiPending = false;
                m_nmiSample = false;
            }
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
    private void AdvanceReturn(byte value) {
        var rti = (CurrentOperation == Operation.Rti);

        if (m_step == 2) {
            ++m_s;
        } else if (rti && (m_step == 3)) {
            P = value;
            ++m_s;
        } else if (m_step == (rti ? 4 : 3)) {
            m_operand = value;
            ++m_s;
        } else if (m_step == (rti ? 5 : 4)) {
            m_pc = ((ushort)(m_operand | (value << 8)));
            if (rti) {
                Finish();

                return;
            }
        } else if (!rti && (m_step == 5)) {
            ++m_pc;
            Finish();

            return;
        }
        ++m_step;
    }
}
