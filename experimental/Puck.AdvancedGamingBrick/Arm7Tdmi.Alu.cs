namespace Puck.AdvancedGamingBrick;

public sealed partial class Arm7Tdmi {
    private static bool CheckCondition(Arm7Tdmi cpu, uint condition) {
        var cpsr = cpu.m_cpsr;
        var n = (cpsr & FlagN) != 0u;
        var z = (cpsr & FlagZ) != 0u;
        var c = (cpsr & FlagC) != 0u;
        var v = (cpsr & FlagV) != 0u;

        return condition switch {
            0x0u => z,              // EQ
            0x1u => !z,             // NE
            0x2u => c,              // CS/HS
            0x3u => !c,             // CC/LO
            0x4u => n,              // MI
            0x5u => !n,             // PL
            0x6u => v,              // VS
            0x7u => !v,             // VC
            0x8u => c && !z,        // HI
            0x9u => !c || z,        // LS
            0xAu => n == v,         // GE
            0xBu => n != v,         // LT
            0xCu => !z && (n == v), // GT
            0xDu => z || (n != v),  // LE
            0xEu => true,           // AL
            _ => false,             // 0xF: reserved, never executes
        };
    }

    // The barrel shifter. Returns the shifted value and updates carryOut. byRegister selects the
    // register-specified-amount semantics (different handling of zero and amounts >= 32).
    private static uint BarrelShift(Arm7Tdmi cpu, uint value, ShiftType type, int amount, bool byRegister, ref bool carryOut) {
        if (byRegister && (amount == 0)) {
            return value;
        }

        switch (type) {
            case ShiftType.LogicalLeft:
                if (amount == 0) {
                    return value;
                }

                if (amount < 32) {
                    carryOut = ((value >> (32 - amount)) & 1u) != 0u;

                    return value << amount;
                }

                carryOut = (amount == 32) && ((value & 1u) != 0u);

                return 0u;

            case ShiftType.LogicalRight:
                if ((amount == 0) || (amount == 32)) {
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
                    carryOut = (value & 0x80000000u) != 0u;

                    return (uint)((int)value >> 31);
                }

                carryOut = ((value >> (amount - 1)) & 1u) != 0u;

                return (uint)((int)value >> amount);

            default: // RotateRight
                if (amount == 0) {
                    // ROR #0 (immediate) encodes RRX: 33-bit rotate through carry.
                    var oldCarry = (cpu.m_cpsr & FlagC) != 0u;

                    carryOut = (value & 1u) != 0u;

                    return (value >> 1) | (oldCarry ? 0x80000000u : 0u);
                }

                var rotate = amount & 31;

                if (rotate == 0) {
                    carryOut = (value & 0x80000000u) != 0u;

                    return value;
                }

                carryOut = ((value >> (rotate - 1)) & 1u) != 0u;

                return (value >> rotate) | (value << (32 - rotate));
        }
    }

    private static void SetNZ(Arm7Tdmi cpu, uint result) {
        cpu.m_cpsr = (cpu.m_cpsr & ~(FlagN | FlagZ)) | (result & FlagN) | ((result == 0u) ? FlagZ : 0u);
    }

    private static void SetCarry(Arm7Tdmi cpu, bool carry) {
        cpu.m_cpsr = carry ? (cpu.m_cpsr | FlagC) : (cpu.m_cpsr & ~FlagC);
    }

    private static void SetOverflow(Arm7Tdmi cpu, bool overflow) {
        cpu.m_cpsr = overflow ? (cpu.m_cpsr | FlagV) : (cpu.m_cpsr & ~FlagV);
    }

    private static uint Add(Arm7Tdmi cpu, uint a, uint b, bool setFlags) {
        var wide = (ulong)a + b;
        var result = (uint)wide;

        if (setFlags) {
            SetNZ(cpu: cpu, result: result);
            SetCarry(cpu: cpu, carry: wide > 0xFFFFFFFFul);
            SetOverflow(cpu: cpu, overflow: ((a ^ result) & (b ^ result) & 0x80000000u) != 0u);
        }

        return result;
    }

    private static uint AddWithCarry(Arm7Tdmi cpu, uint a, uint b, bool setFlags) {
        var carryIn = ((cpu.m_cpsr & FlagC) != 0u) ? 1ul : 0ul;
        var wide = (ulong)a + b + carryIn;
        var result = (uint)wide;

        if (setFlags) {
            SetNZ(cpu: cpu, result: result);
            SetCarry(cpu: cpu, carry: wide > 0xFFFFFFFFul);
            SetOverflow(cpu: cpu, overflow: ((a ^ result) & (b ^ result) & 0x80000000u) != 0u);
        }

        return result;
    }

    private static uint Subtract(Arm7Tdmi cpu, uint a, uint b, bool setFlags) {
        var result = a - b;

        if (setFlags) {
            SetNZ(cpu: cpu, result: result);
            SetCarry(cpu: cpu, carry: a >= b);
            SetOverflow(cpu: cpu, overflow: ((a ^ b) & (a ^ result) & 0x80000000u) != 0u);
        }

        return result;
    }

    private static uint SubtractWithCarry(Arm7Tdmi cpu, uint a, uint b, bool setFlags) {
        var borrow = ((cpu.m_cpsr & FlagC) != 0u) ? 0ul : 1ul;
        var wide = (ulong)a - b - borrow;
        var result = (uint)wide;

        if (setFlags) {
            SetNZ(cpu: cpu, result: result);
            SetCarry(cpu: cpu, carry: (ulong)a >= ((ulong)b + borrow));
            SetOverflow(cpu: cpu, overflow: ((a ^ b) & (a ^ result) & 0x80000000u) != 0u);
        }

        return result;
    }

    // ARM7TDMI early termination: the multiplier is consumed 8 bits per cycle, stopping once the remaining upper
    // bits are all zeros — or all ones for the SIGNED variants only, where they are just sign extension. Unsigned
    // long multiplies get no all-ones shortcut (GBATEK; ares algorithms.cpp MUL vs instructions-arm.cpp:332-342).
    private static int MultiplyCycles(uint multiplier, bool signedMultiplier) {
        var cycles = 1;

        if (((multiplier >> 8) != 0u) && (!signedMultiplier || ((multiplier >> 8) != 0x00FFFFFFu))) {
            ++cycles;
        }

        if (((multiplier >> 16) != 0u) && (!signedMultiplier || ((multiplier >> 16) != 0x0000FFFFu))) {
            ++cycles;
        }

        if (((multiplier >> 24) != 0u) && (!signedMultiplier || ((multiplier >> 24) != 0x000000FFu))) {
            ++cycles;
        }

        return cycles;
    }
}
