using System.Numerics;

namespace Puck.GameBoyAdvance;

// The 16-bit Thumb execution unit. Thumb is a re-encoding of a subset of ARM into 19 instruction formats; each
// handler below maps to one format and reuses the shared ALU/shifter/multiply helpers so flag behaviour stays
// identical to the ARM unit. In Thumb state R15 reads as the executing instruction's address + 4.
public sealed partial class Arm7Tdmi {
    private void ExecuteThumb(ushort opcode) {
        switch ((uint)opcode >> 13) {
            case 0b000u:
                if ((opcode & 0x1800u) == 0x1800u) {
                    ThumbAddSubtract(opcode: opcode);
                }
                else {
                    ThumbMoveShifted(opcode: opcode);
                }

                break;
            case 0b001u:
                ThumbMoveCompareAddSubImmediate(opcode: opcode);

                break;
            case 0b010u:
                if ((opcode & 0x1000u) == 0u) {
                    if ((opcode & 0x0800u) == 0u) {
                        if ((opcode & 0x0400u) == 0u) {
                            ThumbAluOperations(opcode: opcode);
                        }
                        else {
                            ThumbHiRegisterOperations(opcode: opcode);
                        }
                    }
                    else {
                        ThumbPcRelativeLoad(opcode: opcode);
                    }
                }
                else if ((opcode & 0x0200u) == 0u) {
                    ThumbLoadStoreRegisterOffset(opcode: opcode);
                }
                else {
                    ThumbLoadStoreSignExtended(opcode: opcode);
                }

                break;
            case 0b011u:
                ThumbLoadStoreImmediateOffset(opcode: opcode);

                break;
            case 0b100u:
                if ((opcode & 0x1000u) == 0u) {
                    ThumbLoadStoreHalfword(opcode: opcode);
                }
                else {
                    ThumbStackPointerRelativeLoadStore(opcode: opcode);
                }

                break;
            case 0b101u:
                if ((opcode & 0x1000u) == 0u) {
                    ThumbLoadAddress(opcode: opcode);
                }
                else if ((opcode & 0x0F00u) == 0x0000u) {
                    ThumbAddOffsetToStackPointer(opcode: opcode);
                }
                else if ((opcode & 0x0600u) == 0x0400u) {
                    ThumbPushPop(opcode: opcode);
                }
                else {
                    UndefinedInstruction();
                }

                break;
            case 0b110u:
                if ((opcode & 0x1000u) == 0u) {
                    ThumbMultipleLoadStore(opcode: opcode);
                }
                else if ((opcode & 0x0F00u) == 0x0F00u) {
                    SoftwareInterrupt();
                }
                else {
                    ThumbConditionalBranch(opcode: opcode);
                }

                break;
            default: // 0b111
                if ((opcode & 0x1000u) == 0u) {
                    ThumbUnconditionalBranch(opcode: opcode);
                }
                else {
                    ThumbLongBranchWithLink(opcode: opcode);
                }

                break;
        }
    }

    private void ThumbMoveShifted(ushort opcode) {
        var type = (ShiftType)((opcode >> 11) & 0x3u);
        var amount = (int)((opcode >> 6) & 0x1Fu);
        var rs = (int)((opcode >> 3) & 0x7u);
        var rd = (int)(opcode & 0x7u);
        var carry = (m_cpsr & FlagC) != 0u;

        var result = BarrelShift(value: m_gpr[rs], type: type, amount: amount, byRegister: false, carryOut: ref carry);

        m_gpr[rd] = result;

        SetNZ(result: result);
        SetCarry(carry: carry);
    }

    private void ThumbAddSubtract(ushort opcode) {
        var immediate = (opcode & (1u << 10)) != 0u;
        var subtract = (opcode & (1u << 9)) != 0u;
        var operand = immediate
            ? (((uint)opcode >> 6) & 0x7u)
            : m_gpr[(int)((opcode >> 6) & 0x7u)];
        var rs = (int)((opcode >> 3) & 0x7u);
        var rd = (int)(opcode & 0x7u);

        m_gpr[rd] = subtract
            ? Subtract(a: m_gpr[rs], b: operand, setFlags: true)
            : Add(a: m_gpr[rs], b: operand, setFlags: true);
    }

    private void ThumbMoveCompareAddSubImmediate(ushort opcode) {
        var operation = (opcode >> 11) & 0x3u;
        var rd = (int)((opcode >> 8) & 0x7u);
        var immediate = opcode & 0xFFu;

        switch (operation) {
            case 0u: // MOV
                m_gpr[rd] = immediate;

                SetNZ(result: immediate);

                break;
            case 1u: // CMP
                _ = Subtract(a: m_gpr[rd], b: immediate, setFlags: true);

                break;
            case 2u: // ADD
                m_gpr[rd] = Add(a: m_gpr[rd], b: immediate, setFlags: true);

                break;
            default: // SUB
                m_gpr[rd] = Subtract(a: m_gpr[rd], b: immediate, setFlags: true);

                break;
        }
    }

    private void ThumbAluOperations(ushort opcode) {
        var operation = (opcode >> 6) & 0xFu;
        var rs = (int)((opcode >> 3) & 0x7u);
        var rd = (int)(opcode & 0x7u);
        var source = m_gpr[rs];
        var destination = m_gpr[rd];

        switch (operation) {
            case 0x0u: // AND
                m_gpr[rd] = destination & source;

                SetNZ(result: m_gpr[rd]);

                break;
            case 0x1u: // EOR
                m_gpr[rd] = destination ^ source;

                SetNZ(result: m_gpr[rd]);

                break;
            case 0x2u: // LSL
                ThumbShiftByRegister(rd: rd, value: destination, type: ShiftType.LogicalLeft, amount: source);

                break;
            case 0x3u: // LSR
                ThumbShiftByRegister(rd: rd, value: destination, type: ShiftType.LogicalRight, amount: source);

                break;
            case 0x4u: // ASR
                ThumbShiftByRegister(rd: rd, value: destination, type: ShiftType.ArithmeticRight, amount: source);

                break;
            case 0x5u: // ADC
                m_gpr[rd] = AddWithCarry(a: destination, b: source, setFlags: true);

                break;
            case 0x6u: // SBC
                m_gpr[rd] = SubtractWithCarry(a: destination, b: source, setFlags: true);

                break;
            case 0x7u: // ROR
                ThumbShiftByRegister(rd: rd, value: destination, type: ShiftType.RotateRight, amount: source);

                break;
            case 0x8u: // TST
                SetNZ(result: destination & source);

                break;
            case 0x9u: // NEG
                m_gpr[rd] = Subtract(a: 0u, b: source, setFlags: true);

                break;
            case 0xAu: // CMP
                _ = Subtract(a: destination, b: source, setFlags: true);

                break;
            case 0xBu: // CMN
                _ = Add(a: destination, b: source, setFlags: true);

                break;
            case 0xCu: // ORR
                m_gpr[rd] = destination | source;

                SetNZ(result: m_gpr[rd]);

                break;
            case 0xDu: // MUL
                m_gpr[rd] = destination * source;

                SetNZ(result: m_gpr[rd]);
                m_bus.Idle(cycles: MultiplyCycles(multiplier: destination));

                break;
            case 0xEu: // BIC
                m_gpr[rd] = destination & ~source;

                SetNZ(result: m_gpr[rd]);

                break;
            default: // MVN
                m_gpr[rd] = ~source;

                SetNZ(result: m_gpr[rd]);

                break;
        }
    }

    private void ThumbShiftByRegister(int rd, uint value, ShiftType type, uint amount) {
        var carry = (m_cpsr & FlagC) != 0u;
        var result = BarrelShift(value: value, type: type, amount: (int)(amount & 0xFFu), byRegister: true, carryOut: ref carry);

        m_gpr[rd] = result;

        SetNZ(result: result);
        SetCarry(carry: carry);
        m_bus.Idle(cycles: 1);
    }

    private void ThumbHiRegisterOperations(ushort opcode) {
        var operation = (opcode >> 8) & 0x3u;
        var rs = (int)(((opcode >> 3) & 0x7u) | ((opcode >> 3) & 0x8u));
        var rd = (int)((opcode & 0x7u) | ((opcode >> 4) & 0x8u));

        switch (operation) {
            case 0u: // ADD (no flags)
                {
                    var result = m_gpr[rd] + m_gpr[rs];

                    if (rd == 15) {
                        BranchTo(address: result & ~1u);
                    }
                    else {
                        m_gpr[rd] = result;
                    }
                }

                break;
            case 1u: // CMP (flags only)
                _ = Subtract(a: m_gpr[rd], b: m_gpr[rs], setFlags: true);

                break;
            case 2u: // MOV (no flags)
                if (rd == 15) {
                    BranchTo(address: m_gpr[rs] & ~1u);
                }
                else {
                    m_gpr[rd] = m_gpr[rs];
                }

                break;
            default: // BX
                {
                    var target = m_gpr[rs];

                    if ((target & 1u) != 0u) {
                        m_cpsr |= FlagT;

                        BranchTo(address: target & ~1u);
                    }
                    else {
                        m_cpsr &= ~FlagT;

                        BranchTo(address: target & ~3u);
                    }
                }

                break;
        }
    }

    private void ThumbPcRelativeLoad(ushort opcode) {
        var rd = (int)((opcode >> 8) & 0x7u);
        var offset = (opcode & 0xFFu) * 4u;
        var address = (m_gpr[15] & ~2u) + offset;

        m_gpr[rd] = m_bus.Read32(address: address, access: BusAccessType.NonSequential);

        m_bus.Idle(cycles: 1);

        m_nextFetchNonSequential = true;
    }

    private void ThumbLoadStoreRegisterOffset(ushort opcode) {
        var load = (opcode & (1u << 11)) != 0u;
        var byteAccess = (opcode & (1u << 10)) != 0u;
        var offsetRegister = (int)((opcode >> 6) & 0x7u);
        var baseRegister = (int)((opcode >> 3) & 0x7u);
        var rd = (int)(opcode & 0x7u);
        var address = m_gpr[baseRegister] + m_gpr[offsetRegister];

        if (load) {
            m_gpr[rd] = byteAccess
                ? m_bus.Read8(address: address, access: BusAccessType.NonSequential)
                : ReadWordRotated(address: address);

            m_bus.Idle(cycles: 1);
        }
        else if (byteAccess) {
            m_bus.Write8(address: address, value: (byte)m_gpr[rd], access: BusAccessType.NonSequential);
        }
        else {
            m_bus.Write32(address: address & ~3u, value: m_gpr[rd], access: BusAccessType.NonSequential);
        }

        m_nextFetchNonSequential = true;
    }

    private void ThumbLoadStoreSignExtended(ushort opcode) {
        // operation = (S << 1) | H, where S is bit 10 and H is bit 11: 0 STRH, 1 LDRH, 2 LDRSB, 3 LDRSH.
        var operation = ((opcode >> 9) & 0x2u) | ((opcode >> 11) & 0x1u);
        var offsetRegister = (int)((opcode >> 6) & 0x7u);
        var baseRegister = (int)((opcode >> 3) & 0x7u);
        var rd = (int)(opcode & 0x7u);
        var address = m_gpr[baseRegister] + m_gpr[offsetRegister];

        switch (operation) {
            case 0u: // STRH
                m_bus.Write16(address: address & ~1u, value: (ushort)m_gpr[rd], access: BusAccessType.NonSequential);

                break;
            case 1u: // LDRH
                {
                    uint data = m_bus.Read16(address: address & ~1u, access: BusAccessType.NonSequential);

                    if ((address & 1u) != 0u) {
                        data = (data >> 8) | (data << 24);
                    }

                    m_gpr[rd] = data;
                    m_bus.Idle(cycles: 1);
                }

                break;
            case 2u: // LDRSB
                m_gpr[rd] = (uint)(sbyte)m_bus.Read8(address: address, access: BusAccessType.NonSequential);
                m_bus.Idle(cycles: 1);

                break;
            default: // LDRSH (odd address degrades to a signed byte)
                if ((address & 1u) != 0u) {
                    m_gpr[rd] = (uint)(sbyte)m_bus.Read8(address: address, access: BusAccessType.NonSequential);
                }
                else {
                    m_gpr[rd] = (uint)(short)m_bus.Read16(address: address, access: BusAccessType.NonSequential);
                }

                m_bus.Idle(cycles: 1);

                break;
        }

        m_nextFetchNonSequential = true;
    }

    private void ThumbLoadStoreImmediateOffset(ushort opcode) {
        var byteAccess = (opcode & (1u << 12)) != 0u;
        var load = (opcode & (1u << 11)) != 0u;
        var offset = ((uint)opcode >> 6) & 0x1Fu;
        var baseRegister = (int)((opcode >> 3) & 0x7u);
        var rd = (int)(opcode & 0x7u);
        var address = byteAccess
            ? (m_gpr[baseRegister] + offset)
            : (m_gpr[baseRegister] + (offset * 4u));

        if (load) {
            m_gpr[rd] = byteAccess
                ? m_bus.Read8(address: address, access: BusAccessType.NonSequential)
                : ReadWordRotated(address: address);

            m_bus.Idle(cycles: 1);
        }
        else if (byteAccess) {
            m_bus.Write8(address: address, value: (byte)m_gpr[rd], access: BusAccessType.NonSequential);
        }
        else {
            m_bus.Write32(address: address & ~3u, value: m_gpr[rd], access: BusAccessType.NonSequential);
        }

        m_nextFetchNonSequential = true;
    }

    private void ThumbLoadStoreHalfword(ushort opcode) {
        var load = (opcode & (1u << 11)) != 0u;
        var offset = (((uint)opcode >> 6) & 0x1Fu) * 2u;
        var baseRegister = (int)((opcode >> 3) & 0x7u);
        var rd = (int)(opcode & 0x7u);
        var address = m_gpr[baseRegister] + offset;

        if (load) {
            uint data = m_bus.Read16(address: address & ~1u, access: BusAccessType.NonSequential);

            if ((address & 1u) != 0u) {
                data = (data >> 8) | (data << 24);
            }

            m_gpr[rd] = data;
            m_bus.Idle(cycles: 1);
        }
        else {
            m_bus.Write16(address: address & ~1u, value: (ushort)m_gpr[rd], access: BusAccessType.NonSequential);
        }

        m_nextFetchNonSequential = true;
    }

    private void ThumbStackPointerRelativeLoadStore(ushort opcode) {
        var load = (opcode & (1u << 11)) != 0u;
        var rd = (int)((opcode >> 8) & 0x7u);
        var address = m_gpr[13] + ((opcode & 0xFFu) * 4u);

        if (load) {
            m_gpr[rd] = ReadWordRotated(address: address);

            m_bus.Idle(cycles: 1);
        }
        else {
            m_bus.Write32(address: address & ~3u, value: m_gpr[rd], access: BusAccessType.NonSequential);
        }

        m_nextFetchNonSequential = true;
    }

    private void ThumbLoadAddress(ushort opcode) {
        var useStackPointer = (opcode & (1u << 11)) != 0u;
        var rd = (int)((opcode >> 8) & 0x7u);
        var offset = (opcode & 0xFFu) * 4u;

        m_gpr[rd] = useStackPointer
            ? (m_gpr[13] + offset)
            : ((m_gpr[15] & ~2u) + offset);
    }

    private void ThumbAddOffsetToStackPointer(ushort opcode) {
        var offset = (opcode & 0x7Fu) * 4u;

        m_gpr[13] = ((opcode & (1u << 7)) != 0u)
            ? (m_gpr[13] - offset)
            : (m_gpr[13] + offset);
    }

    private void ThumbPushPop(ushort opcode) {
        var load = (opcode & (1u << 11)) != 0u;
        var includePcLr = (opcode & (1u << 8)) != 0u;
        var list = opcode & 0xFFu;
        var count = BitOperations.PopCount(list) + (includePcLr ? 1 : 0);

        if (load) {
            // POP: load R0–R7 (then PC if set) from ascending addresses, then move SP up.
            var address = m_gpr[13];
            var access = BusAccessType.NonSequential;

            for (var register = 0; register < 8; ++register) {
                if (((list >> register) & 1u) != 0u) {
                    m_gpr[register] = m_bus.Read32(address: address, access: access);
                    address += 4u;
                    access = BusAccessType.Sequential;
                }
            }

            var branchTarget = 0u;

            if (includePcLr) {
                branchTarget = m_bus.Read32(address: address, access: access);
                address += 4u;
            }

            m_gpr[13] = address;
            m_bus.Idle(cycles: 1);

            if (includePcLr) {
                BranchTo(address: branchTarget & ~1u);
            }
        }
        else {
            // PUSH: full-descending; pre-decrement SP, then store LR (if set) then R7–R0 at ascending addresses.
            var address = m_gpr[13] - ((uint)count * 4u);

            m_gpr[13] = address;

            var access = BusAccessType.NonSequential;

            for (var register = 0; register < 8; ++register) {
                if (((list >> register) & 1u) != 0u) {
                    m_bus.Write32(address: address, value: m_gpr[register], access: access);
                    address += 4u;
                    access = BusAccessType.Sequential;
                }
            }

            if (includePcLr) {
                m_bus.Write32(address: address, value: m_gpr[14], access: access);
            }
        }

        m_nextFetchNonSequential = true;
    }

    private void ThumbMultipleLoadStore(ushort opcode) {
        var load = (opcode & (1u << 11)) != 0u;
        var baseRegister = (int)((opcode >> 8) & 0x7u);
        var list = opcode & 0xFFu;

        // Empty-register-list quirk: only R15 transfers and the base advances by the full 0x40.
        if (list == 0u) {
            var emptyAddress = m_gpr[baseRegister];

            if (load) {
                var data = m_bus.Read32(address: emptyAddress, access: BusAccessType.NonSequential);

                m_gpr[baseRegister] = emptyAddress + 0x40u;

                BranchTo(address: data & ~1u);
            }
            else {
                m_bus.Write32(address: emptyAddress, value: m_gpr[15] + 2u, access: BusAccessType.NonSequential);

                m_gpr[baseRegister] = emptyAddress + 0x40u;
            }

            m_nextFetchNonSequential = true;

            return;
        }

        var address = m_gpr[baseRegister];
        var firstRegister = BitOperations.TrailingZeroCount(list);
        var finalBase = address + ((uint)BitOperations.PopCount(list) * 4u);
        var access = BusAccessType.NonSequential;

        for (var register = 0; register < 8; ++register) {
            if (((list >> register) & 1u) == 0u) {
                continue;
            }

            if (load) {
                m_gpr[register] = m_bus.Read32(address: address, access: access);
            }
            else {
                // Storing the base writes the original value only when it is first in the list.
                var data = ((register == baseRegister) && (register != firstRegister))
                    ? finalBase
                    : m_gpr[register];

                m_bus.Write32(address: address, value: data, access: access);
            }

            address += 4u;
            access = BusAccessType.Sequential;
        }

        if (load) {
            m_bus.Idle(cycles: 1);
        }

        // A load into the base register keeps the loaded value rather than writing back.
        if (!(load && (((list >> baseRegister) & 1u) != 0u))) {
            m_gpr[baseRegister] = finalBase;
        }

        m_nextFetchNonSequential = true;
    }

    private void ThumbConditionalBranch(ushort opcode) {
        if (!CheckCondition(condition: ((uint)opcode >> 8) & 0xFu)) {
            return;
        }

        var offset = ((int)(sbyte)(opcode & 0xFFu)) << 1;

        BranchTo(address: m_gpr[15] + (uint)offset);
    }

    private void ThumbUnconditionalBranch(ushort opcode) {
        var offset = (((opcode & 0x7FF) << 21) >> 21) << 1;

        BranchTo(address: m_gpr[15] + (uint)offset);
    }

    private void ThumbLongBranchWithLink(ushort opcode) {
        if ((opcode & (1u << 11)) == 0u) {
            // First half: LR = PC + (sign-extended high offset << 12).
            var high = (((opcode & 0x7FF) << 21) >> 21) << 12;

            m_gpr[14] = m_gpr[15] + (uint)high;
        }
        else {
            // Second half: branch to LR + (low offset << 1); LR = address of the next instruction, with bit 0 set.
            var target = m_gpr[14] + ((uint)(opcode & 0x7FFu) << 1);
            var returnAddress = (m_gpr[15] - 2u) | 1u;

            m_gpr[14] = returnAddress;

            BranchTo(address: target & ~1u);
        }
    }
}
