using System.Numerics;

namespace Puck.GameBoyAdvance;

// The 32-bit ARM execution unit: decode of the ARMv4T encoding space and one handler per instruction class.
// Decode order matters — the classes overlap in their high bits, so the more specific patterns (BX, the
// multiply/swap/halfword "extension space", PSR transfer) are tested before the general data-processing and
// load/store forms they sit inside.
public sealed partial class Arm7Tdmi {
    private void ExecuteArm(uint opcode) {
        var condition = opcode >> 28;

        // Every ARM instruction is conditional; a failed condition still cost the fetch Step() already charged.
        if ((condition != 0xEu) && !CheckCondition(condition: condition)) {
            return;
        }

        // Branch and Exchange must be matched before the data-processing space it nests in.
        if ((opcode & 0x0FFFFFF0u) == 0x012FFF10u) {
            BranchExchange(opcode: opcode);

            return;
        }

        switch ((opcode >> 25) & 0x7u) {
            case 0b101u:
                Branch(opcode: opcode);

                return;
            case 0b100u:
                BlockDataTransfer(opcode: opcode);

                return;
            case 0b111u:
                if ((opcode & 0x0F000000u) == 0x0F000000u) {
                    SoftwareInterrupt();
                }
                else {
                    // Coprocessor register transfers: no coprocessor exists on the GBA.
                    UndefinedInstruction();
                }

                return;
            case 0b110u:
                // Coprocessor data transfers: likewise undefined on the GBA.
                UndefinedInstruction();

                return;
            case 0b011u:
                if ((opcode & 0x10u) != 0u) {
                    UndefinedInstruction();
                }
                else {
                    SingleDataTransfer(opcode: opcode);
                }

                return;
            case 0b010u:
                SingleDataTransfer(opcode: opcode);

                return;
            default:
                DecodeDataProcessingSpace(opcode: opcode);

                return;
        }
    }

    private void DecodeDataProcessingSpace(uint opcode) {
        // Groups 000 and 001. The 000 group additionally holds the multiply/swap/halfword extension space,
        // distinguished by bits 7 and 4 both being set.
        if ((((opcode >> 25) & 0x7u) == 0b000u) && ((opcode & 0x90u) == 0x90u)) {
            if ((opcode & 0x60u) == 0u) {
                // bits[6:5] == 00 → multiply, multiply-long, or single data swap.
                if ((opcode & 0x0FC000F0u) == 0x00000090u) {
                    Multiply(opcode: opcode);
                }
                else if ((opcode & 0x0F8000F0u) == 0x00800090u) {
                    MultiplyLong(opcode: opcode);
                }
                else if ((opcode & 0x0FB00FF0u) == 0x01000090u) {
                    Swap(opcode: opcode);
                }
                else {
                    UndefinedInstruction();
                }

                return;
            }

            HalfwordTransfer(opcode: opcode);

            return;
        }

        var dataOpcode = (opcode >> 21) & 0xFu;
        var setFlags = (opcode & (1u << 20)) != 0u;

        // The TST/TEQ/CMP/CMN opcodes with S clear are not data processing — they encode PSR transfers.
        if (!setFlags && (dataOpcode >= 0x8u) && (dataOpcode <= 0xBu)) {
            if ((opcode & 0x0FBF0FFFu) == 0x010F0000u) {
                MoveStatusToRegister(opcode: opcode);
            }
            else if (((opcode & 0x0FB0FFF0u) == 0x0120F000u) || ((opcode & 0x0FB0F000u) == 0x0320F000u)) {
                MoveRegisterToStatus(opcode: opcode);
            }
            else {
                UndefinedInstruction();
            }

            return;
        }

        DataProcessing(opcode: opcode);
    }

    private void BranchExchange(uint opcode) {
        var target = m_gpr[(int)(opcode & 0xFu)];

        if ((target & 1u) != 0u) {
            m_cpsr |= FlagT;

            BranchTo(address: target & ~1u);
        }
        else {
            m_cpsr &= ~FlagT;

            BranchTo(address: target & ~3u);
        }
    }

    private void Branch(uint opcode) {
        // 24-bit signed offset, shifted left by two; PC already reads as this instruction + 8.
        var offset = ((int)(opcode << 8)) >> 6;

        if ((opcode & (1u << 24)) != 0u) {
            m_gpr[14] = m_gpr[15] - 4u;
        }

        BranchTo(address: m_gpr[15] + (uint)offset);
    }

    private void DataProcessing(uint opcode) {
        var immediate = (opcode & (1u << 25)) != 0u;
        var operation = (opcode >> 21) & 0xFu;
        var setFlags = (opcode & (1u << 20)) != 0u;
        var rn = (int)((opcode >> 16) & 0xFu);
        var rd = (int)((opcode >> 12) & 0xFu);
        var shiftByRegister = !immediate && ((opcode & (1u << 4)) != 0u);

        var shifterCarry = (m_cpsr & FlagC) != 0u;

        // When a register-specified shift is present the core spends an extra internal cycle and R15 reads as
        // the instruction address + 12 rather than + 8.
        var operandN = m_gpr[rn];

        if (shiftByRegister && (rn == 15)) {
            operandN += 4u;
        }

        uint operand2;

        if (immediate) {
            var value = opcode & 0xFFu;
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
            var rm = (int)(opcode & 0xFu);
            var operandM = m_gpr[rm];

            if (shiftByRegister && (rm == 15)) {
                operandM += 4u;
            }

            var shiftType = (ShiftType)((opcode >> 5) & 0x3u);
            int amount;

            if (shiftByRegister) {
                amount = (int)(m_gpr[(int)((opcode >> 8) & 0xFu)] & 0xFFu);
            }
            else {
                amount = (int)((opcode >> 7) & 0x1Fu);
            }

            operand2 = BarrelShift(value: operandM, type: shiftType, amount: amount, byRegister: shiftByRegister, carryOut: ref shifterCarry);
        }

        if (shiftByRegister) {
            m_bus.Idle(cycles: 1);
        }

        var result = 0u;
        var write = true;

        switch (operation) {
            case 0x0u: // AND
                result = operandN & operand2;
                ApplyLogicalFlags(setFlags: setFlags, result: result, shifterCarry: shifterCarry);

                break;
            case 0x1u: // EOR
                result = operandN ^ operand2;
                ApplyLogicalFlags(setFlags: setFlags, result: result, shifterCarry: shifterCarry);

                break;
            case 0x2u: // SUB
                result = Subtract(a: operandN, b: operand2, setFlags: setFlags);

                break;
            case 0x3u: // RSB
                result = Subtract(a: operand2, b: operandN, setFlags: setFlags);

                break;
            case 0x4u: // ADD
                result = Add(a: operandN, b: operand2, setFlags: setFlags);

                break;
            case 0x5u: // ADC
                result = AddWithCarry(a: operandN, b: operand2, setFlags: setFlags);

                break;
            case 0x6u: // SBC
                result = SubtractWithCarry(a: operandN, b: operand2, setFlags: setFlags);

                break;
            case 0x7u: // RSC
                result = SubtractWithCarry(a: operand2, b: operandN, setFlags: setFlags);

                break;
            case 0x8u: // TST
                ApplyLogicalFlags(setFlags: true, result: operandN & operand2, shifterCarry: shifterCarry);
                write = false;

                break;
            case 0x9u: // TEQ
                ApplyLogicalFlags(setFlags: true, result: operandN ^ operand2, shifterCarry: shifterCarry);
                write = false;

                break;
            case 0xAu: // CMP
                _ = Subtract(a: operandN, b: operand2, setFlags: true);
                write = false;

                break;
            case 0xBu: // CMN
                _ = Add(a: operandN, b: operand2, setFlags: true);
                write = false;

                break;
            case 0xCu: // ORR
                result = operandN | operand2;
                ApplyLogicalFlags(setFlags: setFlags, result: result, shifterCarry: shifterCarry);

                break;
            case 0xDu: // MOV
                result = operand2;
                ApplyLogicalFlags(setFlags: setFlags, result: result, shifterCarry: shifterCarry);

                break;
            case 0xEu: // BIC
                result = operandN & ~operand2;
                ApplyLogicalFlags(setFlags: setFlags, result: result, shifterCarry: shifterCarry);

                break;
            default: // 0xF MVN
                result = ~operand2;
                ApplyLogicalFlags(setFlags: setFlags, result: result, shifterCarry: shifterCarry);

                break;
        }

        if (rd == 15) {
            // S + Rd=15 copies SPSR back to CPSR — the exception-return idiom, and the deprecated TSTP/CMPP/
            // TEQP/CMNP forms where a compare with Rd=15 restores the PSR. Ops that produce a result also branch.
            if (setFlags) {
                WriteCpsr(value: Spsr);
            }

            if (write) {
                BranchTo(address: result & (ThumbState ? ~1u : ~3u));
            }

            return;
        }

        if (write) {
            m_gpr[rd] = result;
        }
    }

    private void ApplyLogicalFlags(bool setFlags, uint result, bool shifterCarry) {
        if (setFlags) {
            SetNZ(result: result);
            SetCarry(carry: shifterCarry);
        }
    }

    private void MoveStatusToRegister(uint opcode) {
        var useSpsr = (opcode & (1u << 22)) != 0u;
        var rd = (int)((opcode >> 12) & 0xFu);

        m_gpr[rd] = useSpsr
            ? Spsr
            : m_cpsr;
    }

    private void MoveRegisterToStatus(uint opcode) {
        var useSpsr = (opcode & (1u << 22)) != 0u;
        var immediate = (opcode & (1u << 25)) != 0u;

        uint value;

        if (immediate) {
            var imm = opcode & 0xFFu;
            var rotate = (int)((opcode >> 8) & 0xFu) * 2;

            value = (rotate == 0)
                ? imm
                : (imm >> rotate) | (imm << (32 - rotate));
        }
        else {
            value = m_gpr[(int)(opcode & 0xFu)];
        }

        var fields = (opcode >> 16) & 0xFu;
        var mask = 0u;

        if ((fields & 0x1u) != 0u) {
            mask |= 0x000000FFu;
        }

        if ((fields & 0x2u) != 0u) {
            mask |= 0x0000FF00u;
        }

        if ((fields & 0x4u) != 0u) {
            mask |= 0x00FF0000u;
        }

        if ((fields & 0x8u) != 0u) {
            mask |= 0xFF000000u;
        }

        // Only the bits the ARMv4T PSR actually defines are writable — NZCV (flags) and the mode/T/F/I control
        // byte. The reserved bits 8–27 always read as zero, so a selected field byte still cannot set them.
        mask &= 0xF00000FFu;

        if (useSpsr) {
            if (HasSpsr) {
                Spsr = (Spsr & ~mask) | (value & mask);
            }

            return;
        }

        // The control byte (mode, I, F, T) is writable only in a privileged mode; User mode may change flags only.
        if (CurrentMode == (uint)CpuMode.User) {
            mask &= 0xF0000000u;
        }

        WriteCpsr(value: (m_cpsr & ~mask) | (value & mask));
    }

    private void SingleDataTransfer(uint opcode) {
        var registerOffset = (opcode & (1u << 25)) != 0u;
        var preIndex = (opcode & (1u << 24)) != 0u;
        var add = (opcode & (1u << 23)) != 0u;
        var byteAccess = (opcode & (1u << 22)) != 0u;
        var writeBack = (opcode & (1u << 21)) != 0u;
        var load = (opcode & (1u << 20)) != 0u;
        var rn = (int)((opcode >> 16) & 0xFu);
        var rd = (int)((opcode >> 12) & 0xFu);

        uint offset;

        if (registerOffset) {
            var operandM = m_gpr[(int)(opcode & 0xFu)];
            var shiftType = (ShiftType)((opcode >> 5) & 0x3u);
            var amount = (int)((opcode >> 7) & 0x1Fu);
            var ignored = (m_cpsr & FlagC) != 0u;

            offset = BarrelShift(value: operandM, type: shiftType, amount: amount, byRegister: false, carryOut: ref ignored);
        }
        else {
            offset = opcode & 0xFFFu;
        }

        var baseAddress = m_gpr[rn];
        var address = baseAddress;

        if (preIndex) {
            address = add
                ? (baseAddress + offset)
                : (baseAddress - offset);
        }

        if (load) {
            var data = byteAccess
                ? m_bus.Read8(address: address, access: BusAccessType.NonSequential)
                : ReadWordRotated(address: address);

            m_bus.Idle(cycles: 1);

            WriteBackBase(rn: rn, preIndex: preIndex, writeBack: writeBack, baseAddress: baseAddress, indexedAddress: address, offset: offset, add: add);

            if (rd == 15) {
                BranchTo(address: data & ~3u);
            }
            else {
                m_gpr[rd] = data;
            }
        }
        else {
            // Storing R15 writes this instruction's address + 12.
            var data = (rd == 15)
                ? m_gpr[15] + 4u
                : m_gpr[rd];

            if (byteAccess) {
                m_bus.Write8(address: address, value: (byte)data, access: BusAccessType.NonSequential);
            }
            else {
                // Raw address: the bus aligns it for wide-bus regions, while the 8-bit save bus uses the low bits
                // to select which byte of the word it stores (and where) — STR to SRAM writes a single byte.
                m_bus.Write32(address: address, value: data, access: BusAccessType.NonSequential);
            }

            WriteBackBase(rn: rn, preIndex: preIndex, writeBack: writeBack, baseAddress: baseAddress, indexedAddress: address, offset: offset, add: add);
        }

        m_nextFetchNonSequential = true;
    }

    private void HalfwordTransfer(uint opcode) {
        var preIndex = (opcode & (1u << 24)) != 0u;
        var add = (opcode & (1u << 23)) != 0u;
        var immediate = (opcode & (1u << 22)) != 0u;
        var writeBack = (opcode & (1u << 21)) != 0u;
        var load = (opcode & (1u << 20)) != 0u;
        var rn = (int)((opcode >> 16) & 0xFu);
        var rd = (int)((opcode >> 12) & 0xFu);
        var kind = (opcode >> 5) & 0x3u;

        var offset = immediate
            ? (((opcode >> 4) & 0xF0u) | (opcode & 0xFu))
            : m_gpr[(int)(opcode & 0xFu)];

        var baseAddress = m_gpr[rn];
        var address = baseAddress;

        if (preIndex) {
            address = add
                ? (baseAddress + offset)
                : (baseAddress - offset);
        }

        if (load) {
            uint data;

            switch (kind) {
                case 1u: // LDRH (unsigned halfword)
                    data = m_bus.Read16(address: address & ~1u, access: BusAccessType.NonSequential);

                    if ((address & 1u) != 0u) {
                        data = (data >> 8) | (data << 24);
                    }

                    break;
                case 2u: // LDRSB (signed byte)
                    data = (uint)(sbyte)m_bus.Read8(address: address, access: BusAccessType.NonSequential);

                    break;
                default: // 3: LDRSH (signed halfword); an odd address degrades to a signed byte
                    if ((address & 1u) != 0u) {
                        data = (uint)(sbyte)m_bus.Read8(address: address, access: BusAccessType.NonSequential);
                    }
                    else {
                        data = (uint)(short)m_bus.Read16(address: address, access: BusAccessType.NonSequential);
                    }

                    break;
            }

            m_bus.Idle(cycles: 1);

            WriteBackBase(rn: rn, preIndex: preIndex, writeBack: writeBack, baseAddress: baseAddress, indexedAddress: address, offset: offset, add: add);

            if (rd == 15) {
                BranchTo(address: data & ~3u);
            }
            else {
                m_gpr[rd] = data;
            }
        }
        else {
            // STRH is the only valid store form in this space. Pass the raw address: the bus aligns it for the
            // wide-bus regions, but the 8-bit save bus needs the low bit to select which byte of the value lands.
            m_bus.Write16(address: address, value: (ushort)m_gpr[rd], access: BusAccessType.NonSequential);

            WriteBackBase(rn: rn, preIndex: preIndex, writeBack: writeBack, baseAddress: baseAddress, indexedAddress: address, offset: offset, add: add);
        }

        m_nextFetchNonSequential = true;
    }

    // Applies the base-register update shared by the load/store forms: post-indexed always writes back the
    // offset base; pre-indexed writes back only when the W bit is set. A load into the base register overrides
    // the write-back, so callers order the loaded-value write after this.
    private void WriteBackBase(int rn, bool preIndex, bool writeBack, uint baseAddress, uint indexedAddress, uint offset, bool add) {
        if (!preIndex) {
            m_gpr[rn] = add
                ? (baseAddress + offset)
                : (baseAddress - offset);
        }
        else if (writeBack) {
            m_gpr[rn] = indexedAddress;
        }
    }

    private uint ReadWordRotated(uint address) {
        var data = m_bus.Read32(address: address & ~3u, access: BusAccessType.NonSequential);
        var rotate = (int)((address & 3u) * 8u);

        return (rotate == 0)
            ? data
            : (data >> rotate) | (data << (32 - rotate));
    }

    private void BlockDataTransfer(uint opcode) {
        var preIndex = (opcode & (1u << 24)) != 0u;
        var add = (opcode & (1u << 23)) != 0u;
        var useUserBank = (opcode & (1u << 22)) != 0u;
        var writeBack = (opcode & (1u << 21)) != 0u;
        var load = (opcode & (1u << 20)) != 0u;
        var rn = (int)((opcode >> 16) & 0xFu);
        var list = opcode & 0xFFFFu;

        // Empty-register-list quirk (ARM7TDMI): only R15 transfers, but the base still moves by the full 0x40
        // (as if 16 registers were transferred). A store writes this instruction's address + 12.
        if (list == 0u) {
            var emptyBase = m_gpr[rn];
            var emptyAddress = add
                ? (preIndex ? (emptyBase + 4u) : emptyBase)
                : (preIndex ? (emptyBase - 0x40u) : (emptyBase - 0x40u + 4u));
            var emptyFinal = add
                ? (emptyBase + 0x40u)
                : (emptyBase - 0x40u);

            if (load) {
                var data = m_bus.Read32(address: emptyAddress, access: BusAccessType.NonSequential);

                if (writeBack) {
                    m_gpr[rn] = emptyFinal;
                }

                BranchTo(address: data & ~3u);
            }
            else {
                m_bus.Write32(address: emptyAddress, value: m_gpr[15] + 4u, access: BusAccessType.NonSequential);

                if (writeBack) {
                    m_gpr[rn] = emptyFinal;
                }
            }

            m_nextFetchNonSequential = true;

            return;
        }

        var count = BitOperations.PopCount(list);
        var baseAddress = m_gpr[rn];
        var total = (uint)count * 4u;

        // Registers always transfer lowest-numbered to lowest address regardless of direction.
        var address = add
            ? (preIndex ? (baseAddress + 4u) : baseAddress)
            : (preIndex ? (baseAddress - total) : (baseAddress - total + 4u));

        var finalBase = add
            ? (baseAddress + total)
            : (baseAddress - total);

        var restoreCpsr = load && useUserBank && ((list & 0x8000u) != 0u);
        var transferUserBank = useUserBank && !restoreCpsr;

        var savedMode = CurrentMode;
        var swappedBank = false;

        if (transferUserBank && (savedMode != (uint)CpuMode.User) && (savedMode != (uint)CpuMode.System)) {
            // Expose the User/System bank for the data transfer without disturbing the current mode's flags.
            SwitchMode(newMode: (uint)CpuMode.System);
            swappedBank = true;
        }

        var firstRegister = BitOperations.TrailingZeroCount(list);
        var access = BusAccessType.NonSequential;
        var branched = false;
        var loadedData = 0u;

        for (var register = 0; register < 16; ++register) {
            if (((list >> register) & 1u) == 0u) {
                continue;
            }

            if (load) {
                var data = m_bus.Read32(address: address, access: access);

                if (register == 15) {
                    branched = true;
                    loadedData = data;
                }
                else {
                    m_gpr[register] = data;
                }
            }
            else {
                var data = (register == 15)
                    ? m_gpr[15] + 4u
                    : m_gpr[register];

                // Storing the base register: the original value if it is first in the list, else the new base.
                if ((register == rn) && (register != firstRegister)) {
                    data = finalBase;
                }

                m_bus.Write32(address: address, value: data, access: access);
            }

            address += 4u;
            access = BusAccessType.Sequential;
        }

        if (load) {
            m_bus.Idle(cycles: 1);
        }

        if (swappedBank) {
            SwitchMode(newMode: savedMode);
        }

        // A load into the base register overrides write-back.
        if (writeBack && !(load && (((list >> rn) & 1u) != 0u))) {
            m_gpr[rn] = finalBase;
        }

        if (branched) {
            if (restoreCpsr) {
                WriteCpsr(value: Spsr);
            }

            BranchTo(address: loadedData & (ThumbState ? ~1u : ~3u));
        }

        m_nextFetchNonSequential = true;
    }

    private void Multiply(uint opcode) {
        var accumulate = (opcode & (1u << 21)) != 0u;
        var setFlags = (opcode & (1u << 20)) != 0u;
        var rd = (int)((opcode >> 16) & 0xFu);
        var rn = (int)((opcode >> 12) & 0xFu);
        var rs = (int)((opcode >> 8) & 0xFu);
        var rm = (int)(opcode & 0xFu);

        var result = m_gpr[rm] * m_gpr[rs];

        if (accumulate) {
            result += m_gpr[rn];
        }

        m_gpr[rd] = result;

        if (setFlags) {
            SetNZ(result: result);
        }

        m_bus.Idle(cycles: MultiplyCycles(multiplier: m_gpr[rs]) + (accumulate ? 1 : 0));
    }

    private void MultiplyLong(uint opcode) {
        var signed = (opcode & (1u << 22)) != 0u;
        var accumulate = (opcode & (1u << 21)) != 0u;
        var setFlags = (opcode & (1u << 20)) != 0u;
        var rdHigh = (int)((opcode >> 16) & 0xFu);
        var rdLow = (int)((opcode >> 12) & 0xFu);
        var rs = (int)((opcode >> 8) & 0xFu);
        var rm = (int)(opcode & 0xFu);

        ulong result;

        if (signed) {
            var product = (long)(int)m_gpr[rm] * (int)m_gpr[rs];

            if (accumulate) {
                product += (long)(((ulong)m_gpr[rdHigh] << 32) | m_gpr[rdLow]);
            }

            result = (ulong)product;
        }
        else {
            result = (ulong)m_gpr[rm] * m_gpr[rs];

            if (accumulate) {
                result += ((ulong)m_gpr[rdHigh] << 32) | m_gpr[rdLow];
            }
        }

        m_gpr[rdLow] = (uint)result;
        m_gpr[rdHigh] = (uint)(result >> 32);

        if (setFlags) {
            m_cpsr = (m_cpsr & ~(FlagN | FlagZ))
                | (((result & 0x8000000000000000ul) != 0ul) ? FlagN : 0u)
                | ((result == 0ul) ? FlagZ : 0u);
        }

        m_bus.Idle(cycles: MultiplyCycles(multiplier: m_gpr[rs]) + (accumulate ? 2 : 1));
    }

    private void Swap(uint opcode) {
        var byteAccess = (opcode & (1u << 22)) != 0u;
        var rn = (int)((opcode >> 16) & 0xFu);
        var rd = (int)((opcode >> 12) & 0xFu);
        var rm = (int)(opcode & 0xFu);
        var address = m_gpr[rn];

        if (byteAccess) {
            var old = m_bus.Read8(address: address, access: BusAccessType.NonSequential);

            m_bus.Write8(address: address, value: (byte)m_gpr[rm], access: BusAccessType.NonSequential);

            m_gpr[rd] = old;
        }
        else {
            var old = ReadWordRotated(address: address);

            m_bus.Write32(address: address & ~3u, value: m_gpr[rm], access: BusAccessType.NonSequential);

            m_gpr[rd] = old;
        }

        m_bus.Idle(cycles: 1);

        m_nextFetchNonSequential = true;
    }

    // The ARM7TDMI multiplier runs 1–4 internal cycles depending on how many high bytes of the multiplier are
    // all-zero or all-one (early termination).
    private static int MultiplyCycles(uint multiplier) {
        if (((multiplier & 0xFFFFFF00u) == 0u) || ((multiplier & 0xFFFFFF00u) == 0xFFFFFF00u)) {
            return 1;
        }

        if (((multiplier & 0xFFFF0000u) == 0u) || ((multiplier & 0xFFFF0000u) == 0xFFFF0000u)) {
            return 2;
        }

        if (((multiplier & 0xFF000000u) == 0u) || ((multiplier & 0xFF000000u) == 0xFF000000u)) {
            return 3;
        }

        return 4;
    }
}
