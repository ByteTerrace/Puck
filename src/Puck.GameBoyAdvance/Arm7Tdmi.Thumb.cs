using System.Numerics;

namespace Puck.GameBoyAdvance;

public sealed partial class Arm7Tdmi {
    // Precomputed 256-entry dispatch table for the 16-bit Thumb instruction set.
    // Index = opcode >> 8 (top byte). All Thumb instruction classes are fully determined by the top 8 bits.
    private static readonly unsafe delegate*<Arm7Tdmi, ushort, void>[] s_thumbTable = BuildThumbTable();

    private static unsafe delegate*<Arm7Tdmi, ushort, void>[] BuildThumbTable() {
        var table = new delegate*<Arm7Tdmi, ushort, void>[256];

        for (var idx = 0; idx < 256; ++idx) {
            // Reconstruct a representative opcode with the low byte zeroed (only the top byte matters for class
            // selection; operands are decoded from the full opcode at runtime).
            var op = (ushort)(idx << 8);

            table[idx] = PickThumbHandler(op: op);
        }

        return table;
    }

    private static unsafe delegate*<Arm7Tdmi, ushort, void> PickThumbHandler(ushort op) {
        var top3 = (uint)op >> 13;

        switch (top3) {
            case 0b000u:
                return ((op & 0x1800u) == 0x1800u) ? &ThumbAddSubtract : &ThumbMoveShifted;

            case 0b001u:
                return &ThumbMoveCompareAddSubImmediate;

            case 0b010u:
                if ((op & 0x1000u) == 0u) {
                    if ((op & 0x0800u) == 0u) {
                        return ((op & 0x0400u) == 0u) ? &ThumbAluOperations : &ThumbHiRegisterOperations;
                    }

                    return &ThumbPcRelativeLoad;
                }

                return ((op & 0x0200u) == 0u) ? &ThumbLoadStoreRegisterOffset : &ThumbLoadStoreSignExtended;

            case 0b011u:
                return &ThumbLoadStoreImmediateOffset;

            case 0b100u:
                return ((op & 0x1000u) == 0u) ? &ThumbLoadStoreHalfword : &ThumbStackPointerRelativeLoadStore;

            case 0b101u:
                if ((op & 0x1000u) == 0u) {
                    return &ThumbLoadAddress;
                }

                if ((op & 0x0F00u) == 0x0000u) {
                    return &ThumbAddOffsetToStackPointer;
                }

                return ((op & 0x0600u) == 0x0400u) ? &ThumbPushPop : &ThumbUndefined;

            case 0b110u:
                if ((op & 0x1000u) == 0u) {
                    return &ThumbMultipleLoadStore;
                }

                if ((op & 0x0F00u) == 0x0F00u) {
                    return &ThumbSoftwareInterrupt;
                }

                return &ThumbConditionalBranch;

            default: // 0b111
                return ((op & 0x1000u) == 0u) ? &ThumbUnconditionalBranch : &ThumbLongBranchWithLink;
        }
    }

    // --- Thumb instruction implementations (static, cpu passed explicitly) ---

    private static void ThumbMoveShifted(Arm7Tdmi cpu, ushort opcode) {
        var type   = (ShiftType)((opcode >> 11) & 0x3u);
        var amount = (int)((opcode >> 6) & 0x1Fu);
        var rs     = (int)((opcode >> 3) & 0x7u);
        var rd     = (int)(opcode & 0x7u);
        var carry  = (cpu.m_cpsr & FlagC) != 0u;

        var result = BarrelShift(cpu: cpu, value: cpu.m_gpr[rs], type: type, amount: amount, byRegister: false, carryOut: ref carry);

        cpu.m_gpr[rd] = result;

        SetNZ(cpu: cpu, result: result);
        SetCarry(cpu: cpu, carry: carry);
    }

    private static void ThumbAddSubtract(Arm7Tdmi cpu, ushort opcode) {
        var immediate = (opcode & (1u << 10)) != 0u;
        var subtract  = (opcode & (1u << 9)) != 0u;
        var operand   = immediate
            ? (((uint)opcode >> 6) & 0x7u)
            : cpu.m_gpr[(int)((opcode >> 6) & 0x7u)];
        var rs = (int)((opcode >> 3) & 0x7u);
        var rd = (int)(opcode & 0x7u);

        cpu.m_gpr[rd] = subtract
            ? Subtract(cpu: cpu, a: cpu.m_gpr[rs], b: operand, setFlags: true)
            : Add(cpu: cpu, a: cpu.m_gpr[rs], b: operand, setFlags: true);
    }

    private static void ThumbMoveCompareAddSubImmediate(Arm7Tdmi cpu, ushort opcode) {
        var operation = (opcode >> 11) & 0x3u;
        var rd        = (int)((opcode >> 8) & 0x7u);
        var immediate = (uint)(opcode & 0xFFu);

        switch (operation) {
            case 0u: // MOV
                cpu.m_gpr[rd] = immediate;
                SetNZ(cpu: cpu, result: immediate);
                break;
            case 1u: // CMP
                _ = Subtract(cpu: cpu, a: cpu.m_gpr[rd], b: immediate, setFlags: true);
                break;
            case 2u: // ADD
                cpu.m_gpr[rd] = Add(cpu: cpu, a: cpu.m_gpr[rd], b: immediate, setFlags: true);
                break;
            default: // SUB
                cpu.m_gpr[rd] = Subtract(cpu: cpu, a: cpu.m_gpr[rd], b: immediate, setFlags: true);
                break;
        }
    }

    private static void ThumbAluOperations(Arm7Tdmi cpu, ushort opcode) {
        var operation   = (opcode >> 6) & 0xFu;
        var rs          = (int)((opcode >> 3) & 0x7u);
        var rd          = (int)(opcode & 0x7u);
        var source      = cpu.m_gpr[rs];
        var destination = cpu.m_gpr[rd];

        switch (operation) {
            case 0x0u: // AND
                cpu.m_gpr[rd] = destination & source;
                SetNZ(cpu: cpu, result: cpu.m_gpr[rd]);
                break;
            case 0x1u: // EOR
                cpu.m_gpr[rd] = destination ^ source;
                SetNZ(cpu: cpu, result: cpu.m_gpr[rd]);
                break;
            case 0x2u: // LSL
                ThumbShiftByRegister(cpu: cpu, rd: rd, value: destination, type: ShiftType.LogicalLeft, amount: source);
                break;
            case 0x3u: // LSR
                ThumbShiftByRegister(cpu: cpu, rd: rd, value: destination, type: ShiftType.LogicalRight, amount: source);
                break;
            case 0x4u: // ASR
                ThumbShiftByRegister(cpu: cpu, rd: rd, value: destination, type: ShiftType.ArithmeticRight, amount: source);
                break;
            case 0x5u: // ADC
                cpu.m_gpr[rd] = AddWithCarry(cpu: cpu, a: destination, b: source, setFlags: true);
                break;
            case 0x6u: // SBC
                cpu.m_gpr[rd] = SubtractWithCarry(cpu: cpu, a: destination, b: source, setFlags: true);
                break;
            case 0x7u: // ROR
                ThumbShiftByRegister(cpu: cpu, rd: rd, value: destination, type: ShiftType.RotateRight, amount: source);
                break;
            case 0x8u: // TST
                SetNZ(cpu: cpu, result: destination & source);
                break;
            case 0x9u: // NEG
                cpu.m_gpr[rd] = Subtract(cpu: cpu, a: 0u, b: source, setFlags: true);
                break;
            case 0xAu: // CMP
                _ = Subtract(cpu: cpu, a: destination, b: source, setFlags: true);
                break;
            case 0xBu: // CMN
                _ = Add(cpu: cpu, a: destination, b: source, setFlags: true);
                break;
            case 0xCu: // ORR
                cpu.m_gpr[rd] = destination | source;
                SetNZ(cpu: cpu, result: cpu.m_gpr[rd]);
                break;
            case 0xDu: // MUL
                cpu.m_gpr[rd] = destination * source;
                SetNZ(cpu: cpu, result: cpu.m_gpr[rd]);
                cpu.m_bus.Idle(cycles: MultiplyCycles(multiplier: destination));
                break;
            case 0xEu: // BIC
                cpu.m_gpr[rd] = destination & ~source;
                SetNZ(cpu: cpu, result: cpu.m_gpr[rd]);
                break;
            default: // MVN
                cpu.m_gpr[rd] = ~source;
                SetNZ(cpu: cpu, result: cpu.m_gpr[rd]);
                break;
        }
    }

    private static void ThumbShiftByRegister(Arm7Tdmi cpu, int rd, uint value, ShiftType type, uint amount) {
        var carry  = (cpu.m_cpsr & FlagC) != 0u;
        var result = BarrelShift(cpu: cpu, value: value, type: type, amount: (int)(amount & 0xFFu), byRegister: true, carryOut: ref carry);

        cpu.m_gpr[rd] = result;

        SetNZ(cpu: cpu, result: result);
        SetCarry(cpu: cpu, carry: carry);
        cpu.m_bus.Idle(cycles: 1);
    }

    private static void ThumbHiRegisterOperations(Arm7Tdmi cpu, ushort opcode) {
        var operation = (opcode >> 8) & 0x3u;
        var rs        = (int)(((opcode >> 3) & 0x7u) | ((opcode >> 3) & 0x8u));
        var rd        = (int)((opcode & 0x7u) | ((opcode >> 4) & 0x8u));

        switch (operation) {
            case 0u: // ADD (no flags)
                {
                    var result = cpu.m_gpr[rd] + cpu.m_gpr[rs];

                    if (rd == 15) {
                        cpu.BranchTo(address: result & ~1u);
                    }
                    else {
                        cpu.m_gpr[rd] = result;
                    }
                }
                break;
            case 1u: // CMP (flags only)
                _ = Subtract(cpu: cpu, a: cpu.m_gpr[rd], b: cpu.m_gpr[rs], setFlags: true);
                break;
            case 2u: // MOV (no flags)
                if (rd == 15) {
                    cpu.BranchTo(address: cpu.m_gpr[rs] & ~1u);
                }
                else {
                    cpu.m_gpr[rd] = cpu.m_gpr[rs];
                }

                break;
            default: // BX
                {
                    var target = cpu.m_gpr[rs];

                    if ((target & 1u) != 0u) {
                        cpu.m_cpsr |= FlagT;
                        cpu.BranchTo(address: target & ~1u);
                    }
                    else {
                        cpu.m_cpsr &= ~FlagT;
                        cpu.BranchTo(address: target & ~3u);
                    }
                }
                break;
        }
    }

    private static void ThumbPcRelativeLoad(Arm7Tdmi cpu, ushort opcode) {
        var rd      = (int)((opcode >> 8) & 0x7u);
        var offset  = (uint)(opcode & 0xFFu) * 4u;
        var address = (cpu.m_gpr[15] & ~2u) + offset;

        cpu.m_gpr[rd] = cpu.m_bus.Read32(address: address, access: BusAccessType.NonSequential);

        cpu.m_bus.Idle(cycles: 1);
        cpu.m_nextFetchNonSequential = true;
    }

    private static void ThumbLoadStoreRegisterOffset(Arm7Tdmi cpu, ushort opcode) {
        var load           = (opcode & (1u << 11)) != 0u;
        var byteAccess     = (opcode & (1u << 10)) != 0u;
        var offsetRegister = (int)((opcode >> 6) & 0x7u);
        var baseRegister   = (int)((opcode >> 3) & 0x7u);
        var rd             = (int)(opcode & 0x7u);
        var address        = cpu.m_gpr[baseRegister] + cpu.m_gpr[offsetRegister];

        if (load) {
            cpu.m_gpr[rd] = byteAccess
                ? cpu.m_bus.Read8(address: address, access: BusAccessType.NonSequential)
                : ReadWordRotated(cpu: cpu, address: address);

            cpu.m_bus.Idle(cycles: 1);
        }
        else if (byteAccess) {
            cpu.m_bus.Write8(address: address, value: (byte)cpu.m_gpr[rd], access: BusAccessType.NonSequential);
        }
        else {
            cpu.m_bus.Write32(address: address, value: cpu.m_gpr[rd], access: BusAccessType.NonSequential);
        }

        cpu.m_nextFetchNonSequential = true;
    }

    private static void ThumbLoadStoreSignExtended(Arm7Tdmi cpu, ushort opcode) {
        // operation = (S << 1) | H: 0=STRH, 1=LDRH, 2=LDRSB, 3=LDRSH.
        var operation    = ((opcode >> 9) & 0x2u) | ((opcode >> 11) & 0x1u);
        var offsetRegister = (int)((opcode >> 6) & 0x7u);
        var baseRegister   = (int)((opcode >> 3) & 0x7u);
        var rd             = (int)(opcode & 0x7u);
        var address        = cpu.m_gpr[baseRegister] + cpu.m_gpr[offsetRegister];

        switch (operation) {
            case 0u: // STRH
                cpu.m_bus.Write16(address: address, value: (ushort)cpu.m_gpr[rd], access: BusAccessType.NonSequential);
                break;
            case 1u: // LDRH
                {
                    uint data = cpu.m_bus.Read16(address: address & ~1u, access: BusAccessType.NonSequential);

                    if ((address & 1u) != 0u) {
                        data = (data >> 8) | (data << 24);
                    }

                    cpu.m_gpr[rd] = data;
                    cpu.m_bus.Idle(cycles: 1);
                }
                break;
            case 2u: // LDRSB
                cpu.m_gpr[rd] = (uint)(sbyte)cpu.m_bus.Read8(address: address, access: BusAccessType.NonSequential);
                cpu.m_bus.Idle(cycles: 1);
                break;
            default: // LDRSH (odd address degrades to signed byte)
                if ((address & 1u) != 0u) {
                    cpu.m_gpr[rd] = (uint)(sbyte)cpu.m_bus.Read8(address: address, access: BusAccessType.NonSequential);
                }
                else {
                    cpu.m_gpr[rd] = (uint)(short)cpu.m_bus.Read16(address: address, access: BusAccessType.NonSequential);
                }

                cpu.m_bus.Idle(cycles: 1);
                break;
        }

        cpu.m_nextFetchNonSequential = true;
    }

    private static void ThumbLoadStoreImmediateOffset(Arm7Tdmi cpu, ushort opcode) {
        var byteAccess   = (opcode & (1u << 12)) != 0u;
        var load         = (opcode & (1u << 11)) != 0u;
        var offset       = ((uint)opcode >> 6) & 0x1Fu;
        var baseRegister = (int)((opcode >> 3) & 0x7u);
        var rd           = (int)(opcode & 0x7u);
        var address      = byteAccess
            ? (cpu.m_gpr[baseRegister] + offset)
            : (cpu.m_gpr[baseRegister] + (offset * 4u));

        if (load) {
            cpu.m_gpr[rd] = byteAccess
                ? cpu.m_bus.Read8(address: address, access: BusAccessType.NonSequential)
                : ReadWordRotated(cpu: cpu, address: address);

            cpu.m_bus.Idle(cycles: 1);
        }
        else if (byteAccess) {
            cpu.m_bus.Write8(address: address, value: (byte)cpu.m_gpr[rd], access: BusAccessType.NonSequential);
        }
        else {
            cpu.m_bus.Write32(address: address, value: cpu.m_gpr[rd], access: BusAccessType.NonSequential);
        }

        cpu.m_nextFetchNonSequential = true;
    }

    private static void ThumbLoadStoreHalfword(Arm7Tdmi cpu, ushort opcode) {
        var load         = (opcode & (1u << 11)) != 0u;
        var offset       = (((uint)opcode >> 6) & 0x1Fu) * 2u;
        var baseRegister = (int)((opcode >> 3) & 0x7u);
        var rd           = (int)(opcode & 0x7u);
        var address      = cpu.m_gpr[baseRegister] + offset;

        if (load) {
            uint data = cpu.m_bus.Read16(address: address & ~1u, access: BusAccessType.NonSequential);

            if ((address & 1u) != 0u) {
                data = (data >> 8) | (data << 24);
            }

            cpu.m_gpr[rd] = data;
            cpu.m_bus.Idle(cycles: 1);
        }
        else {
            cpu.m_bus.Write16(address: address, value: (ushort)cpu.m_gpr[rd], access: BusAccessType.NonSequential);
        }

        cpu.m_nextFetchNonSequential = true;
    }

    private static void ThumbStackPointerRelativeLoadStore(Arm7Tdmi cpu, ushort opcode) {
        var load    = (opcode & (1u << 11)) != 0u;
        var rd      = (int)((opcode >> 8) & 0x7u);
        var address = cpu.m_gpr[13] + ((uint)(opcode & 0xFFu) * 4u);

        if (load) {
            cpu.m_gpr[rd] = ReadWordRotated(cpu: cpu, address: address);
            cpu.m_bus.Idle(cycles: 1);
        }
        else {
            cpu.m_bus.Write32(address: address, value: cpu.m_gpr[rd], access: BusAccessType.NonSequential);
        }

        cpu.m_nextFetchNonSequential = true;
    }

    private static void ThumbLoadAddress(Arm7Tdmi cpu, ushort opcode) {
        var useStackPointer = (opcode & (1u << 11)) != 0u;
        var rd              = (int)((opcode >> 8) & 0x7u);
        var offset          = (uint)(opcode & 0xFFu) * 4u;

        cpu.m_gpr[rd] = useStackPointer
            ? (cpu.m_gpr[13] + offset)
            : ((cpu.m_gpr[15] & ~2u) + offset);
    }

    private static void ThumbAddOffsetToStackPointer(Arm7Tdmi cpu, ushort opcode) {
        var offset = (uint)(opcode & 0x7Fu) * 4u;

        cpu.m_gpr[13] = ((opcode & (1u << 7)) != 0u)
            ? (cpu.m_gpr[13] - offset)
            : (cpu.m_gpr[13] + offset);
    }

    private static void ThumbPushPop(Arm7Tdmi cpu, ushort opcode) {
        var load       = (opcode & (1u << 11)) != 0u;
        var includePcLr = (opcode & (1u << 8)) != 0u;
        var list       = (uint)(opcode & 0xFFu);
        var count      = BitOperations.PopCount(list) + (includePcLr ? 1 : 0);

        if (load) {
            var address = cpu.m_gpr[13];
            var access  = BusAccessType.NonSequential;

            for (var register = 0; register < 8; ++register) {
                if (((list >> register) & 1u) != 0u) {
                    cpu.m_gpr[register] = cpu.m_bus.Read32(address: address, access: access);
                    address += 4u;
                    access = BusAccessType.Sequential;
                }
            }

            var branchTarget = 0u;

            if (includePcLr) {
                branchTarget = cpu.m_bus.Read32(address: address, access: access);
                address += 4u;
            }

            cpu.m_gpr[13] = address;
            cpu.m_bus.Idle(cycles: 1);

            if (includePcLr) {
                cpu.BranchTo(address: branchTarget & ~1u);
            }
        }
        else {
            var address = cpu.m_gpr[13] - ((uint)count * 4u);

            cpu.m_gpr[13] = address;

            var access = BusAccessType.NonSequential;

            for (var register = 0; register < 8; ++register) {
                if (((list >> register) & 1u) != 0u) {
                    cpu.m_bus.Write32(address: address, value: cpu.m_gpr[register], access: access);
                    address += 4u;
                    access = BusAccessType.Sequential;
                }
            }

            if (includePcLr) {
                cpu.m_bus.Write32(address: address, value: cpu.m_gpr[14], access: access);
            }
        }

        cpu.m_nextFetchNonSequential = true;
    }

    private static void ThumbMultipleLoadStore(Arm7Tdmi cpu, ushort opcode) {
        var load         = (opcode & (1u << 11)) != 0u;
        var baseRegister = (int)((opcode >> 8) & 0x7u);
        var list         = (uint)(opcode & 0xFFu);

        if (list == 0u) {
            var emptyAddress = cpu.m_gpr[baseRegister];

            if (load) {
                var data = cpu.m_bus.Read32(address: emptyAddress, access: BusAccessType.NonSequential);

                cpu.m_gpr[baseRegister] = emptyAddress + 0x40u;

                cpu.BranchTo(address: data & ~1u);
            }
            else {
                cpu.m_bus.Write32(address: emptyAddress, value: cpu.m_gpr[15] + 2u, access: BusAccessType.NonSequential);

                cpu.m_gpr[baseRegister] = emptyAddress + 0x40u;
            }

            cpu.m_nextFetchNonSequential = true;

            return;
        }

        var address       = cpu.m_gpr[baseRegister];
        var firstRegister = BitOperations.TrailingZeroCount(list);
        var finalBase     = address + ((uint)BitOperations.PopCount(list) * 4u);
        var access        = BusAccessType.NonSequential;

        for (var register = 0; register < 8; ++register) {
            if (((list >> register) & 1u) == 0u) {
                continue;
            }

            if (load) {
                cpu.m_gpr[register] = cpu.m_bus.Read32(address: address, access: access);
            }
            else {
                var data = ((register == baseRegister) && (register != firstRegister))
                    ? finalBase
                    : cpu.m_gpr[register];

                cpu.m_bus.Write32(address: address, value: data, access: access);
            }

            address += 4u;
            access = BusAccessType.Sequential;
        }

        if (load) {
            cpu.m_bus.Idle(cycles: 1);
        }

        if (!(load && (((list >> baseRegister) & 1u) != 0u))) {
            cpu.m_gpr[baseRegister] = finalBase;
        }

        cpu.m_nextFetchNonSequential = true;
    }

    private static void ThumbConditionalBranch(Arm7Tdmi cpu, ushort opcode) {
        if (!CheckCondition(cpu: cpu, condition: ((uint)opcode >> 8) & 0xFu)) {
            return;
        }

        var offset = ((int)(sbyte)(opcode & 0xFFu)) << 1;

        cpu.BranchTo(address: cpu.m_gpr[15] + (uint)offset);
    }

    private static void ThumbUnconditionalBranch(Arm7Tdmi cpu, ushort opcode) {
        var offset = (((opcode & 0x7FF) << 21) >> 21) << 1;

        cpu.BranchTo(address: cpu.m_gpr[15] + (uint)offset);
    }

    private static void ThumbLongBranchWithLink(Arm7Tdmi cpu, ushort opcode) {
        if ((opcode & (1u << 11)) == 0u) {
            var high = (((opcode & 0x7FF) << 21) >> 21) << 12;

            cpu.m_gpr[14] = cpu.m_gpr[15] + (uint)high;
        }
        else {
            var target        = cpu.m_gpr[14] + ((uint)(opcode & 0x7FFu) << 1);
            var returnAddress = (cpu.m_gpr[15] - 2u) | 1u;

            cpu.m_gpr[14] = returnAddress;

            cpu.BranchTo(address: target & ~1u);
        }
    }

    private static void ThumbSoftwareInterrupt(Arm7Tdmi cpu, ushort opcode) {
        cpu.SoftwareInterrupt();
    }

    private static void ThumbUndefined(Arm7Tdmi cpu, ushort opcode) {
        cpu.UndefinedInstruction();
    }
}
