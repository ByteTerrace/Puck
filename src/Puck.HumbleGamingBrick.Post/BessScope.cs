using Puck.HumbleGamingBrick.Interfaces;
using Puck.Snapshots;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>One snapshot of exactly the state a BESS savestate models — everything <see cref="BessExporter"/> writes
/// and <see cref="BessImporter"/> restores, captured through existing public component surfaces (no new core seam):
/// <see cref="ICpu"/> for the registers, <see cref="Sm83StateCodec"/> for IME/execution state, and
/// <see cref="ISystemBus.ReadByte"/> for every memory region BESS covers.</summary>
/// <param name="Pc">The program counter.</param>
/// <param name="Af">The accumulator/flags pair (A high byte, F low byte).</param>
/// <param name="Bc">The BC register pair.</param>
/// <param name="De">The DE register pair.</param>
/// <param name="Hl">The HL register pair.</param>
/// <param name="Sp">The stack pointer.</param>
/// <param name="Ime">The interrupt-master-enable flag.</param>
/// <param name="Ie">The interrupt-enable register (IE, 0xFFFF).</param>
/// <param name="ExecutionState">0 = running, 1 = halted, 2 = stopped (the BESS convention).</param>
/// <param name="RegisterPage">The 128 memory-mapped registers (0xFF00-0xFF7F), unmasked (the export view).</param>
/// <param name="Ram">The current 8&#160;KiB CPU-visible work-RAM window (0xC000-0xDFFF).</param>
/// <param name="Vram">The current 8&#160;KiB CPU-visible video-RAM window (0x8000-0x9FFF); a Color machine's other bank
/// is a documented best-effort omission (the spec explicitly allows an undersized VRAM block).</param>
/// <param name="MbcRam">The cartridge's external (save) RAM, via <see cref="ICartridge.ExportExternalRam"/>.</param>
/// <param name="Oam">Object attribute memory (0xFE00-0xFE9F).</param>
/// <param name="Hram">High RAM (0xFF80-0xFFFE).</param>
/// <param name="BackgroundPalette">The 64-byte CGB background color-palette RAM, or empty on a monochrome model.</param>
/// <param name="ObjectPalette">The 64-byte CGB object color-palette RAM, or empty on a monochrome model.</param>
internal sealed record BessScopeCapture(
    ushort Pc, ushort Af, ushort Bc, ushort De, ushort Hl, ushort Sp,
    bool Ime, byte Ie, byte ExecutionState,
    byte[] RegisterPage,
    byte[] Ram, byte[] Vram, byte[] MbcRam, byte[] Oam, byte[] Hram,
    byte[] BackgroundPalette, byte[] ObjectPalette
);

/// <summary>Captures and fingerprints the BESS-modeled slice of a machine's state.</summary>
internal static class BessScope {
    /// <summary>The registers backing the CGB color-palette RAM (index port, data port).</summary>
    private static readonly (ushort Index, ushort Data) BackgroundPaletteRegisters = (MemoryMap.BackgroundColorPaletteIndex, MemoryMap.BackgroundColorPaletteData);
    private static readonly (ushort Index, ushort Data) ObjectPaletteRegisters = (MemoryMap.ObjectColorPaletteIndex, MemoryMap.ObjectColorPaletteData);

    /// <summary>Captures the current BESS-modeled state of a running machine.</summary>
    /// <param name="instance">The machine to read.</param>
    /// <param name="model">The emulated model (gates whether CGB palette RAM is captured).</param>
    /// <returns>The capture.</returns>
    public static BessScopeCapture Capture(MachineInstance instance, ConsoleModel model) {
        var bus = instance.GetRequiredService<ISystemBus>();
        var cpu = instance.GetRequiredService<Sm83>();
        var interrupts = instance.GetRequiredService<IInterruptController>();
        var key1 = instance.GetRequiredService<IKey1>();
        var cartridge = instance.GetRequiredService<ICartridge>();
        var scratch = new StateWriter(capacity: Sm83StateCodec.ByteCount);
        var scratchBuffer = new byte[Sm83StateCodec.ByteCount];

        Sm83StateCodec.ReadTail(cpu: cpu, scratch: scratch, buffer: scratchBuffer, halted: out var halted, ime: out var ime, eiPending: out _);

        var executionState = (byte)(key1.IsStopped ? 2 : (halted ? 1 : 0));
        var isColor = model.SupportsColor();

        return new BessScopeCapture(
            Pc: cpu.ProgramCounter,
            Af: (ushort)((cpu.A << 8) | cpu.F),
            Bc: (ushort)((cpu.B << 8) | cpu.C),
            De: (ushort)((cpu.D << 8) | cpu.E),
            Hl: (ushort)((cpu.H << 8) | cpu.L),
            Sp: cpu.StackPointer,
            Ime: ime,
            Ie: (byte)interrupts.Enabled,
            ExecutionState: executionState,
            RegisterPage: ReadRange(bus: bus, start: MemoryMap.IoRegistersStart, length: Bess.RegisterPageLength),
            Ram: ReadRange(bus: bus, start: MemoryMap.WorkRamBank0Start, length: 0x2000),
            Vram: ReadRange(bus: bus, start: MemoryMap.VideoRamStart, length: 0x2000),
            MbcRam: cartridge.ExportExternalRam(),
            Oam: ReadRange(bus: bus, start: MemoryMap.ObjectAttributeMemoryStart, length: 0xA0),
            Hram: ReadRange(bus: bus, start: MemoryMap.HighRamStart, length: 0x7F),
            BackgroundPalette: (isColor ? ReadColorPalette(bus: bus, registers: BackgroundPaletteRegisters) : []),
            ObjectPalette: (isColor ? ReadColorPalette(bus: bus, registers: ObjectPaletteRegisters) : [])
        );
    }
    /// <summary>Fingerprints exactly the bytes an export/import round trip is expected to preserve — the register page
    /// with five documented non-goals masked to zero on both sides, so they never show as a false divergence: 0xFF46
    /// DMA-start and 0xFF55 HDMA-start (this importer deliberately does not restore them; see
    /// <see cref="BessImporter"/>), STAT's (0xFF41) mode and LYC-coincidence bits (0-2), LY (0xFF44) itself, and NR52's
    /// (0xFF26) per-channel active-status bits (0-3) — all hardware-derived, read-only status the PPU/APU recompute
    /// from their own live internal state machines, not settable through a register write (only STAT's
    /// interrupt-enable bits 3-6 and NR52's power bit 7 are genuinely restorable this way; BESS itself only ever
    /// captures the LY byte, not the sub-dot phase behind it, so no BESS-compliant importer can reconstruct the exact
    /// internal state from it alone).</summary>
    /// <param name="capture">The capture to fingerprint.</param>
    /// <returns>A 64-bit FNV-1a fingerprint.</returns>
    public static ulong Fingerprint(BessScopeCapture capture) {
        var maskedPage = (byte[])capture.RegisterPage.Clone();

        maskedPage[MemoryMap.OamDmaSource - MemoryMap.IoRegistersStart] = 0;
        maskedPage[MemoryMap.HdmaControl - MemoryMap.IoRegistersStart] = 0;
        maskedPage[MemoryMap.LcdStatus - MemoryMap.IoRegistersStart] &= 0xF8;
        maskedPage[MemoryMap.LcdY - MemoryMap.IoRegistersStart] = 0;
        maskedPage[MemoryMap.AudioMasterControl - MemoryMap.IoRegistersStart] &= 0xF0;

        var writer = new StateWriter();

        writer.WriteUInt16(value: capture.Pc);
        writer.WriteUInt16(value: capture.Af);
        writer.WriteUInt16(value: capture.Bc);
        writer.WriteUInt16(value: capture.De);
        writer.WriteUInt16(value: capture.Hl);
        writer.WriteUInt16(value: capture.Sp);
        writer.WriteBoolean(value: capture.Ime);
        writer.WriteByte(value: capture.Ie);
        writer.WriteByte(value: capture.ExecutionState);
        writer.WriteBytes(value: maskedPage);
        writer.WriteBytes(value: capture.Ram);
        writer.WriteBytes(value: capture.Vram);
        writer.WriteBytes(value: capture.MbcRam);
        writer.WriteBytes(value: capture.Oam);
        writer.WriteBytes(value: capture.Hram);
        writer.WriteBytes(value: capture.BackgroundPalette);
        writer.WriteBytes(value: capture.ObjectPalette);

        return StateFingerprint.Compute(data: writer.ToArray());
    }
    /// <summary>Reads a run of bytes through the bus (a diagnostic read; not recorded anywhere, no side effects beyond
    /// whatever a real CPU read on that address would have).</summary>
    public static byte[] ReadRange(ISystemBus bus, ushort start, int length) {
        var bytes = new byte[length];

        for (var index = 0; (index < length); ++index) {
            bytes[index] = bus.ReadByte(address: (ushort)(start + index));
        }

        return bytes;
    }
    /// <summary>Reads the 64-byte CGB color-palette RAM behind an index/data register pair, without perturbing the
    /// index register's live value: the index is written with auto-increment (bit 7) clear for every entry, and the
    /// original index byte is restored afterward.</summary>
    public static byte[] ReadColorPalette(ISystemBus bus, (ushort Index, ushort Data) registers) {
        var original = bus.ReadByte(address: registers.Index);
        var palette = new byte[0x40];

        for (var index = 0; (index < palette.Length); ++index) {
            bus.WriteByte(address: registers.Index, value: (byte)index);
            palette[index] = bus.ReadByte(address: registers.Data);
        }

        bus.WriteByte(address: registers.Index, value: original);

        return palette;
    }
    /// <summary>Writes the 64-byte CGB color-palette RAM back through the same index/data register pair, finishing with
    /// the index register set to a caller-supplied value (the exported byte, including its auto-increment bit) rather
    /// than whatever the target machine's own index register held beforehand — the target has not been told the
    /// source's index/auto-increment state any other way.</summary>
    public static void WriteColorPalette(ISystemBus bus, (ushort Index, ushort Data) registers, byte finalIndexRegister, ReadOnlySpan<byte> palette) {
        if (palette.Length == 0) {
            return;
        }

        for (var index = 0; (index < palette.Length); ++index) {
            bus.WriteByte(address: registers.Index, value: (byte)index);
            bus.WriteByte(address: registers.Data, value: palette[index]);
        }

        bus.WriteByte(address: registers.Index, value: finalIndexRegister);
    }
}
