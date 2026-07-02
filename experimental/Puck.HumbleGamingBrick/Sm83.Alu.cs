namespace Puck.HumbleGamingBrick;

/// <summary>
/// The SM83's arithmetic and logic, with the exact flag behaviour the Game Boy programs depend on. Every operation
/// here is integer-only and side-effect-free apart from the accumulator and the flags register, so the results are
/// deterministic.
/// </summary>
public sealed partial class Sm83 {
    private bool CarryFlagSet =>
        ((m_f & FlagCarry) != 0);
    private bool HalfCarryFlagSet =>
        ((m_f & FlagHalfCarry) != 0);
    private bool SubtractFlagSet =>
        ((m_f & FlagSubtract) != 0);
    private bool ZeroFlagSet =>
        ((m_f & FlagZero) != 0);

    private void SetFlags(bool zero, bool subtract, bool halfCarry, bool carry) {
        byte flags = 0;

        if (zero) { flags |= FlagZero; }
        if (subtract) { flags |= FlagSubtract; }
        if (halfCarry) { flags |= FlagHalfCarry; }
        if (carry) { flags |= FlagCarry; }

        m_f = flags;
    }
    private void AluA(int operation, byte value) {
        switch (operation) {
            case 0: AluAdd(value: value); break;
            case 1: AluAdc(value: value); break;
            case 2: AluSub(value: value); break;
            case 3: AluSbc(value: value); break;
            case 4: AluAnd(value: value); break;
            case 5: AluXor(value: value); break;
            case 6: AluOr(value: value); break;
            default: AluCp(value: value); break;
        }
    }
    private void AluAdd(byte value) {
        var result = (m_a + value);

        SetFlags(zero: ((result & 0xFF) == 0), subtract: false, halfCarry: (((m_a & 0xF) + (value & 0xF)) > 0xF), carry: (result > 0xFF));

        m_a = (byte)result;
    }
    private void AluAdc(byte value) {
        var carry = (CarryFlagSet ? 1 : 0);
        var result = (m_a + value + carry);

        SetFlags(zero: ((result & 0xFF) == 0), subtract: false, halfCarry: (((m_a & 0xF) + (value & 0xF) + carry) > 0xF), carry: (result > 0xFF));

        m_a = (byte)result;
    }
    private void AluSub(byte value) {
        var result = (m_a - value);

        SetFlags(zero: ((result & 0xFF) == 0), subtract: true, halfCarry: (((m_a & 0xF) - (value & 0xF)) < 0), carry: (result < 0));

        m_a = (byte)result;
    }
    private void AluSbc(byte value) {
        var carry = (CarryFlagSet ? 1 : 0);
        var result = (m_a - value - carry);

        SetFlags(zero: ((result & 0xFF) == 0), subtract: true, halfCarry: (((m_a & 0xF) - (value & 0xF) - carry) < 0), carry: (result < 0));

        m_a = (byte)result;
    }
    private void AluAnd(byte value) {
        m_a &= value;

        SetFlags(zero: (m_a == 0), subtract: false, halfCarry: true, carry: false);
    }
    private void AluXor(byte value) {
        m_a ^= value;

        SetFlags(zero: (m_a == 0), subtract: false, halfCarry: false, carry: false);
    }
    private void AluOr(byte value) {
        m_a |= value;

        SetFlags(zero: (m_a == 0), subtract: false, halfCarry: false, carry: false);
    }
    private void AluCp(byte value) {
        var result = (m_a - value);

        SetFlags(zero: ((result & 0xFF) == 0), subtract: true, halfCarry: (((m_a & 0xF) - (value & 0xF)) < 0), carry: (result < 0));
    }
    private byte IncByte(byte value) {
        var result = (byte)(value + 1);

        SetFlags(zero: (result == 0), subtract: false, halfCarry: ((value & 0xF) == 0xF), carry: CarryFlagSet);

        return result;
    }
    private byte DecByte(byte value) {
        var result = (byte)(value - 1);

        SetFlags(zero: (result == 0), subtract: true, halfCarry: ((value & 0xF) == 0), carry: CarryFlagSet);

        return result;
    }
    private void AddHl(ushort value) {
        var hl = (int)Hl;
        var result = (hl + value);

        SetFlags(zero: ZeroFlagSet, subtract: false, halfCarry: (((hl & 0xFFF) + (value & 0xFFF)) > 0xFFF), carry: (result > 0xFFFF));

        Hl = (ushort)result;

        InternalCycle();
    }
    private ushort AddStackPointerOffset(sbyte offset) {
        var stackPointer = (int)m_stackPointer;
        var result = (stackPointer + offset);

        SetFlags(
            zero: false,
            subtract: false,
            halfCarry: (((stackPointer & 0xF) + (offset & 0xF)) > 0xF),
            carry: (((stackPointer & 0xFF) + (offset & 0xFF)) > 0xFF)
        );

        return (ushort)result;
    }
    private void DecimalAdjustAccumulator() {
        var subtract = SubtractFlagSet;
        var halfCarry = HalfCarryFlagSet;
        var carry = CarryFlagSet;
        var value = (int)m_a;

        if (!subtract) {
            if (carry || (value > 0x99)) {
                value += 0x60;
                carry = true;
            }

            if (halfCarry || ((value & 0x0F) > 0x09)) {
                value += 0x06;
            }
        }
        else {
            if (carry) {
                value -= 0x60;
            }

            if (halfCarry) {
                value -= 0x06;
            }
        }

        m_a = (byte)value;

        SetFlags(zero: (m_a == 0), subtract: subtract, halfCarry: false, carry: carry);
    }
    private void ComplementAccumulator() {
        m_a = (byte)~m_a;
        m_f = (byte)((m_f & (FlagZero | FlagCarry)) | FlagSubtract | FlagHalfCarry);
    }
    private void SetCarryFlag() =>
        m_f = (byte)((m_f & FlagZero) | FlagCarry);
    private void ComplementCarryFlag() =>
        m_f = (byte)((m_f & FlagZero) | (CarryFlagSet ? 0 : FlagCarry));
    private void RotateAccumulatorLeftCircular() {
        var carry = ((m_a >> 7) & 1);

        m_a = (byte)((m_a << 1) | carry);

        SetFlags(zero: false, subtract: false, halfCarry: false, carry: (carry != 0));
    }
    private void RotateAccumulatorRightCircular() {
        var carry = (m_a & 1);

        m_a = (byte)((m_a >> 1) | (carry << 7));

        SetFlags(zero: false, subtract: false, halfCarry: false, carry: (carry != 0));
    }
    private void RotateAccumulatorLeft() {
        var carryIn = (CarryFlagSet ? 1 : 0);
        var carryOut = ((m_a >> 7) & 1);

        m_a = (byte)((m_a << 1) | carryIn);

        SetFlags(zero: false, subtract: false, halfCarry: false, carry: (carryOut != 0));
    }
    private void RotateAccumulatorRight() {
        var carryIn = (CarryFlagSet ? 1 : 0);
        var carryOut = (m_a & 1);

        m_a = (byte)((m_a >> 1) | (carryIn << 7));

        SetFlags(zero: false, subtract: false, halfCarry: false, carry: (carryOut != 0));
    }
    private byte RotateOrShift(int operation, byte value) {
        int result;
        int carryOut;

        switch (operation) {
            case 0: // RLC
                carryOut = ((value >> 7) & 1);
                result = ((value << 1) | carryOut);

                break;
            case 1: // RRC
                carryOut = (value & 1);
                result = ((value >> 1) | (carryOut << 7));

                break;
            case 2: // RL
                carryOut = ((value >> 7) & 1);
                result = ((value << 1) | (CarryFlagSet ? 1 : 0));

                break;
            case 3: // RR
                carryOut = (value & 1);
                result = ((value >> 1) | (CarryFlagSet ? 0x80 : 0));

                break;
            case 4: // SLA
                carryOut = ((value >> 7) & 1);
                result = (value << 1);

                break;
            case 5: // SRA
                carryOut = (value & 1);
                result = ((value >> 1) | (value & 0x80));

                break;
            case 6: // SWAP
                carryOut = 0;
                result = ((value >> 4) | (value << 4));

                break;
            default: // SRL
                carryOut = (value & 1);
                result = (value >> 1);

                break;
        }

        var truncated = (byte)result;

        SetFlags(zero: (truncated == 0), subtract: false, halfCarry: false, carry: (carryOut != 0));

        return truncated;
    }
    private void TestBit(int bit, byte value) =>
        m_f = (byte)((m_f & FlagCarry) | FlagHalfCarry | ((((value >> bit) & 1) == 0) ? FlagZero : 0));
}
