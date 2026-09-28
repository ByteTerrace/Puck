namespace Puck.HumbleGamingDeck;

public sealed partial class HgdCpu<TBus> {
    private byte SetNz(byte value) {
        m_p = ((byte)((m_p & ~(Negative | Zero)) | (value & Negative) | ((value == 0) ? Zero : 0)));

        return value;
    }
    private void SetFlag(byte flag, bool set) {
        m_p = (set ? (byte)(m_p | flag) : (byte)(m_p & ~flag));
    }
    private void Add(byte value) {
        var sum = ((m_a + value) + (m_p & Carry));

        SetFlag(flag: Carry, set: (sum > 255));
        SetFlag(flag: Overflow, set: ((~(m_a ^ value) & (m_a ^ sum) & 128) != 0));
        m_a = SetNz(value: ((byte)sum));
    }
    private void Compare(byte register, byte value) {
        SetFlag(flag: Carry, set: (register >= value));
        _ = SetNz(value: ((byte)(register - value)));
    }
    private void ReadOperation(byte value) {
        switch (CurrentOperation) {
            case Operation.Ora:
                m_a = SetNz(value: ((byte)(m_a | value)));
                break;
            case Operation.And:
                m_a = SetNz(value: ((byte)(m_a & value)));
                break;
            case Operation.Eor:
                m_a = SetNz(value: ((byte)(m_a ^ value)));
                break;
            case Operation.Adc:
                Add(value: value);
                break;
            case Operation.Sbc:
                Add(value: ((byte)~value));
                break;
            case Operation.Cmp:
                Compare(register: m_a, value: value);
                break;
            case Operation.Cpx:
                Compare(register: m_x, value: value);
                break;
            case Operation.Cpy:
                Compare(register: m_y, value: value);
                break;
            case Operation.Lda:
                m_a = SetNz(value: value);
                break;
            case Operation.Ldx:
                m_x = SetNz(value: value);
                break;
            case Operation.Ldy:
                m_y = SetNz(value: value);
                break;
            case Operation.Lax:
                m_x = SetNz(value: value);
                m_a = m_x;
                break;
            case Operation.Las:
                m_s = SetNz(value: ((byte)(value & m_s)));
                m_x = m_s;
                m_a = m_x;
                break;
            case Operation.Bit:
                m_p = ((byte)((m_p & ~(Negative | Overflow | Zero)) | (value & 0xC0) | (((m_a & value) == 0) ? Zero : 0)));
                break;
            case Operation.Anc:
                m_a = SetNz(value: ((byte)(m_a & value)));
                SetFlag(flag: Carry, set: ((m_a & Negative) != 0));
                break;
            case Operation.Alr:
                m_a &= value;
                SetFlag(flag: Carry, set: ((m_a & 1) != 0));
                m_a = SetNz(value: ((byte)(m_a >> 1)));
                break;
            case Operation.Arr:
                m_a = SetNz(value: ((byte)(((m_a & value) >> 1) | ((m_p & Carry) << 7))));
                SetFlag(flag: Carry, set: ((m_a & 64) != 0));
                SetFlag(flag: Overflow, set: (((m_a ^ (m_a << 1)) & 64) != 0));
                break;
            case Operation.Axs:
                var difference = ((m_a & m_x) - value);

                SetFlag(flag: Carry, set: (difference >= 0));
                m_x = SetNz(value: ((byte)difference));
                break;
            case Operation.Xaa:
                m_a = SetNz(value: ((byte)((m_a | m_specialBus) & m_x & value)));
                break;
            case Operation.Lxa:
                m_x = SetNz(value: ((byte)((m_a | m_specialBus) & value)));
                m_a = m_x;
                break;
            case Operation.Nop:
                break;
            default:
                throw new InvalidOperationException(message: "Invalid CPU read operation.");
        }
    }
    private byte Modify(byte value) {
        var carry = m_p & Carry;

        switch (CurrentOperation) {
            case Operation.Asl:
            case Operation.Slo:
                SetFlag(flag: Carry, set: ((value & 128) != 0));
                value = SetNz(value: ((byte)(value << 1)));
                break;
            case Operation.Lsr:
            case Operation.Sre:
                SetFlag(flag: Carry, set: ((value & 1) != 0));
                value = SetNz(value: ((byte)(value >> 1)));
                break;
            case Operation.Rol:
            case Operation.Rla:
                SetFlag(flag: Carry, set: ((value & 128) != 0));
                value = SetNz(value: ((byte)((value << 1) | carry)));
                break;
            case Operation.Ror:
            case Operation.Rra:
                SetFlag(flag: Carry, set: ((value & 1) != 0));
                value = SetNz(value: ((byte)((value >> 1) | (carry << 7))));
                break;
            case Operation.Inc:
            case Operation.Isc:
                value = SetNz(value: ((byte)(value + 1)));
                break;
            case Operation.Dec:
            case Operation.Dcp:
                value = SetNz(value: ((byte)(value - 1)));
                break;
        }
        switch (CurrentOperation) {
            case Operation.Slo:
                m_a = SetNz(value: ((byte)(m_a | value)));
                break;
            case Operation.Sre:
                m_a = SetNz(value: ((byte)(m_a ^ value)));
                break;
            case Operation.Rla:
                m_a = SetNz(value: ((byte)(m_a & value)));
                break;
            case Operation.Rra:
                Add(value: value);
                break;
            case Operation.Dcp:
                Compare(register: m_a, value: value);
                break;
            case Operation.Isc:
                Add(value: ((byte)~value));
                break;
        }

        return value;
    }
    private void ImpliedOperation() {
        switch (CurrentOperation) {
            case Operation.Clc:
                m_p &= unchecked((byte)~Carry);
                break;
            case Operation.Sec:
                m_p |= Carry;
                break;
            case Operation.Cli:
                m_p &= unchecked((byte)~Interrupt);
                break;
            case Operation.Sei:
                m_p |= Interrupt;
                break;
            case Operation.Cld:
                m_p &= unchecked((byte)~Decimal);
                break;
            case Operation.Sed:
                m_p |= Decimal;
                break;
            case Operation.Clv:
                m_p &= unchecked((byte)~Overflow);
                break;
            case Operation.Tax:
                m_x = SetNz(value: m_a);
                break;
            case Operation.Tay:
                m_y = SetNz(value: m_a);
                break;
            case Operation.Txa:
                m_a = SetNz(value: m_x);
                break;
            case Operation.Tya:
                m_a = SetNz(value: m_y);
                break;
            case Operation.Tsx:
                m_x = SetNz(value: m_s);
                break;
            case Operation.Txs:
                m_s = m_x;
                break;
            case Operation.Inx:
                m_x = SetNz(value: ((byte)(m_x + 1)));
                break;
            case Operation.Iny:
                m_y = SetNz(value: ((byte)(m_y + 1)));
                break;
            case Operation.Dex:
                m_x = SetNz(value: ((byte)(m_x - 1)));
                break;
            case Operation.Dey:
                m_y = SetNz(value: ((byte)(m_y - 1)));
                break;
        }
    }
}
