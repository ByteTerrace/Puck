namespace Puck.GameBoy;

// The arithmetic/logic helpers, factored out of the opcode decode. Each sets the flag fields exactly as the
// SM83 does; operations that leave a flag untouched (the carry on INC/DEC, the zero on the A-register rotates)
// are documented at their call sites.
public sealed partial class Sm83 {
    private void AddToAccumulator(byte value, bool withCarry) {
        var carry = ((withCarry && m_flagCarry) ? 1 : 0);
        var result = (m_a + value + carry);

        m_flagHalfCarry = (((m_a & 0x0F) + (value & 0x0F) + carry) > 0x0F);
        m_flagCarry = (result > 0xFF);
        m_a = (byte)result;
        m_flagZero = (m_a == 0);
        m_flagSubtract = false;
    }
    private void SubtractFromAccumulator(byte value, bool withCarry) {
        var carry = ((withCarry && m_flagCarry) ? 1 : 0);
        var result = (m_a - value - carry);

        m_flagHalfCarry = (((m_a & 0x0F) - (value & 0x0F) - carry) < 0);
        m_flagCarry = (result < 0);
        m_a = (byte)result;
        m_flagZero = (m_a == 0);
        m_flagSubtract = true;
    }
    private void CompareWithAccumulator(byte value) {
        var result = (m_a - value);

        m_flagHalfCarry = (((m_a & 0x0F) - (value & 0x0F)) < 0);
        m_flagCarry = (result < 0);
        m_flagZero = ((byte)result == 0);
        m_flagSubtract = true;
    }
    private void AndWithAccumulator(byte value) {
        m_a &= value;
        m_flagZero = (m_a == 0);
        m_flagSubtract = false;
        m_flagHalfCarry = true;
        m_flagCarry = false;
    }
    private void OrWithAccumulator(byte value) {
        m_a |= value;
        m_flagZero = (m_a == 0);
        m_flagSubtract = false;
        m_flagHalfCarry = false;
        m_flagCarry = false;
    }
    private void XorWithAccumulator(byte value) {
        m_a ^= value;
        m_flagZero = (m_a == 0);
        m_flagSubtract = false;
        m_flagHalfCarry = false;
        m_flagCarry = false;
    }

    private byte Increment8(byte value) {
        var result = (byte)(value + 1);

        m_flagHalfCarry = ((value & 0x0F) == 0x0F);
        m_flagZero = (result == 0);
        m_flagSubtract = false;

        return result;
    }
    private byte Decrement8(byte value) {
        var result = (byte)(value - 1);

        m_flagHalfCarry = ((value & 0x0F) == 0x00);
        m_flagZero = (result == 0);
        m_flagSubtract = true;

        return result;
    }

    private void AddToHl(ushort value) {
        var hl = HL;
        var result = (hl + value);

        m_flagHalfCarry = (((hl & 0x0FFF) + (value & 0x0FFF)) > 0x0FFF);
        m_flagCarry = (result > 0xFFFF);
        m_flagSubtract = false;
        HL = (ushort)result;
    }
    private ushort AddSignedToStackPointer(sbyte offset) {
        var sp = m_stackPointer;

        // The carries are computed from the unsigned low byte, as the hardware does for both ADD SP,e and LD HL,SP+e.
        m_flagHalfCarry = (((sp & 0x0F) + (offset & 0x0F)) > 0x0F);
        m_flagCarry = (((sp & 0xFF) + (offset & 0xFF)) > 0xFF);
        m_flagZero = false;
        m_flagSubtract = false;

        return (ushort)(sp + offset);
    }

    private void DecimalAdjustAccumulator() {
        var adjust = 0;
        var carry = m_flagCarry;

        if (!m_flagSubtract) {
            if (m_flagHalfCarry || ((m_a & 0x0F) > 0x09)) {
                adjust |= 0x06;
            }

            if (m_flagCarry || (m_a > 0x99)) {
                adjust |= 0x60;
                carry = true;
            }

            m_a = (byte)(m_a + adjust);
        }
        else {
            if (m_flagHalfCarry) {
                adjust |= 0x06;
            }

            if (m_flagCarry) {
                adjust |= 0x60;
            }

            m_a = (byte)(m_a - adjust);
        }

        m_flagZero = (m_a == 0);
        m_flagHalfCarry = false;
        m_flagCarry = carry;
    }

    // The eight CB rotate/shift kernels. Each returns the result and sets C from the bit shifted out; the
    // caller sets Z/N/H (Z from the result for CB-prefixed forms, Z=0 for the A-register forms 0x07-0x1F).
    private byte RotateLeftCircular(byte value) {
        var carry = ((value >> 7) & 1);

        m_flagCarry = (carry != 0);

        return (byte)((value << 1) | carry);
    }
    private byte RotateRightCircular(byte value) {
        var carry = (value & 1);

        m_flagCarry = (carry != 0);

        return (byte)((value >> 1) | (carry << 7));
    }
    private byte RotateLeftThroughCarry(byte value) {
        var carryIn = (m_flagCarry ? 1 : 0);

        m_flagCarry = (((value >> 7) & 1) != 0);

        return (byte)((value << 1) | carryIn);
    }
    private byte RotateRightThroughCarry(byte value) {
        var carryIn = (m_flagCarry ? 0x80 : 0x00);

        m_flagCarry = ((value & 1) != 0);

        return (byte)((value >> 1) | carryIn);
    }
    private byte ShiftLeftArithmetic(byte value) {
        m_flagCarry = (((value >> 7) & 1) != 0);

        return (byte)(value << 1);
    }
    private byte ShiftRightArithmetic(byte value) {
        m_flagCarry = ((value & 1) != 0);

        return (byte)((value >> 1) | (value & 0x80));
    }
    private byte ShiftRightLogical(byte value) {
        m_flagCarry = ((value & 1) != 0);

        return (byte)(value >> 1);
    }
    private byte Swap(byte value) {
        m_flagCarry = false;

        return (byte)((value >> 4) | (value << 4));
    }
}
