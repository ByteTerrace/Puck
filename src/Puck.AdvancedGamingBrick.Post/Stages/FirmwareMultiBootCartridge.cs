using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Original native sender and downloadable ARM program, with no retail header artwork or firmware instructions.</summary>
internal static class FirmwareMultiBootCartridge {
    internal const uint Completion = 0x02003000;
    internal const int PayloadLength = 256;
    internal const byte Palette = 0x81;
    internal const byte ClientData = 0x11;
    internal const byte RandomData = 0xA1;
    internal const byte Handshake = 0x20; // (0x11 + client1 + absent-client2 + absent-client3) modulo 256.
    private const uint RomBase = 0x08000000;
    private const int TableOffset = 0x400;
    private const int ParametersOffset = 0x600;
    private const int DownloadOffset = 0x800;

    internal static byte[] Download() {
        var bytes = new byte[0xC0 + PayloadLength];
        for (var index = 4; index < 0xA0; ++index) { bytes[index] = (byte)(index * 37 + 11); }
        "PUCK LINK   "u8.CopyTo(destination: bytes.AsSpan(start: 0xA0));
        "PUCKBT"u8.CopyTo(destination: bytes.AsSpan(start: 0xAC));
        bytes[0xB2] = 0x96;
        var complement = -0x19;
        for (var index = 0xA0; index <= 0xBC; ++index) { complement -= bytes[index]; }
        bytes[0xBD] = (byte)complement;
        for (var index = 0xC0; index < bytes.Length; ++index) { bytes[index] = (byte)(index * 53 + 19); }
        Write(image: bytes, word: 0, value: 0xEA00002E);
        Write(image: bytes, word: 0xC0 / 4, value: 0xEA00000E); // Download entry branches over the extended header to 0x100.
        bytes.AsSpan(start: 0xC4, length: 0x3C).Clear();
        uint[] program = [
            0xE3A00402, // mov r0, #0x02000000
            0xE2800A03, // add r0, r0, #0x3000
            0xE10F1000, // mrs r1, cpsr
            0xE5801004, // str r1, [r0, #4]
            0xE580D008, // str sp, [r0, #8]
            0xE3A010A5, // mov r1, #0xA5
            0xE5801000, // str r1, [r0]: the downloaded program, not the host, publishes completion.
            0xEAFFFFFE,
        ];
        for (var index = 0; index < program.Length; ++index) { Write(image: bytes, word: 0x100 / 4 + index, value: program[index]); }
        return bytes;
    }

    internal static ushort[] Negotiation(byte[] download, int clients = 1) {
        var clientBits = (ushort)((1 << (clients + 1)) - 2);
        var words = new List<ushort>();
        for (var repeat = 0; repeat < 16; ++repeat) { words.Add(item: 0x6200); }
        words.Add(item: (ushort)(0x6100 | clientBits));
        for (var offset = 0; offset < 0xC0; offset += 2) {
            words.Add(item: BinaryPrimitives.ReadUInt16LittleEndian(source: download.AsSpan(start: offset, length: 2)));
        }
        words.AddRange(collection: new ushort[] { 0x6200, (ushort)(0x6200 | clientBits), 0x6381, 0x6381, (ushort)(0x6400 | HandshakeFor(clients: clients)) });
        return words.ToArray();
    }

    internal static byte[] Sender(byte[] download, ushort[] negotiation, int mode = 1, int clients = 1) {
        var rom = new byte[32 * 1024];
        // A fixed diagnostic program: issue the original negotiation table through SIO, copy its parameter
        // structure into EWRAM, call native SWI 25, and record its return value. Branch targets are word indices.
        uint[] code = [
            0, 0, 0, 0,                 // 0..3: load SIO base, table start/end, response log.
            0xE3A00000, 0xE1C401B4,     // 4..5: RCNT=0; SIO owns the pins.
            0, 0xE1C400B8,             // 6..7: configure SIOCNT; normal variants replace the control literal below.
            0xE1D500B0, 0xE2855002, 0,  // 8..10: next halfword, advance table, BL transfer.
            0xE1C700B0, 0xE2877002,     // 11..12: log child reply.
            0xE1550006, 0,             // 13..14: loop until the table ends.
            0, 0, 0xE3A02013,          // 15..17: copy 0x4C-byte parameter structure.
            0xE4903004, 0xE4813004, 0xE2522001, 0,
            0, 0xE3A01001, 0xEF250000, // 22..24: MultiBoot(parameters, mode); replace the mode immediate below.
            0, 0xE5810004, 0xE3A0005A, 0xE5810000, 0xEAFFFFFE,
            0xE1C400BA, 0, 0xE1C400B8, // 30..32: transfer: SEND=value, START=1.
            0xE1D400B8, 0xE3100080, 0,  // 33..35: wait for hardware completion.
            0, 0xE2500001, 0,          // 36..38: native inter-transfer settling delay.
            0xE1D400B2, 0xE12FFF1E,     // 39..40: return child SIOMULTI1.
            0x04000120, RomBase + TableOffset, RomBase + TableOffset + (uint)(negotiation.Length * 2), 0x02004000,
            0x2003, RomBase + ParametersOffset, FirmwareSwiProbe.Info, Completion, 0x2083, 1024,
        ];
        Load(code: code, at: 0, register: 4, literal: 41);
        Load(code: code, at: 1, register: 5, literal: 42);
        Load(code: code, at: 2, register: 6, literal: 43);
        Load(code: code, at: 3, register: 7, literal: 44);
        Load(code: code, at: 6, register: 0, literal: 45);
        Load(code: code, at: 15, register: 0, literal: 46);
        Load(code: code, at: 16, register: 1, literal: 47);
        Load(code: code, at: 22, register: 0, literal: 47);
        Load(code: code, at: 25, register: 1, literal: 48);
        Load(code: code, at: 31, register: 0, literal: 49);
        Load(code: code, at: 36, register: 0, literal: 50);
        code[10] = Branch(from: 10, to: 30, opcode: 0xEB000000);
        code[14] = Branch(from: 14, to: 8, opcode: 0x1A000000);
        code[21] = Branch(from: 21, to: 18, opcode: 0x1A000000);
        code[35] = Branch(from: 35, to: 33, opcode: 0x1A000000);
        code[38] = Branch(from: 38, to: 37, opcode: 0x1A000000);
        code[23] = 0xE3A01000u | (uint)mode;
        if (mode != 1) {
            code[30] = 0xE5840000; // Normal mode sends full words through SIODATA32.
            code[39] = 0xE5940000;
            code[45] = mode == 0 ? 0x1001u : 0x1003u;
            code[49] = code[45] | 0x80;
        }
        for (var index = 0; index < code.Length; ++index) { Write(image: rom, word: index, value: code[index]); }
        for (var index = 0; index < negotiation.Length; ++index) {
            BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: TableOffset + index * 2), value: negotiation[index]);
        }
        var parameters = rom.AsSpan(start: ParametersOffset, length: 0x4C);
        parameters[0x14] = HandshakeFor(clients: clients);
        parameters[0x19] = ClientData;
        parameters[0x1A] = clients >= 2 ? (byte)0x12 : (byte)0xFF;
        parameters[0x1B] = clients >= 3 ? (byte)0x13 : (byte)0xFF;
        parameters[0x1C] = Palette;
        parameters[0x1E] = (byte)((1 << (clients + 1)) - 2);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: parameters[0x20..], value: RomBase + DownloadOffset + 0xC0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: parameters[0x24..], value: RomBase + DownloadOffset + (uint)download.Length);
        download.CopyTo(array: rom, index: DownloadOffset);
        return rom;
    }

    internal static byte HandshakeFor(int clients) => (byte)(0x11 + 0x11 + (clients >= 2 ? 0x12 : 0xFF) + (clients >= 3 ? 0x13 : 0xFF));

    private static void Load(uint[] code, int at, int register, int literal) =>
        code[at] = 0xE59F0000u | (uint)(register << 12) | (uint)((literal - at - 2) * 4);
    private static uint Branch(int from, int to, uint opcode) => opcode | ((uint)(to - from - 2) & 0xFFFFFF);
    private static void Write(byte[] image, int word, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: word * 4), value: value);
}
