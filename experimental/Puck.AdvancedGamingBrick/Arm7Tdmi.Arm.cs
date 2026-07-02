using System.Numerics;

namespace Puck.AdvancedGamingBrick;

public sealed partial class Arm7Tdmi {
    // Precomputed 4096-entry dispatch table for the 32-bit ARM instruction set.
    // Index = opcode[27:20] (in bits [11:4]) | opcode[7:4] (in bits [3:0]).
    // Built once at class initialisation; each entry is a static method pointer — zero-allocation,
    // no delegate invocation overhead.
    private static readonly unsafe delegate*<Arm7Tdmi, uint, void>[] s_armTable = BuildArmTable();

    private static unsafe delegate*<Arm7Tdmi, uint, void>[] BuildArmTable() {
        var table = new delegate*<Arm7Tdmi, uint, void>[4096];

        for (var idx = 0; idx < 4096; ++idx) {
            // Reconstruct the significant opcode bits from the index.
            // high8 = opcode bits [27:20], low4 = opcode bits [7:4].
            var high8 = (uint)(idx >> 4);
            var low4  = (uint)(idx & 0xF);
            var op    = (high8 << 20) | (low4 << 4);

            table[idx] = PickArmHandler(op);
        }

        return table;
    }

    private static unsafe delegate*<Arm7Tdmi, uint, void> PickArmHandler(uint op) {
        var bits27_25 = (op >> 25) & 0x7u;
        var bit4      = (op >> 4)  & 0x1u;
        var bit7      = (op >> 7)  & 0x1u;

        switch (bits27_25) {
            case 0b101u: return &ArmBranch;
            case 0b100u: return &ArmBlockDataTransfer;
            case 0b111u:
                return ((op & 0x0F000000u) == 0x0F000000u) ? &ArmSoftwareInterrupt : &ArmUndefined;
            case 0b110u:
                return &ArmUndefined;
            case 0b011u:
                return (bit4 != 0u) ? &ArmUndefined : &ArmSingleDataTransfer;
            case 0b010u:
                return &ArmSingleDataTransfer;
            default: // 0b000 or 0b001
                return PickDataProcessingSpaceHandler(op: op, bit4: bit4, bit7: bit7);
        }
    }

    private static unsafe delegate*<Arm7Tdmi, uint, void> PickDataProcessingSpaceHandler(uint op, uint bit4, uint bit7) {
        // All indices that reach here have bits[27:25] == 000 or 001.
        // Use only bits[27:20] (high8) and bits[7:4] (low4) since those are the only index bits.
        var high8 = op >> 20;
        var low4  = (op >> 4) & 0xFu;

        // Extension space: group 000 (bit25=0), bit7=1 AND bit4=1.
        if (((op >> 25) & 0x7u) == 0b000u && bit7 != 0u && bit4 != 0u) {
            // bits[6:5] = low4 bits [2:1]; non-zero → halfword/signed transfer.
            if ((low4 & 0x6u) != 0u) {
                return &ArmHalfwordTransfer;
            }

            // low4 == 0x9: multiply / long-multiply / swap.
            // Distinguish by bits[27:23] of high8.
            if ((high8 & 0xF8u) == 0x00u) { return &ArmMultiply; }
            if ((high8 & 0xF8u) == 0x08u) { return &ArmMultiplyLong; }
            // SWP: bits[27:20] = 0001_0?00, bit21 must be 0 → check with mask 0xFB.
            if ((high8 & 0xFBu) == 0x10u) { return &ArmSwap; }

            return &ArmUndefined;
        }

        // BX: bit4=1, bit7=0, bits[27:20] = 0001_0??0 (bit24=1, bit23=0, bit21/22 free, bit20=0).
        // Mask 0xF9 checks bits[27:24,23,20] leaving bits[22:21] free.
        if (bit4 != 0u && bit7 == 0u && (high8 & 0xF9u) == 0x10u) {
            return &ArmBranchExchange;
        }

        // TST/TEQ/CMP/CMN with S=0 encode PSR transfers (MRS/MSR). These indices are ambiguous at
        // the 12-bit level — bits[19:8] that distinguish MRS from MSR from data-processing are not
        // in the index — so use a combined runtime-dispatch handler.
        // S = high8 bit0, dataOp = high8 bits[4:1].
        var setFlag = high8 & 0x1u;
        var dataOp  = (high8 >> 1) & 0xFu;

        if (setFlag == 0u && dataOp >= 0x8u && dataOp <= 0xBu) {
            return &ArmDataProcessingOrPsr;
        }

        return &ArmDataProcessing;
    }

    // --- ARM instruction implementations (static, cpu passed explicitly) ---

    private static void ArmBranchExchange(Arm7Tdmi cpu, uint opcode) {
        var target = cpu.m_gpr[(int)(opcode & 0xFu)];

        if ((target & 1u) != 0u) {
            cpu.m_cpsr |= FlagT;
            cpu.BranchTo(address: target & ~1u);
        }
        else {
            cpu.m_cpsr &= ~FlagT;
            cpu.BranchTo(address: target & ~3u);
        }
    }

    private static void ArmBranch(Arm7Tdmi cpu, uint opcode) {
        var offset = ((int)(opcode << 8)) >> 6;

        if ((opcode & (1u << 24)) != 0u) {
            cpu.m_gpr[14] = cpu.m_gpr[15] - 4u;
        }

        cpu.BranchTo(address: cpu.m_gpr[15] + (uint)offset);
    }

    private static void ArmDataProcessing(Arm7Tdmi cpu, uint opcode) {
        var immediate     = (opcode & (1u << 25)) != 0u;
        var operation     = (opcode >> 21) & 0xFu;
        var setFlags      = (opcode & (1u << 20)) != 0u;
        var rn            = (int)((opcode >> 16) & 0xFu);
        var rd            = (int)((opcode >> 12) & 0xFu);
        var shiftByReg    = !immediate && ((opcode & (1u << 4)) != 0u);
        var shifterCarry  = (cpu.m_cpsr & FlagC) != 0u;
        var operandN      = cpu.m_gpr[rn];

        if (shiftByReg && (rn == 15)) {
            operandN += 4u;
        }

        uint operand2;

        if (immediate) {
            var value  = opcode & 0xFFu;
            var rotate = (int)((opcode >> 8) & 0xFu) * 2;

            if (rotate == 0) {
                operand2 = value;
            }
            else {
                operand2 = (value >> rotate) | (value << (32 - rotate));
                shifterCarry = (operand2 & 0x80000000u) != 0u;
            }
        }
        else {
            var rm       = (int)(opcode & 0xFu);
            var operandM = cpu.m_gpr[rm];

            if (shiftByReg && (rm == 15)) {
                operandM += 4u;
            }

            var shiftType = (ShiftType)((opcode >> 5) & 0x3u);
            int amount;

            if (shiftByReg) {
                amount = (int)(cpu.m_gpr[(int)((opcode >> 8) & 0xFu)] & 0xFFu);
            }
            else {
                amount = (int)((opcode >> 7) & 0x1Fu);
            }

            operand2 = BarrelShift(cpu: cpu, value: operandM, type: shiftType, amount: amount, byRegister: shiftByReg, carryOut: ref shifterCarry);
        }

        if (shiftByReg) {
            cpu.m_bus.Idle(cycles: 1);
        }

        var result = 0u;
        var write  = true;

        switch (operation) {
            case 0x0u: // AND
                result = operandN & operand2;
                ApplyLogicalFlags(cpu: cpu, setFlags: setFlags, result: result, shifterCarry: shifterCarry);
                break;
            case 0x1u: // EOR
                result = operandN ^ operand2;
                ApplyLogicalFlags(cpu: cpu, setFlags: setFlags, result: result, shifterCarry: shifterCarry);
                break;
            case 0x2u: // SUB
                result = Subtract(cpu: cpu, a: operandN, b: operand2, setFlags: setFlags);
                break;
            case 0x3u: // RSB
                result = Subtract(cpu: cpu, a: operand2, b: operandN, setFlags: setFlags);
                break;
            case 0x4u: // ADD
                result = Add(cpu: cpu, a: operandN, b: operand2, setFlags: setFlags);
                break;
            case 0x5u: // ADC
                result = AddWithCarry(cpu: cpu, a: operandN, b: operand2, setFlags: setFlags);
                break;
            case 0x6u: // SBC
                result = SubtractWithCarry(cpu: cpu, a: operandN, b: operand2, setFlags: setFlags);
                break;
            case 0x7u: // RSC
                result = SubtractWithCarry(cpu: cpu, a: operand2, b: operandN, setFlags: setFlags);
                break;
            case 0x8u: // TST
                ApplyLogicalFlags(cpu: cpu, setFlags: true, result: operandN & operand2, shifterCarry: shifterCarry);
                write = false;
                break;
            case 0x9u: // TEQ
                ApplyLogicalFlags(cpu: cpu, setFlags: true, result: operandN ^ operand2, shifterCarry: shifterCarry);
                write = false;
                break;
            case 0xAu: // CMP
                _ = Subtract(cpu: cpu, a: operandN, b: operand2, setFlags: true);
                write = false;
                break;
            case 0xBu: // CMN
                _ = Add(cpu: cpu, a: operandN, b: operand2, setFlags: true);
                write = false;
                break;
            case 0xCu: // ORR
                result = operandN | operand2;
                ApplyLogicalFlags(cpu: cpu, setFlags: setFlags, result: result, shifterCarry: shifterCarry);
                break;
            case 0xDu: // MOV
                result = operand2;
                ApplyLogicalFlags(cpu: cpu, setFlags: setFlags, result: result, shifterCarry: shifterCarry);
                break;
            case 0xEu: // BIC
                result = operandN & ~operand2;
                ApplyLogicalFlags(cpu: cpu, setFlags: setFlags, result: result, shifterCarry: shifterCarry);
                break;
            default: // 0xF MVN
                result = ~operand2;
                ApplyLogicalFlags(cpu: cpu, setFlags: setFlags, result: result, shifterCarry: shifterCarry);
                break;
        }

        if (rd == 15) {
            if (setFlags) {
                cpu.WriteCpsr(value: cpu.Spsr);
            }

            if (write) {
                cpu.BranchTo(address: result & (cpu.ThumbState ? ~1u : ~3u));
            }

            return;
        }

        if (write) {
            cpu.m_gpr[rd] = result;
        }
    }

    private static void ApplyLogicalFlags(Arm7Tdmi cpu, bool setFlags, uint result, bool shifterCarry) {
        if (setFlags) {
            SetNZ(cpu: cpu, result: result);
            SetCarry(cpu: cpu, carry: shifterCarry);
        }
    }

    // Handles indices where S=0 and dataOp ∈ {TST,TEQ,CMP,CMN} — ambiguous at the 12-bit level
    // between PSR transfers and ordinary data processing. Runtime discriminates via the full opcode.
    private static void ArmDataProcessingOrPsr(Arm7Tdmi cpu, uint opcode) {
        var dataOp   = (opcode >> 21) & 0xFu;
        var setFlags = (opcode & (1u << 20)) != 0u;

        if (!setFlags && dataOp >= 0x8u && dataOp <= 0xBu) {
            if ((opcode & 0x0FBF0FFFu) == 0x010F0000u) {
                ArmMoveStatusToRegister(cpu: cpu, opcode: opcode);
                return;
            }

            if (((opcode & 0x0FB0FFF0u) == 0x0120F000u) || ((opcode & 0x0FB0F000u) == 0x0320F000u)) {
                ArmMoveRegisterToStatus(cpu: cpu, opcode: opcode);
                return;
            }

            cpu.UndefinedInstruction();
            return;
        }

        ArmDataProcessing(cpu: cpu, opcode: opcode);
    }

    private static void ArmMoveStatusToRegister(Arm7Tdmi cpu, uint opcode) {
        var useSpsr = (opcode & (1u << 22)) != 0u;
        var rd      = (int)((opcode >> 12) & 0xFu);

        cpu.m_gpr[rd] = useSpsr ? cpu.Spsr : cpu.m_cpsr;
    }

    private static void ArmMoveRegisterToStatus(Arm7Tdmi cpu, uint opcode) {
        var useSpsr   = (opcode & (1u << 22)) != 0u;
        var immediate = (opcode & (1u << 25)) != 0u;

        uint value;

        if (immediate) {
            var imm    = opcode & 0xFFu;
            var rotate = (int)((opcode >> 8) & 0xFu) * 2;

            value = (rotate == 0) ? imm : (imm >> rotate) | (imm << (32 - rotate));
        }
        else {
            value = cpu.m_gpr[(int)(opcode & 0xFu)];
        }

        var fields = (opcode >> 16) & 0xFu;
        var mask   = 0u;

        if ((fields & 0x1u) != 0u) { mask |= 0x000000FFu; }
        if ((fields & 0x2u) != 0u) { mask |= 0x0000FF00u; }
        if ((fields & 0x4u) != 0u) { mask |= 0x00FF0000u; }
        if ((fields & 0x8u) != 0u) { mask |= 0xFF000000u; }

        // Only defined PSR bits are writable.
        mask &= 0xF00000FFu;

        if (useSpsr) {
            if (cpu.HasSpsr) {
                cpu.Spsr = (cpu.Spsr & ~mask) | (value & mask);
            }

            return;
        }

        // Control byte writable only in privileged modes.
        if (cpu.CurrentMode == (uint)CpuMode.User) {
            mask &= 0xF0000000u;
        }

        cpu.WriteCpsr(value: (cpu.m_cpsr & ~mask) | (value & mask));
    }

    private static void ArmSingleDataTransfer(Arm7Tdmi cpu, uint opcode) {
        var registerOffset = (opcode & (1u << 25)) != 0u;
        var preIndex       = (opcode & (1u << 24)) != 0u;
        var add            = (opcode & (1u << 23)) != 0u;
        var byteAccess     = (opcode & (1u << 22)) != 0u;
        var writeBack      = (opcode & (1u << 21)) != 0u;
        var load           = (opcode & (1u << 20)) != 0u;
        var rn             = (int)((opcode >> 16) & 0xFu);
        var rd             = (int)((opcode >> 12) & 0xFu);

        uint offset;

        if (registerOffset) {
            var operandM  = cpu.m_gpr[(int)(opcode & 0xFu)];
            var shiftType = (ShiftType)((opcode >> 5) & 0x3u);
            var amount    = (int)((opcode >> 7) & 0x1Fu);
            var ignored   = (cpu.m_cpsr & FlagC) != 0u;

            offset = BarrelShift(cpu: cpu, value: operandM, type: shiftType, amount: amount, byRegister: false, carryOut: ref ignored);
        }
        else {
            offset = opcode & 0xFFFu;
        }

        var baseAddress = cpu.m_gpr[rn];
        var address     = baseAddress;

        if (preIndex) {
            address = add ? (baseAddress + offset) : (baseAddress - offset);
        }

        if (load) {
            var data = byteAccess
                ? cpu.m_bus.Read8(address: address, access: BusAccessType.NonSequential)
                : ReadWordRotated(cpu: cpu, address: address);

            cpu.m_bus.Idle(cycles: 1);

            WriteBackBase(cpu: cpu, rn: rn, preIndex: preIndex, writeBack: writeBack, baseAddress: baseAddress, indexedAddress: address, offset: offset, add: add);

            if (rd == 15) {
                cpu.BranchTo(address: data & ~3u);
            }
            else {
                cpu.m_gpr[rd] = data;
            }
        }
        else {
            var data = (rd == 15) ? cpu.m_gpr[15] + 4u : cpu.m_gpr[rd];

            if (byteAccess) {
                cpu.m_bus.Write8(address: address, value: (byte)data, access: BusAccessType.NonSequential);
            }
            else {
                cpu.m_bus.Write32(address: address, value: data, access: BusAccessType.NonSequential);
            }

            WriteBackBase(cpu: cpu, rn: rn, preIndex: preIndex, writeBack: writeBack, baseAddress: baseAddress, indexedAddress: address, offset: offset, add: add);
        }

        cpu.m_nextFetchNonSequential = true;
    }

    private static void ArmHalfwordTransfer(Arm7Tdmi cpu, uint opcode) {
        var preIndex  = (opcode & (1u << 24)) != 0u;
        var add       = (opcode & (1u << 23)) != 0u;
        var immediate = (opcode & (1u << 22)) != 0u;
        var writeBack = (opcode & (1u << 21)) != 0u;
        var load      = (opcode & (1u << 20)) != 0u;
        var rn        = (int)((opcode >> 16) & 0xFu);
        var rd        = (int)((opcode >> 12) & 0xFu);
        var kind      = (opcode >> 5) & 0x3u;

        var offset = immediate
            ? (((opcode >> 4) & 0xF0u) | (opcode & 0xFu))
            : cpu.m_gpr[(int)(opcode & 0xFu)];

        var baseAddress = cpu.m_gpr[rn];
        var address     = baseAddress;

        if (preIndex) {
            address = add ? (baseAddress + offset) : (baseAddress - offset);
        }

        if (load) {
            uint data;

            switch (kind) {
                case 1u: // LDRH (unsigned halfword)
                    data = cpu.m_bus.Read16(address: address & ~1u, access: BusAccessType.NonSequential);

                    if ((address & 1u) != 0u) {
                        data = (data >> 8) | (data << 24);
                    }

                    break;
                case 2u: // LDRSB (signed byte)
                    data = (uint)(sbyte)cpu.m_bus.Read8(address: address, access: BusAccessType.NonSequential);
                    break;
                default: // LDRSH (odd address degrades to signed byte)
                    if ((address & 1u) != 0u) {
                        data = (uint)(sbyte)cpu.m_bus.Read8(address: address, access: BusAccessType.NonSequential);
                    }
                    else {
                        data = (uint)(short)cpu.m_bus.Read16(address: address, access: BusAccessType.NonSequential);
                    }

                    break;
            }

            cpu.m_bus.Idle(cycles: 1);

            WriteBackBase(cpu: cpu, rn: rn, preIndex: preIndex, writeBack: writeBack, baseAddress: baseAddress, indexedAddress: address, offset: offset, add: add);

            if (rd == 15) {
                cpu.BranchTo(address: data & ~3u);
            }
            else {
                cpu.m_gpr[rd] = data;
            }
        }
        else {
            cpu.m_bus.Write16(address: address, value: (ushort)cpu.m_gpr[rd], access: BusAccessType.NonSequential);

            WriteBackBase(cpu: cpu, rn: rn, preIndex: preIndex, writeBack: writeBack, baseAddress: baseAddress, indexedAddress: address, offset: offset, add: add);
        }

        cpu.m_nextFetchNonSequential = true;
    }

    private static void WriteBackBase(Arm7Tdmi cpu, int rn, bool preIndex, bool writeBack, uint baseAddress, uint indexedAddress, uint offset, bool add) {
        if (!preIndex) {
            cpu.m_gpr[rn] = add ? (baseAddress + offset) : (baseAddress - offset);
        }
        else if (writeBack) {
            cpu.m_gpr[rn] = indexedAddress;
        }
    }

    private static uint ReadWordRotated(Arm7Tdmi cpu, uint address) {
        var data   = cpu.m_bus.Read32(address: address & ~3u, access: BusAccessType.NonSequential);
        var rotate = (int)((address & 3u) * 8u);

        return (rotate == 0) ? data : (data >> rotate) | (data << (32 - rotate));
    }

    private static void ArmBlockDataTransfer(Arm7Tdmi cpu, uint opcode) {
        var preIndex     = (opcode & (1u << 24)) != 0u;
        var add          = (opcode & (1u << 23)) != 0u;
        var useUserBank  = (opcode & (1u << 22)) != 0u;
        var writeBack    = (opcode & (1u << 21)) != 0u;
        var load         = (opcode & (1u << 20)) != 0u;
        var rn           = (int)((opcode >> 16) & 0xFu);
        var list         = opcode & 0xFFFFu;

        if (list == 0u) {
            var emptyBase    = cpu.m_gpr[rn];
            var emptyAddress = add
                ? (preIndex ? (emptyBase + 4u) : emptyBase)
                : (preIndex ? (emptyBase - 0x40u) : (emptyBase - 0x40u + 4u));
            var emptyFinal = add ? (emptyBase + 0x40u) : (emptyBase - 0x40u);

            if (load) {
                var data = cpu.m_bus.Read32(address: emptyAddress, access: BusAccessType.NonSequential);

                if (writeBack) { cpu.m_gpr[rn] = emptyFinal; }

                cpu.BranchTo(address: data & ~3u);
            }
            else {
                cpu.m_bus.Write32(address: emptyAddress, value: cpu.m_gpr[15] + 4u, access: BusAccessType.NonSequential);

                if (writeBack) { cpu.m_gpr[rn] = emptyFinal; }
            }

            cpu.m_nextFetchNonSequential = true;

            return;
        }

        var count       = BitOperations.PopCount(list);
        var baseAddress = cpu.m_gpr[rn];
        var total       = (uint)count * 4u;

        var address = add
            ? (preIndex ? (baseAddress + 4u) : baseAddress)
            : (preIndex ? (baseAddress - total) : (baseAddress - total + 4u));

        var finalBase = add ? (baseAddress + total) : (baseAddress - total);

        var restoreCpsr  = load && useUserBank && ((list & 0x8000u) != 0u);
        var transferUser = useUserBank && !restoreCpsr;
        var savedMode    = cpu.CurrentMode;
        var swappedBank  = false;

        if (transferUser && (savedMode != (uint)CpuMode.User) && (savedMode != (uint)CpuMode.System)) {
            cpu.SwitchMode(newMode: (uint)CpuMode.System);
            swappedBank = true;
        }

        var firstRegister = BitOperations.TrailingZeroCount(list);
        var access        = BusAccessType.NonSequential;
        var branched      = false;
        var loadedData    = 0u;

        for (var register = 0; register < 16; ++register) {
            if (((list >> register) & 1u) == 0u) {
                continue;
            }

            if (load) {
                var data = cpu.m_bus.Read32(address: address, access: access);

                if (register == 15) {
                    branched   = true;
                    loadedData = data;
                }
                else {
                    cpu.m_gpr[register] = data;
                }
            }
            else {
                var data = (register == 15) ? cpu.m_gpr[15] + 4u : cpu.m_gpr[register];

                if ((register == rn) && (register != firstRegister)) {
                    data = finalBase;
                }

                cpu.m_bus.Write32(address: address, value: data, access: access);
            }

            address += 4u;
            access = BusAccessType.Sequential;
        }

        // LDM has a trailing internal cycle (n S + 1N + 1I); STM does NOT — its trailing N is the next opcode
        // fetch, already accounted for by m_nextFetchNonSequential below. Verified against the ARES bus trace.
        if (load) {
            cpu.m_bus.Idle(cycles: 1);
        }

        if (swappedBank) {
            cpu.SwitchMode(newMode: savedMode);
        }

        if (writeBack && !(load && (((list >> rn) & 1u) != 0u))) {
            cpu.m_gpr[rn] = finalBase;
        }

        if (branched) {
            if (restoreCpsr) {
                cpu.WriteCpsr(value: cpu.Spsr);
            }

            cpu.BranchTo(address: loadedData & (cpu.ThumbState ? ~1u : ~3u));
        }

        cpu.m_nextFetchNonSequential = true;
    }

    private static void ArmMultiply(Arm7Tdmi cpu, uint opcode) {
        var accumulate = (opcode & (1u << 21)) != 0u;
        var setFlags   = (opcode & (1u << 20)) != 0u;
        var rd         = (int)((opcode >> 16) & 0xFu);
        var rn         = (int)((opcode >> 12) & 0xFu);
        var rs         = (int)((opcode >> 8) & 0xFu);
        var rm         = (int)(opcode & 0xFu);

        // Capture the multiplier before the destination write: when rd == rs the early-termination count must use
        // the original value, not the product (ares reads r(s) before storing).
        var multiplier = cpu.m_gpr[rs];
        var result = cpu.m_gpr[rm] * multiplier;

        if (accumulate) {
            result += cpu.m_gpr[rn];
        }

        cpu.m_gpr[rd] = result;

        if (setFlags) {
            SetNZ(cpu: cpu, result: result);
        }

        cpu.m_bus.Idle(cycles: MultiplyCycles(multiplier: multiplier, signedMultiplier: true) + (accumulate ? 1 : 0));
    }

    private static void ArmMultiplyLong(Arm7Tdmi cpu, uint opcode) {
        var signed     = (opcode & (1u << 22)) != 0u;
        var accumulate = (opcode & (1u << 21)) != 0u;
        var setFlags   = (opcode & (1u << 20)) != 0u;
        var rdHigh     = (int)((opcode >> 16) & 0xFu);
        var rdLow      = (int)((opcode >> 12) & 0xFu);
        var rs         = (int)((opcode >> 8) & 0xFu);
        var rm         = (int)(opcode & 0xFu);

        // Capture the multiplier before the destination writes: when rs == rdLow/rdHigh the early-termination
        // count must use the original value, not the product (ares reads r(s) before storing).
        var multiplier = cpu.m_gpr[rs];

        ulong result;

        if (signed) {
            var product = (long)(int)cpu.m_gpr[rm] * (int)multiplier;

            if (accumulate) {
                product += (long)(((ulong)cpu.m_gpr[rdHigh] << 32) | cpu.m_gpr[rdLow]);
            }

            result = (ulong)product;
        }
        else {
            result = (ulong)cpu.m_gpr[rm] * multiplier;

            if (accumulate) {
                result += ((ulong)cpu.m_gpr[rdHigh] << 32) | cpu.m_gpr[rdLow];
            }
        }

        cpu.m_gpr[rdLow]  = (uint)result;
        cpu.m_gpr[rdHigh] = (uint)(result >> 32);

        if (setFlags) {
            cpu.m_cpsr = (cpu.m_cpsr & ~(FlagN | FlagZ))
                | (((result & 0x8000000000000000ul) != 0ul) ? FlagN : 0u)
                | ((result == 0ul) ? FlagZ : 0u);
        }

        cpu.m_bus.Idle(cycles: MultiplyCycles(multiplier: multiplier, signedMultiplier: signed) + (accumulate ? 2 : 1));
    }

    private static void ArmSwap(Arm7Tdmi cpu, uint opcode) {
        var byteAccess = (opcode & (1u << 22)) != 0u;
        var rn         = (int)((opcode >> 16) & 0xFu);
        var rd         = (int)((opcode >> 12) & 0xFu);
        var rm         = (int)(opcode & 0xFu);
        var address    = cpu.m_gpr[rn];

        if (byteAccess) {
            var old = cpu.m_bus.Read8(address: address, access: BusAccessType.NonSequential);

            cpu.m_bus.Write8(address: address, value: (byte)cpu.m_gpr[rm], access: BusAccessType.NonSequential);

            cpu.m_gpr[rd] = old;
        }
        else {
            var old = ReadWordRotated(cpu: cpu, address: address);

            cpu.m_bus.Write32(address: address & ~3u, value: cpu.m_gpr[rm], access: BusAccessType.NonSequential);

            cpu.m_gpr[rd] = old;
        }

        cpu.m_bus.Idle(cycles: 1);
        cpu.m_nextFetchNonSequential = true;
    }

    private static void ArmSoftwareInterrupt(Arm7Tdmi cpu, uint opcode) {
        cpu.SoftwareInterrupt();
    }

    private static void ArmUndefined(Arm7Tdmi cpu, uint opcode) {
        cpu.UndefinedInstruction();
    }
}
