using System.Globalization;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// A pure, side-effect-free SM83 disassembler — machine-neutral truth about the ISA, decoded straight from the same
/// bit-field structure the <see cref="Sm83"/> core's <c>Execute</c> dispatch uses, so the two never drift. It reads bytes
/// through a caller-supplied side-effect-free reader (a debug bus peek) rather than owning memory, so a host can
/// disassemble any bus address without perturbing the machine. Every opcode decodes to a mnemonic; there is no illegal
/// escape hatch — the handful of undefined opcodes render as their assembler <c>DB</c> byte, exactly what the CPU wedges
/// on.
/// </summary>
public static class Sm83Disassembler {
    private static readonly string[] Registers = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
    private static readonly string[] AluOps = ["ADD A,", "ADC A,", "SUB ", "SBC A,", "AND ", "XOR ", "OR ", "CP "];
    private static readonly string[] Conditions = ["NZ", "Z", "NC", "C"];
    private static readonly string[] RotateOps = ["RLC", "RRC", "RL", "RR", "SLA", "SRA", "SWAP", "SRL"];

    /// <summary>Decodes the one instruction at <paramref name="address"/>, reading its bytes through
    /// <paramref name="read"/>.</summary>
    /// <param name="address">The address of the opcode's first byte.</param>
    /// <param name="read">A side-effect-free byte reader over the bus address space (a debug peek).</param>
    /// <returns>The instruction's byte length and its assembly text.</returns>
    public static (int Length, string Text) Decode(ushort address, Func<ushort, byte> read) {
        ArgumentNullException.ThrowIfNull(argument: read);

        var opcode = read(address);
        var byte1 = read((ushort)(address + 1));
        var byte2 = read((ushort)(address + 2));
        var word = (ushort)((byte2 << 8) | byte1);

        // The same dispatch order Execute() uses: the CB prefix, then the four regular bit-field blocks, then the four
        // 32-entry range groups for the remainder.
        if (opcode == 0xCB) {
            return (2, DecodeBitOperation(opcode: byte1));
        }

        if ((opcode >= 0x40) && (opcode <= 0x7F)) {
            return (1, ((opcode == 0x76) ? "HALT" : $"LD {Registers[(opcode >> 3) & 7]},{Registers[opcode & 7]}"));
        }

        if ((opcode >= 0x80) && (opcode <= 0xBF)) {
            return (1, $"{AluOps[(opcode >> 3) & 7]}{Registers[opcode & 7]}");
        }

        if ((opcode & 0xC7) == 0x04) {
            return (1, $"INC {Registers[(opcode >> 3) & 7]}");
        }

        if ((opcode & 0xC7) == 0x05) {
            return (1, $"DEC {Registers[(opcode >> 3) & 7]}");
        }

        if ((opcode & 0xC7) == 0x06) {
            return (2, $"LD {Registers[(opcode >> 3) & 7]},{Hex8(value: byte1)}");
        }

        if ((opcode & 0xC7) == 0xC6) {
            return (2, $"{AluOps[(opcode >> 3) & 7]}{Hex8(value: byte1)}");
        }

        if ((opcode & 0xC7) == 0xC7) {
            return (1, $"RST {Hex8(value: (byte)(opcode & 0x38))}");
        }

        return opcode switch {
            < 0x20 => DecodeLowGroup0(opcode: opcode, address: address, byte1: byte1, word: word),
            < 0x40 => DecodeLowGroup1(opcode: opcode, address: address, byte1: byte1, word: word),
            < 0xE0 => DecodeControlGroup(opcode: opcode, word: word),
            _ => DecodeHighPageGroup(opcode: opcode, byte1: byte1, word: word),
        };
    }

    private static (int Length, string Text) DecodeLowGroup0(byte opcode, ushort address, byte byte1, ushort word) =>
        opcode switch {
            0x00 => (1, "NOP"),
            0x10 => (2, "STOP 0"),
            0x01 => (3, $"LD BC,{Hex16(value: word)}"),
            0x11 => (3, $"LD DE,{Hex16(value: word)}"),
            0x02 => (1, "LD (BC),A"),
            0x12 => (1, "LD (DE),A"),
            0x0A => (1, "LD A,(BC)"),
            0x1A => (1, "LD A,(DE)"),
            0x03 => (1, "INC BC"),
            0x13 => (1, "INC DE"),
            0x0B => (1, "DEC BC"),
            0x1B => (1, "DEC DE"),
            0x07 => (1, "RLCA"),
            0x0F => (1, "RRCA"),
            0x17 => (1, "RLA"),
            0x1F => (1, "RRA"),
            0x08 => (3, $"LD ({Hex16(value: word)}),SP"),
            0x09 => (1, "ADD HL,BC"),
            0x19 => (1, "ADD HL,DE"),
            _ => (2, $"JR {RelativeTarget(address: address, offset: byte1)}"),
        };

    private static (int Length, string Text) DecodeLowGroup1(byte opcode, ushort address, byte byte1, ushort word) =>
        opcode switch {
            0x21 => (3, $"LD HL,{Hex16(value: word)}"),
            0x31 => (3, $"LD SP,{Hex16(value: word)}"),
            0x22 => (1, "LD (HL+),A"),
            0x32 => (1, "LD (HL-),A"),
            0x2A => (1, "LD A,(HL+)"),
            0x3A => (1, "LD A,(HL-)"),
            0x23 => (1, "INC HL"),
            0x33 => (1, "INC SP"),
            0x2B => (1, "DEC HL"),
            0x3B => (1, "DEC SP"),
            0x27 => (1, "DAA"),
            0x2F => (1, "CPL"),
            0x37 => (1, "SCF"),
            0x3F => (1, "CCF"),
            0x29 => (1, "ADD HL,HL"),
            0x39 => (1, "ADD HL,SP"),
            _ => (2, $"JR {Conditions[(opcode >> 3) & 3]},{RelativeTarget(address: address, offset: byte1)}"),
        };

    private static (int Length, string Text) DecodeControlGroup(byte opcode, ushort word) =>
        opcode switch {
            0xC0 or 0xC8 or 0xD0 or 0xD8 => (1, $"RET {Conditions[(opcode >> 3) & 3]}"),
            0xC9 => (1, "RET"),
            0xD9 => (1, "RETI"),
            0xC1 => (1, "POP BC"),
            0xD1 => (1, "POP DE"),
            0xC5 => (1, "PUSH BC"),
            0xD5 => (1, "PUSH DE"),
            0xC2 or 0xCA or 0xD2 or 0xDA => (3, $"JP {Conditions[(opcode >> 3) & 3]},{Hex16(value: word)}"),
            0xC3 => (3, $"JP {Hex16(value: word)}"),
            0xC4 or 0xCC or 0xD4 or 0xDC => (3, $"CALL {Conditions[(opcode >> 3) & 3]},{Hex16(value: word)}"),
            0xCD => (3, $"CALL {Hex16(value: word)}"),
            _ => (1, $"DB {Hex8(value: opcode)}"),
        };

    private static (int Length, string Text) DecodeHighPageGroup(byte opcode, byte byte1, ushort word) =>
        opcode switch {
            0xE0 => (2, $"LDH ({HighPage(value: byte1)}),A"),
            0xF0 => (2, $"LDH A,({HighPage(value: byte1)})"),
            0xE1 => (1, "POP HL"),
            0xF1 => (1, "POP AF"),
            0xE5 => (1, "PUSH HL"),
            0xF5 => (1, "PUSH AF"),
            0xE2 => (1, "LD (C),A"),
            0xF2 => (1, "LD A,(C)"),
            0xE9 => (1, "JP (HL)"),
            0xEA => (3, $"LD ({Hex16(value: word)}),A"),
            0xFA => (3, $"LD A,({Hex16(value: word)})"),
            0xE8 => (2, $"ADD SP,{SignedByte(value: byte1)}"),
            0xF8 => (2, $"LD HL,SP{SignedByte(value: byte1)}"),
            0xF9 => (1, "LD SP,HL"),
            0xF3 => (1, "DI"),
            0xFB => (1, "EI"),
            _ => (1, $"DB {Hex8(value: opcode)}"),
        };

    private static string DecodeBitOperation(byte opcode) {
        var index = Registers[opcode & 7];

        return ((opcode >> 6) switch {
            0 => $"{RotateOps[(opcode >> 3) & 7]} {index}",
            1 => $"BIT {(opcode >> 3) & 7},{index}",
            2 => $"RES {(opcode >> 3) & 7},{index}",
            _ => $"SET {(opcode >> 3) & 7},{index}",
        });
    }

    // The absolute target of a relative jump: PC-after-the-2-byte-instruction plus the signed offset — what a reader
    // wants to see, not the raw displacement.
    private static string RelativeTarget(ushort address, byte offset) =>
        Hex16(value: (ushort)(address + 2 + (sbyte)offset));

    private static string HighPage(byte value) =>
        $"0xFF{value.ToString(format: "X2", provider: CultureInfo.InvariantCulture)}";

    private static string SignedByte(byte value) {
        var signed = (sbyte)value;

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{((signed < 0) ? "-" : "+")}0x{Math.Abs(value: signed):X2}");
    }

    private static string Hex8(byte value) =>
        $"0x{value.ToString(format: "X2", provider: CultureInfo.InvariantCulture)}";

    private static string Hex16(ushort value) =>
        $"0x{value.ToString(format: "X4", provider: CultureInfo.InvariantCulture)}";
}
