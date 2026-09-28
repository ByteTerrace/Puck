namespace Puck.HumbleGamingDeck;

public sealed partial class HgdCpu<TBus> {
    private ushort StackAddress => ((ushort)(0x100 | m_s));
    private ushort ProvisionalAddress => ((ushort)((m_baseAddress & 0xFF00) | (m_address & 0xFF)));

    private Access NextAccess() {
        if (m_sequence == Sequence.Reset) {
            return new(Address: m_step switch {
                2 or 3 or 4 => StackAddress,
                5 => 0xFFFC,
                6 => 0xFFFD,
                _ => m_pc,
            });
        }
        if (IsJammed) {
            return new(Address: m_jamPhase switch {
                JamPhase.FirstLowRead or JamPhase.SecondLowRead => 0xFFFE,
                _ => 0xFFFF,
            });
        }
        if (m_step == 0) {
            return new(Address: m_pc);
        }
        if (CurrentMode == AddressMode.Special) {
            return SpecialAccess();
        }
        switch (CurrentMode) {
            case AddressMode.Immediate:
            case AddressMode.Implied:
            case AddressMode.Accumulator:
                return new(Address: m_pc);
            case AddressMode.Relative:
                return new(Address: ((m_step == 3) ? ProvisionalAddress : m_pc));
            case AddressMode.Indirect:
                return new(Address: m_step switch {
                    3 => m_pointer,
                    4 => ((ushort)((m_pointer & 0xFF00) | ((m_pointer + 1) & 0xFF))),
                    _ => m_pc,
                });
            case AddressMode.ZeroPage:
                return ((m_step == 1) ? new(Address: m_pc) : TargetAccess(firstStep: 2));
            case AddressMode.ZeroPageX:
            case AddressMode.ZeroPageY:
                return m_step switch {
                    1 => new(Address: m_pc),
                    2 => new(Address: m_baseAddress),
                    _ => TargetAccess(firstStep: 3),
                };
            case AddressMode.Absolute:
                return ((m_step <= 2) ? new(Address: m_pc) : TargetAccess(firstStep: 3));
            case AddressMode.AbsoluteX:
            case AddressMode.AbsoluteY:
                return m_step switch {
                    1 or 2 => new(Address: m_pc),
                    3 => new(Address: ProvisionalAddress),
                    _ => TargetAccess(firstStep: 4),
                };
            case AddressMode.IndirectX:
                return m_step switch {
                    1 => new(Address: m_pc),
                    2 => new(Address: m_pointer),
                    3 => new(Address: m_pointer),
                    4 => new(Address: ((byte)(m_pointer + 1))),
                    _ => TargetAccess(firstStep: 5),
                };
            case AddressMode.IndirectY:
                return m_step switch {
                    1 => new(Address: m_pc),
                    2 => new(Address: m_pointer),
                    3 => new(Address: ((byte)(m_pointer + 1))),
                    4 => new(Address: ProvisionalAddress),
                    _ => TargetAccess(firstStep: 5),
                };
            default:
                throw new InvalidOperationException(message: "Invalid CPU address mode.");
        }
    }
    private Access TargetAccess(int firstStep) {
        if (IsStore) {
            return new(Address: StoreAddress(), Value: StoreValue(), Write: true);
        }

        return new(Address: m_address, Value: m_operand, Write: (IsModify && (m_step > firstStep)));
    }
    private Access SpecialAccess() {
        switch (CurrentOperation) {
            case Operation.Brk:
                return m_step switch {
                    2 => new(Address: StackAddress, Value: ((byte)(m_pc >> 8)), Write: true),
                    3 => new(Address: StackAddress, Value: ((byte)m_pc), Write: true),
                    4 => new(Address: StackAddress, Value: ((byte)(m_p | ((m_sequence == Sequence.Instruction) ? 0x30 : 0x20))), Write: true),
                    5 => new(Address: m_vector),
                    6 => new(Address: ((ushort)(m_vector + 1))),
                    _ => new(Address: m_pc),
                };
            case Operation.Jsr:
                return m_step switch {
                    2 => new(Address: StackAddress),
                    3 => new(Address: StackAddress, Value: ((byte)(m_pc >> 8)), Write: true),
                    4 => new(Address: StackAddress, Value: ((byte)m_pc), Write: true),
                    _ => new(Address: m_pc),
                };
            case Operation.Pha:
            case Operation.Php:
                return ((m_step == 1) ? new(Address: m_pc) : new(Address: StackAddress,
                    Value: ((CurrentOperation == Operation.Pha) ? m_a : (byte)(m_p | 0x30)), Write: true));
            case Operation.Pla:
            case Operation.Plp:
            case Operation.Rti:
                return new(Address: ((m_step == 1) ? m_pc : StackAddress));
            case Operation.Rts:
                return new(Address: ((m_step is 1 or 5) ? m_pc : StackAddress));
            case Operation.Jam:
                return new(Address: m_pc);
            default:
                throw new InvalidOperationException(message: "Invalid special CPU sequence.");
        }
    }
    private bool IsUnstableStoreDummy() {
        return ((CurrentOperation is Operation.Ahx or Operation.Shx or Operation.Shy or Operation.Tas) &&
            (m_step == ((CurrentMode == AddressMode.IndirectY) ? 4 : 3)));
    }
    private byte StoreSource() {
        return CurrentOperation switch {
            Operation.Sta => m_a,
            Operation.Stx or Operation.Shx => m_x,
            Operation.Sty or Operation.Shy => m_y,
            _ => ((byte)(m_a & m_x)),
        };
    }
    private byte StoreValue() {
        var source = StoreSource();

        return (((CurrentOperation is Operation.Ahx or Operation.Shx or Operation.Shy or Operation.Tas) && !m_storeHalted)
            ? (byte)(source & ((m_baseAddress >> 8) + 1)) : source);
    }
    private ushort StoreAddress() {
        // The indexed carry drives the unstable store's high address from the same masked source as its data.
        // RDY can suppress the data mask while leaving this address corruption intact (NESdev unofficial opcodes).
        if (m_crossed && (CurrentOperation is Operation.Ahx or Operation.Shx or Operation.Shy or Operation.Tas)) {
            return ((ushort)((m_address & 255) | ((StoreSource() & ((m_baseAddress >> 8) + 1)) << 8)));
        }

        return m_address;
    }
}
