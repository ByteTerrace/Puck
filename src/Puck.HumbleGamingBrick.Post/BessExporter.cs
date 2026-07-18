using System.Buffers.Binary;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Writes a BESS-compliant savestate for a running machine: <c>NAME</c>, <c>INFO</c>, <c>CORE</c>, an optional
/// <c>MBC </c> block (only when the cartridge carries a mapper), and <c>END</c>, over the raw buffers those blocks
/// reference. Everything is read through existing public component surfaces (<see cref="BessScope"/>,
/// <see cref="ICartridge"/>) — no new core seam. See <see cref="Bess"/> for the block set this covers and what it
/// deliberately omits (<c>XOAM</c>, <c>RTC</c>, <c>HUC3</c>, <c>TPP1</c>, <c>MBC7</c>, <c>SGB</c>).
/// </summary>
internal static class BessExporter {
    /// <summary>Exports a BESS-compliant file for a machine's current state.</summary>
    /// <param name="instance">The machine to export.</param>
    /// <param name="model">The emulated model.</param>
    /// <returns>The file bytes and the scope this export captured (for a self-consistency check).</returns>
    public static (byte[] File, BessScopeCapture Scope) Export(MachineInstance instance, ConsoleModel model) {
        var capture = BessScope.Capture(instance: instance, model: model);
        var cartridge = instance.GetRequiredService<ICartridge>();
        var rom = (instance.Configuration.CartridgeRom ?? throw new InvalidOperationException(message: "The machine has no cartridge ROM to describe."));
        var file = new List<byte>(capacity: 16_384);

        var ramOffset = file.Count;
        file.AddRange(collection: capture.Ram);
        var vramOffset = file.Count;
        file.AddRange(collection: capture.Vram);
        var mbcRamOffset = file.Count;
        file.AddRange(collection: capture.MbcRam);
        var oamOffset = file.Count;
        file.AddRange(collection: capture.Oam);
        var hramOffset = file.Count;
        file.AddRange(collection: capture.Hram);
        var backgroundPaletteOffset = file.Count;
        file.AddRange(collection: capture.BackgroundPalette);
        var objectPaletteOffset = file.Count;
        file.AddRange(collection: capture.ObjectPalette);

        var firstBlockOffset = file.Count;

        Bess.WriteBlock(destination: file, tag: "NAME", payload: "Puck.HumbleGamingBrick 1.0"u8);
        Bess.WriteBlock(destination: file, tag: "INFO", payload: BuildInfoBlock(rom: rom));
        Bess.WriteBlock(destination: file, tag: "CORE", payload: BuildCoreBlock(
            capture: capture, model: model,
            ramOffset: ramOffset, vramOffset: vramOffset, mbcRamOffset: mbcRamOffset,
            oamOffset: oamOffset, hramOffset: hramOffset,
            backgroundPaletteOffset: backgroundPaletteOffset, objectPaletteOffset: objectPaletteOffset
        ));

        if (cartridge.Header.Mapper != MapperKind.RomOnly) {
            Bess.WriteBlock(destination: file, tag: "MBC ", payload: BuildMbcBlock(cartridge: cartridge));
        }

        Bess.WriteBlock(destination: file, tag: "END ", payload: []);
        Bess.WriteFooter(destination: file, firstBlockOffset: (uint)firstBlockOffset);

        return (file.ToArray(), capture);
    }
    private static byte[] BuildInfoBlock(byte[] rom) {
        var block = new byte[0x12];

        if (rom.Length >= 0x144) {
            rom.AsSpan(start: 0x134, length: 16).CopyTo(destination: block.AsSpan(start: 0x00, length: 16));
        }
        if (rom.Length >= 0x150) {
            rom.AsSpan(start: 0x14E, length: 2).CopyTo(destination: block.AsSpan(start: 0x10, length: 2));
        }

        return block;
    }
    private static byte[] BuildCoreBlock(BessScopeCapture capture, ConsoleModel model, int ramOffset, int vramOffset, int mbcRamOffset, int oamOffset, int hramOffset, int backgroundPaletteOffset, int objectPaletteOffset) {
        var block = new byte[Bess.CoreBlockLength];

        BinaryPrimitives.WriteUInt16LittleEndian(destination: block.AsSpan(start: 0x00), value: 1); // BESS major
        BinaryPrimitives.WriteUInt16LittleEndian(destination: block.AsSpan(start: 0x02), value: 1); // BESS minor
        Bess.ModelTag(model: model).CopyTo(destination: block.AsSpan(start: 0x04, length: 4));
        BinaryPrimitives.WriteUInt16LittleEndian(destination: block.AsSpan(start: 0x08), value: capture.Pc);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: block.AsSpan(start: 0x0A), value: capture.Af);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: block.AsSpan(start: 0x0C), value: capture.Bc);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: block.AsSpan(start: 0x0E), value: capture.De);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: block.AsSpan(start: 0x10), value: capture.Hl);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: block.AsSpan(start: 0x12), value: capture.Sp);
        block[0x14] = (byte)(capture.Ime ? 1 : 0);
        block[0x15] = capture.Ie;
        block[0x16] = capture.ExecutionState;
        // 0x17 reserved, already zero.
        capture.RegisterPage.CopyTo(destination: block.AsSpan(start: Bess.RegisterPageOffset, length: Bess.RegisterPageLength));

        WriteBufferEntry(block: block, tableOffset: 0x00, size: capture.Ram.Length, fileOffset: ramOffset);
        WriteBufferEntry(block: block, tableOffset: 0x08, size: capture.Vram.Length, fileOffset: vramOffset);
        WriteBufferEntry(block: block, tableOffset: 0x10, size: capture.MbcRam.Length, fileOffset: mbcRamOffset);
        WriteBufferEntry(block: block, tableOffset: 0x18, size: capture.Oam.Length, fileOffset: oamOffset);
        WriteBufferEntry(block: block, tableOffset: 0x20, size: capture.Hram.Length, fileOffset: hramOffset);
        WriteBufferEntry(block: block, tableOffset: 0x28, size: capture.BackgroundPalette.Length, fileOffset: backgroundPaletteOffset);
        WriteBufferEntry(block: block, tableOffset: 0x30, size: capture.ObjectPalette.Length, fileOffset: objectPaletteOffset);

        return block;
    }
    // tableOffset is relative to Bess.BufferTableOffset (0x98): each entry is a (size, file-offset) UInt32 pair.
    private static void WriteBufferEntry(byte[] block, int tableOffset, int size, int fileOffset) {
        var absolute = (Bess.BufferTableOffset + tableOffset);

        BinaryPrimitives.WriteUInt32LittleEndian(destination: block.AsSpan(start: absolute), value: (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block.AsSpan(start: (absolute + 4)), value: (uint)fileOffset);
    }
    // A mapper-neutral, best-effort register-write reconstruction: the ROM/RAM bank numbers this cartridge's own
    // ComputeRomWindows/TryComputeRamWindow already derive, replayed as writes through the mapper's OWN WriteControl —
    // so each mapper reinterprets them per its own register semantics rather than this exporter assuming one mapper's
    // layout. Deliberately does not reconstruct MBC1's banking-mode register, MBC3's RTC latch/select, or an
    // EEPROM/IR mapper's protocol state (MBC7, HuC1, HuC3, camera) — those need per-mapper introspection this
    // evidence tool's first pass does not add.
    private static byte[] BuildMbcBlock(ICartridge cartridge) {
        var entries = new List<byte>(capacity: 9);

        void Write(ushort address, byte value) {
            entries.Add(item: (byte)address);
            entries.Add(item: (byte)(address >> 8));
            entries.Add(item: value);
        }

        if (cartridge.ExternalRamByteCount > 0) {
            Write(0x0000, 0x0A); // RAM enable.
        }

        cartridge.ComputeRomWindows(bank0Offset: out _, bankNOffset: out var bankNOffset);

        if (bankNOffset >= 0) {
            var romBank = (bankNOffset / 0x4000);

            Write(0x2000, (byte)(romBank & 0xFF));

            if (romBank > 0xFF) {
                Write(0x3000, (byte)((romBank >> 8) & 0x01)); // MBC5's 9th ROM-bank bit.
            }
        }

        if (cartridge.TryComputeRamWindow(offset: out var ramOffset, length: out var ramLength) && (ramLength > 0)) {
            Write(0x4000, (byte)(ramOffset / 0x2000));
        }

        return entries.ToArray();
    }
}
