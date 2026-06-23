namespace Puck.GameBoyAdvance;

// The arithmetic/logic helpers shared by the ARM and Thumb execution units: condition-code evaluation, the
// barrel shifter, and the flag-setting add/subtract primitives. Keeping flag derivation in one place is what
// makes the carry/overflow behaviour — the part conformance suites probe hardest — consistent across both
// instruction sets.
public sealed partial class Arm7Tdmi {
    // Evaluates a 4-bit ARM condition field against the current flags.
    private bool CheckCondition(uint condition) {
        var n = (m_cpsr & FlagN) != 0u;
        var z = (m_cpsr & FlagZ) != 0u;
        var c = (m_cpsr & FlagC) != 0u;
        var v = (m_cpsr & FlagV) != 0u;

        return condition switch {
            0x0u => z,                  // EQ
            0x1u => !z,                 // NE
            0x2u => c,                  // CS/HS
            0x3u => !c,                 // CC/LO
            0x4u => n,                  // MI
            0x5u => !n,                 // PL
            0x6u => v,                  // VS
            0x7u => !v,                 // VC
            0x8u => c && !z,            // HI
            0x9u => !c || z,            // LS
            0xAu => n == v,             // GE
            0xBu => n != v,             // LT
            0xCu => !z && (n == v),     // GT
            0xDu => z || (n != v),      // LE
            0xEu => true,               // AL
            _ => false,                 // 0xF: reserved on ARMv4T — never executes.
        };
    }

    // The barrel shifter. Returns the shifted value and updates carryOut (the shifter carry, which feeds the C
    // flag of logical data-processing operations). byRegister selects the register-specified-amount semantics,
    // whose handling of zero and of amounts >= 32 differs from the immediate forms.
    private uint BarrelShift(uint value, ShiftType type, int amount, bool byRegister, ref bool carryOut) {
        if (byRegister && (amount == 0)) {
            // Register shift by zero: operand and carry are unaffected.
            return value;
        }

        switch (type) {
            case ShiftType.LogicalLeft:
                if (amount == 0) {
                    return value; // LSL #0 (immediate): unchanged, carry preserved.
                }

                if (amount < 32) {
                    carryOut = ((value >> (32 - amount)) & 1u) != 0u;

                    return value << amount;
                }

                carryOut = (amount == 32) && ((value & 1u) != 0u);

                return 0u;

            case ShiftType.LogicalRight:
                if ((amount == 0) || (amount == 32)) {
                    // LSR #0 encodes LSR #32.
                    carryOut = (value & 0x80000000u) != 0u;

                    return 0u;
                }

                if (amount < 32) {
                    carryOut = ((value >> (amount - 1)) & 1u) != 0u;

                    return value >> amount;
                }

                carryOut = false;

                return 0u;

            case ShiftType.ArithmeticRight:
                if ((amount == 0) || (amount >= 32)) {
                    // ASR #0 encodes ASR #32; any amount >= 32 saturates to the sign bit.
                    carryOut = (value & 0x80000000u) != 0u;

                    return (uint)((int)value >> 31);
                }

                carryOut = ((value >> (amount - 1)) & 1u) != 0u;

                return (uint)((int)value >> amount);

            default: // ShiftType.RotateRight
                if (amount == 0) {
                    // ROR #0 (immediate) encodes RRX: a 33-bit rotate through carry.
                    var oldCarry = (m_cpsr & FlagC) != 0u;

                    carryOut = (value & 1u) != 0u;

                    return (value >> 1) | (oldCarry ? 0x80000000u : 0u);
                }

                var rotate = amount & 31;

                if (rotate == 0) {
                    // Amount is a non-zero multiple of 32: value unchanged, carry from the top bit.
                    carryOut = (value & 0x80000000u) != 0u;

                    return value;
                }

                carryOut = ((value >> (rotate - 1)) & 1u) != 0u;

                return (value >> rotate) | (value << (32 - rotate));
        }
    }

    private void SetNZ(uint result) {
        m_cpsr = (m_cpsr & ~(FlagN | FlagZ)) | (result & FlagN) | ((result == 0u) ? FlagZ : 0u);
    }

    private void SetCarry(bool carry) {
        m_cpsr = carry
            ? (m_cpsr | FlagC)
            : (m_cpsr & ~FlagC);
    }

    private void SetOverflow(bool overflow) {
        m_cpsr = overflow
            ? (m_cpsr | FlagV)
            : (m_cpsr & ~FlagV);
    }

    // result = a + b, optionally setting N/Z/C/V.
    private uint Add(uint a, uint b, bool setFlags) {
        var wide = (ulong)a + b;
        var result = (uint)wide;

        if (setFlags) {
            SetNZ(result: result);
            SetCarry(carry: wide > 0xFFFFFFFFul);
            SetOverflow(overflow: ((a ^ result) & (b ^ result) & 0x80000000u) != 0u);
        }

        return result;
    }

    // result = a + b + carry.
    private uint AddWithCarry(uint a, uint b, bool setFlags) {
        var carryIn = ((m_cpsr & FlagC) != 0u) ? 1ul : 0ul;
        var wide = (ulong)a + b + carryIn;
        var result = (uint)wide;

        if (setFlags) {
            SetNZ(result: result);
            SetCarry(carry: wide > 0xFFFFFFFFul);
            SetOverflow(overflow: ((a ^ result) & (b ^ result) & 0x80000000u) != 0u);
        }

        return result;
    }

    // result = a - b. Carry is set when no borrow occurs (a >= b), matching ARM semantics.
    private uint Subtract(uint a, uint b, bool setFlags) {
        var result = a - b;

        if (setFlags) {
            SetNZ(result: result);
            SetCarry(carry: a >= b);
            SetOverflow(overflow: ((a ^ b) & (a ^ result) & 0x80000000u) != 0u);
        }

        return result;
    }

    // result = a - b - NOT(carry).
    private uint SubtractWithCarry(uint a, uint b, bool setFlags) {
        var borrow = ((m_cpsr & FlagC) != 0u) ? 0ul : 1ul;
        var wide = (ulong)a - b - borrow;
        var result = (uint)wide;

        if (setFlags) {
            SetNZ(result: result);
            SetCarry(carry: (ulong)a >= ((ulong)b + borrow));
            SetOverflow(overflow: ((a ^ b) & (a ^ result) & 0x80000000u) != 0u);
        }

        return result;
    }
}
