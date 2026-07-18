using System.Globalization;
using System.Text;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// A pure, side-effect-free ARM7TDMI disassembler — machine-neutral truth about the ARM/THUMB ISAs, covering the common
/// instruction classes a debugger meets in boot and game code (branches, data processing, PSR transfer, multiply,
/// single/halfword/block data transfer, SWI, and the main THUMB formats). It is the textual companion the AGB Post's
/// <c>--pctrace</c> never had (that mode prints raw PCs only), so there was no decoder to lift — it lives in the core as
/// ISA truth both the demo's <c>agb.dis</c> verb and any future Post tooling can consume. An encoding outside the
/// covered set renders as its assembler <c>DCD</c> word rather than a guess, so the output is never misleading.
/// </summary>
public static class ArmDisassembler {
    private static readonly string[] Conditions = ["EQ", "NE", "CS", "CC", "MI", "PL", "VS", "VC", "HI", "LS", "GE", "LT", "GT", "LE", "", "NV"];
    private static readonly string[] DataOps = ["AND", "EOR", "SUB", "RSB", "ADD", "ADC", "SBC", "RSC", "TST", "TEQ", "CMP", "CMN", "ORR", "MOV", "BIC", "MVN"];
    private static readonly string[] ShiftNames = ["LSL", "LSR", "ASR", "ROR"];

    /// <summary>Disassembles one ARM instruction word.</summary>
    /// <param name="address">The instruction's address (for branch-target resolution).</param>
    /// <param name="instruction">The 32-bit instruction word.</param>
    /// <returns>The assembly text.</returns>
    public static string DecodeArm(uint address, uint instruction) {
        var condition = Conditions[instruction >> 28];

        // BX Rn.
        if ((instruction & 0x0FFFFFF0u) == 0x012FFF10u) {
            return $"BX{condition} {Register(index: (int)(instruction & 0xFu))}";
        }

        // Branch / branch-with-link.
        if ((instruction & 0x0E000000u) == 0x0A000000u) {
            var offset = (int)(instruction << 8) >> 6; // sign-extend the 24-bit field, then <<2
            var target = (uint)(address + 8 + offset);

            return $"B{((instruction & 0x01000000u) != 0u ? "L" : "")}{condition} {Hex32(value: target)}";
        }

        // Multiply / multiply-long (bits 27-22 select, bits 7-4 == 1001).
        if (((instruction & 0x0FC000F0u) == 0x00000090u) || ((instruction & 0x0F8000F0u) == 0x00800090u)) {
            return DecodeMultiply(instruction: instruction, condition: condition);
        }

        // PSR transfer (MRS/MSR) — carved out of the data-processing space.
        if ((instruction & 0x0FBF0FFFu) == 0x010F0000u) {
            return $"MRS{condition} {Register(index: (int)((instruction >> 12) & 0xFu))},{(((instruction & 0x00400000u) != 0u) ? "SPSR" : "CPSR")}";
        }

        if (((instruction & 0x0FB00000u) == 0x03200000u) || ((instruction & 0x0FB000F0u) == 0x01200000u)) {
            return DecodeMoveToStatus(instruction: instruction, condition: condition);
        }

        // Halfword / signed data transfer (bit 4 and bit 7 set, bit 25 clear).
        if (((instruction & 0x0E000090u) == 0x00000090u) && ((instruction & 0x00000060u) != 0u)) {
            return DecodeHalfwordTransfer(instruction: instruction, condition: condition);
        }

        // Data processing.
        if ((instruction & 0x0C000000u) == 0x00000000u) {
            return DecodeDataProcessing(instruction: instruction, condition: condition);
        }

        // Single data transfer (LDR/STR).
        if ((instruction & 0x0C000000u) == 0x04000000u) {
            return DecodeSingleTransfer(instruction: instruction, condition: condition);
        }

        // Block data transfer (LDM/STM).
        if ((instruction & 0x0E000000u) == 0x08000000u) {
            return DecodeBlockTransfer(instruction: instruction, condition: condition);
        }

        // Software interrupt.
        if ((instruction & 0x0F000000u) == 0x0F000000u) {
            return $"SWI{condition} 0x{(instruction & 0x00FFFFFFu):X6}";
        }

        return $"DCD {Hex32(value: instruction)}";
    }

    /// <summary>Disassembles one THUMB instruction halfword.</summary>
    /// <param name="address">The instruction's address (for branch-target resolution).</param>
    /// <param name="instruction">The 16-bit instruction halfword.</param>
    /// <returns>The assembly text.</returns>
    public static string DecodeThumb(uint address, ushort instruction) {
        var op = instruction;

        // Format 1/2: shifts and add/subtract.
        if ((op & 0xF800) < 0x1800) {
            var shift = ((op >> 11) & 3);

            return $"{ShiftNames[shift]} {Register(index: op & 7)},{Register(index: (op >> 3) & 7)},#{(op >> 6) & 0x1F}";
        }

        if ((op & 0xF800) == 0x1800) {
            var immediate = ((op & 0x0400) != 0);
            var subtract = ((op & 0x0200) != 0);
            var operand = ((op >> 6) & 7);

            return $"{(subtract ? "SUB" : "ADD")} {Register(index: op & 7)},{Register(index: (op >> 3) & 7)},{(immediate ? $"#{operand}" : Register(index: operand))}";
        }

        // Format 3: move/compare/add/subtract immediate.
        if ((op & 0xE000) == 0x2000) {
            string[] ops = ["MOV", "CMP", "ADD", "SUB"];

            return $"{ops[(op >> 11) & 3]} {Register(index: (op >> 8) & 7)},#0x{(op & 0xFF):X2}";
        }

        // Format 4: ALU operations.
        if ((op & 0xFC00) == 0x4000) {
            string[] alu = ["AND", "EOR", "LSL", "LSR", "ASR", "ADC", "SBC", "ROR", "TST", "NEG", "CMP", "CMN", "ORR", "MUL", "BIC", "MVN"];

            return $"{alu[(op >> 6) & 0xF]} {Register(index: op & 7)},{Register(index: (op >> 3) & 7)}";
        }

        // Format 5: high-register operations / BX.
        if ((op & 0xFC00) == 0x4400) {
            var destination = ((op & 7) | ((op >> 4) & 8));
            var source = ((op >> 3) & 0xF);

            return ((op >> 8) & 3) switch {
                0 => $"ADD {Register(index: destination)},{Register(index: source)}",
                1 => $"CMP {Register(index: destination)},{Register(index: source)}",
                2 => $"MOV {Register(index: destination)},{Register(index: source)}",
                _ => $"BX {Register(index: source)}",
            };
        }

        // Format 6: PC-relative load.
        if ((op & 0xF800) == 0x4800) {
            var target = ((address + 4) & ~3u) + (uint)((op & 0xFF) << 2);

            return $"LDR {Register(index: (op >> 8) & 7)},[{Hex32(value: target)}]";
        }

        // Formats 7/8: load/store with register offset.
        if ((op & 0xF200) == 0x5000) {
            string[] ops = ["STR", "STRH", "STRB", "LDRSB", "LDR", "LDRH", "LDRB", "LDRSH"];

            return $"{ops[(op >> 9) & 7]} {Register(index: op & 7)},[{Register(index: (op >> 3) & 7)},{Register(index: (op >> 6) & 7)}]";
        }

        // Format 9: load/store with immediate offset.
        if ((op & 0xE000) == 0x6000) {
            var load = ((op & 0x0800) != 0);
            var byteAccess = ((op & 0x1000) != 0);
            var offset = ((op >> 6) & 0x1F) << (byteAccess ? 0 : 2);

            return $"{(load ? "LDR" : "STR")}{(byteAccess ? "B" : "")} {Register(index: op & 7)},[{Register(index: (op >> 3) & 7)},#0x{offset:X}]";
        }

        // Format 10: halfword load/store.
        if ((op & 0xF000) == 0x8000) {
            var offset = ((op >> 6) & 0x1F) << 1;

            return $"{(((op & 0x0800) != 0) ? "LDRH" : "STRH")} {Register(index: op & 7)},[{Register(index: (op >> 3) & 7)},#0x{offset:X}]";
        }

        // Format 11: SP-relative load/store.
        if ((op & 0xF000) == 0x9000) {
            return $"{(((op & 0x0800) != 0) ? "LDR" : "STR")} {Register(index: (op >> 8) & 7)},[SP,#0x{(op & 0xFF) << 2:X}]";
        }

        // Format 12: load address.
        if ((op & 0xF000) == 0xA000) {
            return $"ADD {Register(index: (op >> 8) & 7)},{(((op & 0x0800) != 0) ? "SP" : "PC")},#0x{(op & 0xFF) << 2:X}";
        }

        // Format 13: add offset to SP.
        if ((op & 0xFF00) == 0xB000) {
            return $"ADD SP,#{(((op & 0x80) != 0) ? "-" : "")}0x{(op & 0x7F) << 2:X}";
        }

        // Format 14: push/pop.
        if ((op & 0xF600) == 0xB400) {
            var pop = ((op & 0x0800) != 0);
            var extra = ((op & 0x0100) != 0) ? (pop ? "PC" : "LR") : null;

            return $"{(pop ? "POP" : "PUSH")} {RegisterList(mask: (byte)op, extra: extra)}";
        }

        // Format 15: multiple load/store.
        if ((op & 0xF000) == 0xC000) {
            return $"{(((op & 0x0800) != 0) ? "LDMIA" : "STMIA")} {Register(index: (op >> 8) & 7)}!,{RegisterList(mask: (byte)op, extra: null)}";
        }

        // Format 17: software interrupt.
        if ((op & 0xFF00) == 0xDF00) {
            return $"SWI 0x{(op & 0xFF):X2}";
        }

        // Format 16: conditional branch.
        if ((op & 0xF000) == 0xD000) {
            var offset = (int)(sbyte)(op & 0xFF) << 1;

            return $"B{Conditions[(op >> 8) & 0xF]} {Hex32(value: (uint)(address + 4 + offset))}";
        }

        // Format 18: unconditional branch.
        if ((op & 0xF800) == 0xE000) {
            var offset = (int)(op << 21) >> 20; // sign-extend 11-bit, <<1

            return $"B {Hex32(value: (uint)(address + 4 + offset))}";
        }

        // Format 19: long branch with link (two halfwords; the first stashes the high offset).
        if ((op & 0xF000) == 0xF000) {
            return $"BL (hi/lo) 0x{(op & 0x7FF):X3}";
        }

        return $"DCW 0x{op:X4}";
    }

    private static string DecodeMultiply(uint instruction, string condition) {
        var setFlags = (((instruction & 0x00100000u) != 0u) ? "S" : "");
        var rd = (int)((instruction >> 16) & 0xFu);
        var rn = (int)((instruction >> 12) & 0xFu);
        var rs = (int)((instruction >> 8) & 0xFu);
        var rm = (int)(instruction & 0xFu);

        if ((instruction & 0x00800000u) != 0u) {
            var unsigned = (((instruction & 0x00400000u) == 0u) ? "U" : "S");
            var accumulate = (((instruction & 0x00200000u) != 0u) ? "MLAL" : "MULL");

            return $"{unsigned}{accumulate}{condition}{setFlags} {Register(index: rn)},{Register(index: rd)},{Register(index: rm)},{Register(index: rs)}";
        }

        return (((instruction & 0x00200000u) != 0u)
            ? $"MLA{condition}{setFlags} {Register(index: rd)},{Register(index: rm)},{Register(index: rs)},{Register(index: rn)}"
            : $"MUL{condition}{setFlags} {Register(index: rd)},{Register(index: rm)},{Register(index: rs)}");
    }

    private static string DecodeMoveToStatus(uint instruction, string condition) {
        var target = (((instruction & 0x00400000u) != 0u) ? "SPSR" : "CPSR");

        if ((instruction & 0x02000000u) != 0u) {
            var rotate = (int)((instruction >> 8) & 0xFu) * 2;
            var value = (uint)BitwiseRotateRight(value: (instruction & 0xFFu), amount: rotate);

            return $"MSR{condition} {target}_flg,#{Hex32(value: value)}";
        }

        return $"MSR{condition} {target},{Register(index: (int)(instruction & 0xFu))}";
    }

    private static string DecodeDataProcessing(uint instruction, string condition) {
        var opcode = (int)((instruction >> 21) & 0xFu);
        var setFlags = ((instruction & 0x00100000u) != 0u);
        var rn = (int)((instruction >> 16) & 0xFu);
        var rd = (int)((instruction >> 12) & 0xFu);
        var mnemonic = DataOps[opcode];
        var operand = DecodeOperand2(instruction: instruction);
        var setSuffix = ((setFlags && (opcode is < 8 or > 11)) ? "S" : "");

        // TST/TEQ/CMP/CMN take no destination; MOV/MVN take no first operand.
        if (opcode is >= 8 and <= 11) {
            return $"{mnemonic}{condition} {Register(index: rn)},{operand}";
        }

        if (opcode is 13 or 15) {
            return $"{mnemonic}{condition}{setSuffix} {Register(index: rd)},{operand}";
        }

        return $"{mnemonic}{condition}{setSuffix} {Register(index: rd)},{Register(index: rn)},{operand}";
    }

    private static string DecodeOperand2(uint instruction) {
        if ((instruction & 0x02000000u) != 0u) {
            var rotate = (int)((instruction >> 8) & 0xFu) * 2;
            var value = BitwiseRotateRight(value: (instruction & 0xFFu), amount: rotate);

            return $"#{Hex32(value: value)}";
        }

        var rm = Register(index: (int)(instruction & 0xFu));
        var shiftType = ShiftNames[(instruction >> 5) & 3u];

        if ((instruction & 0x00000010u) != 0u) {
            return $"{rm},{shiftType} {Register(index: (int)((instruction >> 8) & 0xFu))}";
        }

        var amount = (int)((instruction >> 7) & 0x1Fu);

        if (amount == 0) {
            // A zero immediate shift is just the register (LSL #0), except the special ROR #0 = RRX and LSR/ASR #32.
            return (((instruction >> 5) & 3u) == 0u) ? rm : $"{rm},{((((instruction >> 5) & 3u) == 3u) ? "RRX" : $"{shiftType} #32")}";
        }

        return $"{rm},{shiftType} #{amount}";
    }

    private static string DecodeSingleTransfer(uint instruction, string condition) {
        var load = ((instruction & 0x00100000u) != 0u);
        var byteAccess = ((instruction & 0x00400000u) != 0u);
        var rd = (int)((instruction >> 12) & 0xFu);
        var rn = (int)((instruction >> 16) & 0xFu);
        var mnemonic = $"{(load ? "LDR" : "STR")}{(byteAccess ? "B" : "")}{condition}";
        var offset = DecodeTransferOffset(instruction: instruction, immediateBitClearMeansImmediate: true);

        return $"{mnemonic} {Register(index: rd)},{FormatAddress(rn: rn, offset: offset, instruction: instruction)}";
    }

    private static string DecodeHalfwordTransfer(uint instruction, string condition) {
        var load = ((instruction & 0x00100000u) != 0u);
        var rd = (int)((instruction >> 12) & 0xFu);
        var rn = (int)((instruction >> 16) & 0xFu);
        var kind = ((instruction >> 5) & 3u) switch {
            1u => "H",
            2u => "SB",
            _ => "SH",
        };
        var mnemonic = $"{(load ? "LDR" : "STR")}{kind}{condition}";

        // Immediate offset is split across the high and low nibbles when bit 22 is set; else it is a register.
        string offset;

        if ((instruction & 0x00400000u) != 0u) {
            var immediate = (int)(((instruction >> 4) & 0xF0u) | (instruction & 0xFu));

            offset = ((immediate != 0) ? $",#{((((instruction & 0x00800000u) != 0u)) ? "" : "-")}0x{immediate:X}" : "");
        } else {
            offset = $",{((((instruction & 0x00800000u) != 0u)) ? "" : "-")}{Register(index: (int)(instruction & 0xFu))}";
        }

        return $"{mnemonic} {Register(index: rd)},[{Register(index: rn)}{offset}]";
    }

    private static string DecodeTransferOffset(uint instruction, bool immediateBitClearMeansImmediate) {
        var down = ((instruction & 0x00800000u) == 0u) ? "-" : "";

        // For LDR/STR, bit 25 SET means a register (shifted) offset, CLEAR means a 12-bit immediate.
        if ((instruction & 0x02000000u) == 0u) {
            var immediate = (instruction & 0xFFFu);

            return ((immediate != 0) ? $",#{down}0x{immediate:X}" : "");
        }

        var rm = Register(index: (int)(instruction & 0xFu));
        var shiftType = ShiftNames[(instruction >> 5) & 3u];
        var amount = (int)((instruction >> 7) & 0x1Fu);

        return ((amount != 0) ? $",{down}{rm},{shiftType} #{amount}" : $",{down}{rm}");
    }

    private static string FormatAddress(int rn, string offset, uint instruction) {
        var preIndexed = ((instruction & 0x01000000u) != 0u);
        var writeBack = ((instruction & 0x00200000u) != 0u);

        return (preIndexed
            ? $"[{Register(index: rn)}{offset}]{((writeBack) ? "!" : "")}"
            : $"[{Register(index: rn)}]{offset}");
    }

    private static string DecodeBlockTransfer(uint instruction, string condition) {
        var load = ((instruction & 0x00100000u) != 0u);
        var up = ((instruction & 0x00800000u) != 0u);
        var preIndexed = ((instruction & 0x01000000u) != 0u);
        var mode = (up ? (preIndexed ? "IB" : "IA") : (preIndexed ? "DB" : "DA"));
        var writeBack = (((instruction & 0x00200000u) != 0u) ? "!" : "");
        var force = (((instruction & 0x00400000u) != 0u) ? "^" : "");

        return $"{(load ? "LDM" : "STM")}{condition}{mode} {Register(index: (int)((instruction >> 16) & 0xFu))}{writeBack},{RegisterList(mask: (ushort)instruction, extra: null)}{force}";
    }

    private static string RegisterList(ushort mask, string? extra) {
        var builder = new StringBuilder(value: "{");
        var first = true;

        for (var index = 0; (index < 16); ++index) {
            if ((mask & (1 << index)) != 0) {
                if (!first) {
                    builder.Append(value: ',');
                }

                builder.Append(value: Register(index: index));
                first = false;
            }
        }

        if (extra is not null) {
            if (!first) {
                builder.Append(value: ',');
            }

            builder.Append(value: extra);
        }

        builder.Append(value: '}');

        return builder.ToString();
    }

    private static uint BitwiseRotateRight(uint value, int amount) =>
        ((amount == 0) ? value : ((value >> amount) | (value << (32 - amount))));

    private static string Register(int index) =>
        index switch {
            13 => "SP",
            14 => "LR",
            15 => "PC",
            _ => $"R{index.ToString(provider: CultureInfo.InvariantCulture)}",
        };

    private static string Hex32(uint value) =>
        $"0x{value.ToString(format: "X8", provider: CultureInfo.InvariantCulture)}";
}
