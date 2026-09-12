using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Original ARM/Thumb reset callers and independent ROM/EWRAM entry markers; no vendor bytes.</summary>
internal static class FirmwareResetCartridge {
    internal const uint Caller = 0x08000400;
    internal const uint Result = 0x02003000;
    internal const uint Marker = Result + 20;

    internal static byte[] Create(byte number, bool thumb) {
        var rom = FirmwarePresentationCartridge.Create();
        Entry(marker: 0x5A).CopyTo(array: rom, index: 0xC0);
        // Deliberately nondefault, valid stacks prove reset performs initialization.
        uint[] caller = [
            0xE321F0D3, // msr cpsr_c, #0xD3
            0xE59FD030, // ldr sp, [pc, #48] -> 0x03007D00
            0xE321F0D2, // msr cpsr_c, #0xD2
            0xE59FD02C, // ldr sp, [pc, #44] -> 0x03007C00
            0xE3A0E044, // mov lr, #0x44: dirty the banked IRQ link register.
            0xE321F01F, // msr cpsr_c, #0x1F
            0xE59FD024, // ldr sp, [pc, #36] -> 0x03007B00
            thumb ? 0xE59FC024u : 0xEF000000u | ((uint)number << 16),
            thumb ? 0xE12FFF1Cu : 0xE3A070EEu, // bx r12 / impossible SWI-return marker
            0xEAFFFFFE,
            0, 0, 0, 0, 0,
            0x03007D00, 0x03007C00, 0x03007B00, 0x08000601,
        ];
        for (var index = 0; index < caller.Length; ++index) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: 0x400 + index * 4, length: 4), value: caller[index]);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: 0x600, length: 2), value: (ushort)(0xDF00 | number));
        BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: 0x602, length: 2), value: 0x27EE);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: 0x604, length: 2), value: 0xE7FE);
        return rom;
    }

    internal static byte[] Entry(byte marker) {
        // This payload first records all three banked stack pointers and exception
        // status registers using native instructions, then publishes its marker.
        uint[] words = [
            0xE3A00402, // mov r0, #0x02000000
            0xE2800A03, // add r0, r0, #0x3000
            0xE580D000, // str sp, [r0]
            0xE321F0D2, // msr cpsr_c, #0xD2
            0xE580D004, // str sp, [r0, #4]
            0xE580E018, // str lr, [r0, #24]
            0xE14F1000, // mrs r1, spsr
            0xE5801008,
            0xE321F0D3,
            0xE580D00C,
            0xE580E01C,
            0xE14F1000,
            0xE5801010,
            0xE321F01F,
            0xE3A01000u | marker,
            0xE5801014,
            0xEAFFFFFE,
        ];
        var code = new byte[words.Length * 4];
        for (var index = 0; index < words.Length; ++index) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: code.AsSpan(start: index * 4, length: 4), value: words[index]);
        }
        return code;
    }
}
