using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Hand-assembled cartridge with a conventional header layout and an observable native entry point.</summary>
internal static class FirmwarePresentationCartridge {
    /// <summary>Creates an original diagnostic image; its logo fields are deliberately not retail artwork.</summary>
    /// <param name="alternateLogo">Selects a second, independently authored header-logo byte pattern.</param>
    /// <param name="malformedHeader">Corrupts the fixed byte and complement checksum without changing executable code.</param>
    /// <returns>A cartridge that writes an EWRAM completion marker and a constant display backdrop after handoff.</returns>
    public static byte[] Create(bool alternateLogo = false, bool malformedHeader = false) {
        var rom = new byte[32 * 1024];
        Write(rom: rom, offset: 0, value: 0xEA00002E); // ARM branch over the header to 0x080000C0.
        for (var index = 0; index < 156; ++index) {
            rom[index + 4] = alternateLogo ? (byte)(index * 17 + 3) : (byte)((index / 4 % 2 == 0) ? 0x3C : 0xC3);
        }
        "PUCK POST   "u8.CopyTo(destination: rom.AsSpan(start: 0xA0));
        (alternateLogo ? "TEST"u8 : "PUCK"u8).CopyTo(destination: rom.AsSpan(start: 0xAC));
        "BT"u8.CopyTo(destination: rom.AsSpan(start: 0xB0));
        rom[0xB2] = 0x96;
        var checksum = -0x19;
        for (var index = 0xA0; index <= 0xBC; ++index) {
            checksum -= rom[index];
        }
        rom[0xBD] = (byte)checksum;
        if (malformedHeader) {
            rom[0xB2] = 0;
            rom[0xBD] ^= 0xFF;
        }
        uint[] instructions = [
            0xE3A00402, // mov r0, #0x02000000
            0xE3A0105A, // mov r1, #0x5A
            0xE5801000, // str r1, [r0]: independent cartridge-entry marker.
            0xE3A02404, // mov r2, #0x04000000
            0xE3A03000, // mov r3, #0
            0xE5823000, // str r3, [r2]: mode 0, no enabled layers.
            0xE3A02405, // mov r2, #0x05000000
            0xE59F3004, // ldr r3, [pc, #4]
            0xE5823000, // str r3, [r2]: solid diagnostic backdrop.
            0xEAFFFFFE, // b .
            0x00001234,
        ];
        for (var index = 0; index < instructions.Length; ++index) {
            Write(rom: rom, offset: 0xC0 + index * 4, value: instructions[index]);
        }
        return rom;
    }

    private static void Write(byte[] rom, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: offset, length: 4), value: value);
}
