using System.Buffers.Binary;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.Snapshots;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>The state a BESS import reports back — enough to eyeball a round trip against another
/// BESS-compliant tool without decoding the file by hand.</summary>
/// <param name="EmulatorName">The originating emulator's <c>NAME</c> block text, or empty when absent.</param>
/// <param name="ModelTag">The <c>CORE</c> block's 4-character model identifier.</param>
/// <param name="Pc">The restored program counter.</param>
/// <param name="Af">The restored AF pair.</param>
/// <param name="Bc">The restored BC pair.</param>
/// <param name="De">The restored DE pair.</param>
/// <param name="Hl">The restored HL pair.</param>
/// <param name="Sp">The restored stack pointer.</param>
/// <param name="Ime">The restored interrupt-master-enable flag.</param>
/// <param name="Ie">The restored IE register.</param>
/// <param name="If">The IF register read back after import (from the register page, not separately restorable —
/// see <see cref="BessImporter"/>'s remarks).</param>
/// <param name="Lcdc">The LCDC register read back after import.</param>
/// <param name="Stat">The STAT register read back after import.</param>
/// <param name="Ly">The LY register read back after import.</param>
/// <param name="RomBank">The switchable ROM bank <see cref="ICartridge.ComputeRomWindows"/> reports after import.</param>
/// <param name="RamBank">The external-RAM bank <see cref="ICartridge.TryComputeRamWindow"/> reports after import, or
/// -1 when the cartridge has no RAM window.</param>
internal readonly record struct BessImportReport(
    string EmulatorName, string ModelTag,
    ushort Pc, ushort Af, ushort Bc, ushort De, ushort Hl, ushort Sp,
    bool Ime, byte Ie, byte If, byte Lcdc, byte Stat, byte Ly,
    int RomBank, int RamBank
);

/// <summary>
/// Loads a BESS-compliant savestate into a running machine and reports the state it restored. Registers, IME, IE, and
/// the execution state (halted/stopped) round-trip exactly through <see cref="ICpu"/> and
/// <see cref="Sm83StateCodec"/>. The 128 memory-mapped registers are replayed through the bus with three documented,
/// spec-flagged exceptions: DIV (0xFF04, whose write side effect resets the divider rather than setting it — restored
/// via the timer's own <see cref="ISnapshotable"/> seam instead), DMA-start and HDMA-start (0xFF46/0xFF55, whose write
/// side effect is "begin a transfer", which the spec explicitly says an import must not trigger — left un-restored),
/// and the four NRx4 sound-trigger bits (bit 7 of 0xFF14/0xFF19/0xFF1E/0xFF23, masked off so a restore never
/// retriggers a channel — the spec's own "no value of NRx4 should trigger a sound pulse" caveat).
/// </summary>
internal static class BessImporter {
    // Register-page addresses (relative to 0xFF00) this importer does not replay as a plain bus write, and why.
    private const int DivOffset = (MemoryMap.Divider - MemoryMap.IoRegistersStart);
    private const int DmaStartOffset = (MemoryMap.OamDmaSource - MemoryMap.IoRegistersStart);
    private const int HdmaStartOffset = (MemoryMap.HdmaControl - MemoryMap.IoRegistersStart);
    // The CGB palette index/data ports: replaying FF68/FF6A (index) as a raw byte ahead of FF69/FF6B (data) arms
    // auto-increment (bit 7) before the loop reaches the data port, so a plain replay of FF69/FF6B corrupts the index
    // via WriteColorRam's own auto-increment — ApplyPalette (BessScope's index/data dance, bit 7 always held low)
    // restores the palette RAM and the index register correctly afterward, so all four addresses are skipped here.
    private static readonly int[] PaletteOffsets = [
        (MemoryMap.BackgroundColorPaletteIndex - MemoryMap.IoRegistersStart), (MemoryMap.BackgroundColorPaletteData - MemoryMap.IoRegistersStart),
        (MemoryMap.ObjectColorPaletteIndex - MemoryMap.IoRegistersStart), (MemoryMap.ObjectColorPaletteData - MemoryMap.IoRegistersStart),
    ];
    private static readonly int[] TriggerBitOffsets = [
        (0xFF14 - MemoryMap.IoRegistersStart), (0xFF19 - MemoryMap.IoRegistersStart),
        (0xFF1E - MemoryMap.IoRegistersStart), (0xFF23 - MemoryMap.IoRegistersStart),
    ];

    /// <summary>Imports a BESS-compliant file into a machine. The whole block graph and buffer table are parsed and
    /// validated against the file's bounds AND the destination machine's region capacities before anything is
    /// applied — a malformed file is rejected wholesale and the machine is left exactly as it was; nothing here
    /// mutates on a path that can still fail.</summary>
    /// <param name="instance">The machine to restore into.</param>
    /// <param name="file">The file bytes.</param>
    /// <returns>A report of the restored state.</returns>
    /// <exception cref="InvalidDataException">The file has no valid BESS footer, its block graph is truncated or
    /// out of bounds, it is missing the required <c>CORE</c> block or the required <c>END </c> block, a block
    /// follows <c>END </c>, <c>END </c> has a nonzero payload, a second <c>CORE</c> block is encountered, an
    /// <c>MBC </c> block is encountered before the required <c>CORE</c> block, the <c>CORE</c> block is shorter than
    /// its defined 0xD0-byte prefix, the <c>CORE</c> block declares a BESS major version other than
    /// <see cref="Bess.SupportedCoreMajorVersion"/>, a buffer-table entry references a span outside the file, a
    /// buffer-table entry's declared size exceeds its destination region's capacity on <paramref name="instance"/>'s
    /// model, (for OAM/HRAM/palette) does not match that region's exact permitted size, or (for an optional
    /// <c>MBC </c> block) its length is not a multiple of 3 or one of its records writes an address outside
    /// <c>0x0000-0x7FFF</c>/<c>0xA000-0xBFFF</c>.</exception>
    public static BessImportReport Import(MachineInstance instance, byte[] file) {
        var (name, core, mbc) = ParseAndValidate(file: file, model: instance.Configuration.Model);

        var cpu = instance.GetRequiredService<Sm83>();
        var interrupts = instance.GetRequiredService<IInterruptController>();
        var key1 = instance.GetRequiredService<Key1Component>();
        var timer = instance.GetRequiredService<TimerComponent>();
        var bus = instance.GetRequiredService<ISystemBus>();
        var cartridge = instance.GetRequiredService<ICartridge>();

        var pc = BinaryPrimitives.ReadUInt16LittleEndian(source: core.AsSpan(start: 0x08));
        var af = BinaryPrimitives.ReadUInt16LittleEndian(source: core.AsSpan(start: 0x0A));
        var bc = BinaryPrimitives.ReadUInt16LittleEndian(source: core.AsSpan(start: 0x0C));
        var de = BinaryPrimitives.ReadUInt16LittleEndian(source: core.AsSpan(start: 0x0E));
        var hl = BinaryPrimitives.ReadUInt16LittleEndian(source: core.AsSpan(start: 0x10));
        var sp = BinaryPrimitives.ReadUInt16LittleEndian(source: core.AsSpan(start: 0x12));
        var ime = (core[0x14] != 0);
        var ie = core[0x15];
        var executionState = core[0x16];
        var registerPage = core.AsSpan(start: Bess.RegisterPageOffset, length: Bess.RegisterPageLength).ToArray();

        var scratch = new StateWriter(capacity: Sm83StateCodec.ByteCount);

        // KEY1 bit 7 (current speed) is read-only from the register interface — real hardware only lets a write arm
        // bit 0, the actual speed flips only through the STOP-triggered switch sequence — so the only way to restore
        // double-speed mode is KEY1's own snapshot seam, spliced the same way DIV is below. Done BEFORE the CPU load so
        // Sm83.LoadState's own "re-sync the component clock's speed from KEY1" step (its normal Restore() behavior)
        // picks up the restored value for free.
        RestoreDoubleSpeed(key1: key1, isDoubleSpeed: ((registerPage[MemoryMap.SpeedSwitch - MemoryMap.IoRegistersStart] & 0x80) != 0));

        Sm83StateCodec.Load(
            cpu: cpu, scratch: scratch,
            a: (byte)(af >> 8), f: (byte)(af & 0xF0), b: (byte)(bc >> 8), c: (byte)bc,
            d: (byte)(de >> 8), e: (byte)de, h: (byte)(hl >> 8), l: (byte)hl,
            sp: sp, pc: pc,
            halted: (executionState == 1), haltBug: false, lockedUp: false,
            ime: ime, interruptEnableCountdown: 0
        );

        interrupts.Enabled = (InterruptKind)ie;

        if (executionState == 2) {
            key1.EnterStop();
        }

        // DIV: any bus write to 0xFF04 resets the divider to zero regardless of the byte written, so the only way to
        // set it to a SPECIFIC restored value is the timer's own snapshot seam (the same seam a normal Restore() uses)
        // — TimerComponent.SaveState's field order is [counter:u16, tima, tma, tac, ...], mirrored here narrowly (just
        // enough to splice one field) rather than exposed as a new public setter.
        RestoreDivider(timer: timer, dividerByte: registerPage[DivOffset]);

        for (var offset = 0; (offset < registerPage.Length); ++offset) {
            if ((offset == DivOffset) || (offset == DmaStartOffset) || (offset == HdmaStartOffset) || (Array.IndexOf(array: PaletteOffsets, value: offset) >= 0)) {
                continue;
            }

            var value = registerPage[offset];

            if (Array.IndexOf(array: TriggerBitOffsets, value: offset) >= 0) {
                value = (byte)(value & 0x7F);
            }

            bus.WriteByte(address: (ushort)(MemoryMap.IoRegistersStart + offset), value: value);
        }

        ApplyBuffer(bus: bus, core: core, tableOffset: 0x00, start: MemoryMap.WorkRamBank0Start, file: file, fillCapacity: ((MemoryMap.WorkRamBankNEnd - MemoryMap.WorkRamBank0Start) + 1));
        ApplyBuffer(bus: bus, core: core, tableOffset: 0x08, start: MemoryMap.VideoRamStart, file: file, fillCapacity: ((MemoryMap.VideoRamEnd - MemoryMap.VideoRamStart) + 1));
        ApplyMbcRam(cartridge: cartridge, core: core, file: file);
        ApplyBuffer(bus: bus, core: core, tableOffset: 0x18, start: MemoryMap.ObjectAttributeMemoryStart, file: file);
        ApplyBuffer(bus: bus, core: core, tableOffset: 0x20, start: MemoryMap.HighRamStart, file: file);
        ApplyPalette(bus: bus, core: core, tableOffset: 0x28, registers: (MemoryMap.BackgroundColorPaletteIndex, MemoryMap.BackgroundColorPaletteData), indexRegisterValue: registerPage[MemoryMap.BackgroundColorPaletteIndex - MemoryMap.IoRegistersStart], file: file);
        ApplyPalette(bus: bus, core: core, tableOffset: 0x30, registers: (MemoryMap.ObjectColorPaletteIndex, MemoryMap.ObjectColorPaletteData), indexRegisterValue: registerPage[MemoryMap.ObjectColorPaletteIndex - MemoryMap.IoRegistersStart], file: file);

        // No domain re-check here: ParseAndValidate's ValidateMbcBlock already proved mbc.Length is a multiple of 3
        // and every record's address falls in 0x0000-0x7FFF or 0xA000-0xBFFF, before any of the writes above ran.
        if (mbc is not null) {
            for (var offset = 0; (offset < mbc.Length); offset += 3) {
                var address = (ushort)(mbc[offset] | (mbc[offset + 1] << 8));

                bus.WriteByte(address: address, value: mbc[offset + 2]);
            }
        }

        cartridge.ComputeRomWindows(bank0Offset: out _, bankNOffset: out var bankNOffset);
        var ramBank = (cartridge.TryComputeRamWindow(offset: out var ramOffset, length: out var ramLength) && (ramLength > 0)
            ? (ramOffset / 0x2000)
            : -1);

        return new BessImportReport(
            EmulatorName: name,
            ModelTag: System.Text.Encoding.ASCII.GetString(bytes: core.AsSpan(start: 0x04, length: 4)),
            Pc: cpu.ProgramCounter, Af: (ushort)((cpu.A << 8) | cpu.F), Bc: (ushort)((cpu.B << 8) | cpu.C),
            De: (ushort)((cpu.D << 8) | cpu.E), Hl: (ushort)((cpu.H << 8) | cpu.L), Sp: cpu.StackPointer,
            Ime: ime, Ie: (byte)interrupts.Enabled, If: bus.ReadByte(address: MemoryMap.InterruptFlag),
            Lcdc: bus.ReadByte(address: MemoryMap.LcdControl), Stat: bus.ReadByte(address: MemoryMap.LcdStatus), Ly: bus.ReadByte(address: MemoryMap.LcdY),
            RomBank: ((bankNOffset >= 0) ? (bankNOffset / 0x4000) : -1), RamBank: ramBank
        );
    }
    // Parses and fully validates a BESS file's block graph, CORE buffer table, and optional MBC payload — against
    // both the file's bounds and the destination machine's region capacities — before any machine state is touched
    // (M-08): block headers/payloads used to be sliced unchecked, and the buffer-table offsets/sizes
    // ApplyBuffer/ApplyMbcRam/ApplyPalette read were trusted while the importer was ALREADY mutating CPU/register
    // state, so a truncated or malformed file could throw an incidental range exception mid-restore and leave the
    // machine partially applied, or (worse) an oversized entry could sail past a source-bounds check that only ever
    // looked at the FILE and wrap through `(ushort)(destinationStart + index)` into an unrelated bus region.
    // Similarly, the optional MBC block's records used to be walked into `bus.WriteByte` with no length or address
    // check, so a trailing fragment was silently dropped and an out-of-domain address (e.g. 0xC000) landed an
    // unintended write in imported work RAM. This pass touches only `file` — no service lookups, no writes — so any
    // InvalidDataException it throws leaves the machine byte-for-byte as it was.
    private static (string Name, byte[] Core, byte[]? Mbc) ParseAndValidate(byte[] file, ConsoleModel model) {
        if (!Bess.TryReadFooter(file: file, firstBlockOffset: out var cursor)) {
            throw new InvalidDataException(message: "The file has no BESS footer.");
        }

        var name = string.Empty;
        byte[]? core = null;
        byte[]? mbc = null;
        var sawCore = false;
        var sawEnd = false;
        var end = (file.Length - Bess.FooterLength);

        while (cursor < end) {
            // BESS spec (END block): "Naturally, it must be the last block." Once END has been read, ANY further
            // block — well-formed or not — violates that, so this is checked before the next block is even parsed.
            if (sawEnd) {
                throw new InvalidDataException(message: "a BESS block follows the END block; the spec requires END to be the last block.");
            }

            if (!Bess.TryReadBlock(file: file, offset: cursor, end: end, tag: out var tag, payload: out var payload, next: out var next)) {
                throw new InvalidDataException(message: $"a BESS block at file offset {cursor} is truncated or extends past the file.");
            }

            switch (tag) {
                case "NAME": name = System.Text.Encoding.ASCII.GetString(bytes: payload); break;
                case "CORE":
                    // (H-11) BESS spec (Validation and Failures): "Duplicate CORE block" is listed among SameBoy's
                    // own fatal conditions.
                    if (sawCore) {
                        throw new InvalidDataException(message: "the file has more than one CORE block; the spec makes a duplicate CORE block fatal.");
                    }

                    core = payload.ToArray();
                    sawCore = true;

                    break;
                case "MBC ":
                    // (H-11) BESS spec (Validation and Failures): "A known block, other than NAME, appearing before
                    // CORE" is a SameBoy fatal condition; the CORE block section itself grants the one further
                    // exemption this importer honors ("This block must be the first block, unless the NAME or INFO
                    // blocks exist then it must come directly after them"). NAME is handled above unconditionally,
                    // and INFO — like every tag this importer assigns no dedicated meaning to — falls to the default
                    // case below and stays unconditionally ignorable ("An implementation should not enforce block
                    // order on blocks unknown to it for future compatibility"). MBC is the one other block type this
                    // importer DOES interpret, so it is the one gated on CORE having already been seen.
                    if (!sawCore) {
                        throw new InvalidDataException(message: "an MBC block was encountered before the required CORE block.");
                    }

                    mbc = payload.ToArray();
                    ValidateMbcBlock(mbc: mbc);

                    break;
                case "END ":
                    // BESS spec (END block): "The length of the END block must be 0" — also listed among SameBoy's
                    // own fatal conditions ("An END block with non-zero length").
                    if (payload.Length != 0) {
                        throw new InvalidDataException(message: $"the END block has a {payload.Length}-byte payload; the spec requires 0.");
                    }

                    sawEnd = true;

                    break;
                default: break; // INFO and any unsupported block: ignored per spec ("should be completely ignored").
            }

            cursor = next;
        }

        if (core is null) {
            throw new InvalidDataException(message: "The file has no CORE block.");
        }

        // Required per the BESS spec: "Naturally, it must be the last block" and a missing END block is an
        // irrecoverable structural error — a CORE graph that merely reaches the footer without one is not accepted.
        if (!sawEnd) {
            throw new InvalidDataException(message: "The file has no required END block.");
        }

        // (M-10) BESS spec (CORE block): "The length of the CORE block is 0xD0 bytes, but implementations are
        // expected to ignore any excess bytes." Only a payload SHORTER than the defined prefix is rejected; a longer
        // one is a legal forward-compatible file from a newer minor revision. Only the defined prefix is kept from
        // here on — every field-offset read below (including the version check immediately following) stays within
        // it, so the tail is truly ignored rather than merely unread.
        if (core.Length < Bess.CoreBlockLength) {
            throw new InvalidDataException(message: $"the CORE block is {core.Length} bytes; the spec requires at least {Bess.CoreBlockLength}.");
        }

        core = core.AsSpan(start: 0, length: Bess.CoreBlockLength).ToArray();

        // (H-10) BESS spec (CORE block): "0x00 | Major BESS version as a 16-bit integer" and "Both major and minor
        // versions should be 1. Implementations are expected to reject incompatible majors, but still attempt to
        // read newer minor versions." The minor is deliberately never read as a compatibility gate.
        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(source: core.AsSpan(start: 0x00));

        if (majorVersion != Bess.SupportedCoreMajorVersion) {
            throw new InvalidDataException(message: $"the CORE block declares BESS major version {majorVersion}; only major version {Bess.SupportedCoreMajorVersion} is supported.");
        }

        // Palette RAM exists only on a color-capable destination (DMG has none); the spec's own "sizes must be 0 for
        // models prior to Game Boy Color" gives a DMG destination zero capacity here, so any nonzero palette entry is
        // rejected the same way an oversized one is.
        var paletteCapacity = (model.SupportsColor() ? 0x40 : 0); // BESS spec: palette size "must be 0 or 0x40".

        // Work-RAM/video-RAM/OAM/high-RAM capacities are the fixed CPU-visible bus windows ApplyBuffer writes through
        // (start, start+1, ...) — the same extents BessScope captures on export — regardless of DMG/CGB/AGB, since a
        // banked region beyond that window is not reachable through this sequential bus-write path at all.
        //
        // Work-RAM and video-RAM have no fixed size in the spec's buffer table (unlike OAM/HRAM/palette below, whose
        // rows carry an explicit "=" size) — only an upper bound applies here; a smaller-than-capacity size is legal
        // and gets the spec's own graceful handling ("if a too small VRAM size is specified... set that extra bank to
        // all zeros") at apply time in ApplyBuffer, not rejected here.
        ValidateBufferTable(core: core, tableOffset: 0x00, fileLength: file.Length, name: "work-RAM", destinationCapacity: ((MemoryMap.WorkRamBankNEnd - MemoryMap.WorkRamBank0Start) + 1));
        ValidateBufferTable(core: core, tableOffset: 0x08, fileLength: file.Length, name: "video-RAM", destinationCapacity: ((MemoryMap.VideoRamEnd - MemoryMap.VideoRamStart) + 1));
        // MBC-RAM has no destination-capacity check: ICartridge.ImportExternalRam already clamps to the cartridge's own
        // RAM size (Math.Min), matching the spec's explicit "too large MBC RAM size... the superfluous data should be
        // ignored" — there is no unchecked-index write here to guard.
        ValidateBufferTable(core: core, tableOffset: 0x10, fileLength: file.Length, name: "MBC-RAM");
        // OAM and HRAM carry a fixed spec size ("=0xA0", "=0x7F") with no "or 0" escape (unlike palette below), so
        // anything but the exact capacity is rejected.
        ValidateBufferTable(core: core, tableOffset: 0x18, fileLength: file.Length, name: "OAM", destinationCapacity: ((MemoryMap.ObjectAttributeMemoryEnd - MemoryMap.ObjectAttributeMemoryStart) + 1), shape: BufferSizeShape.Exact);
        ValidateBufferTable(core: core, tableOffset: 0x20, fileLength: file.Length, name: "high-RAM", destinationCapacity: ((MemoryMap.HighRamEnd - MemoryMap.HighRamStart) + 1), shape: BufferSizeShape.Exact);
        // Palette carries a fixed spec size too, but with the explicit "or 0" escape ("=0x40 or 0") — a DMG
        // destination's capacity is already 0, so its two allowed sizes collapse to just 0.
        ValidateBufferTable(core: core, tableOffset: 0x28, fileLength: file.Length, name: "background-palette", destinationCapacity: paletteCapacity, shape: BufferSizeShape.ExactOrZero);
        ValidateBufferTable(core: core, tableOffset: 0x30, fileLength: file.Length, name: "object-palette", destinationCapacity: paletteCapacity, shape: BufferSizeShape.ExactOrZero);

        return (name, core, mbc);
    }
    // BESS spec (MBC block): "The length of this block is variable and must be divisible by 3" and "Values outside
    // the 0x0000-0x7FFF and 0xA000-0xBFFF ranges are not allowed" — both also listed among SameBoy's own fatal
    // conditions ("An invalid length of MBC (not a multiple of 3)", "A write outside the $0000-$7FFF and
    // $A000-$BFFF ranges in the MBC block"). Called from the pure parse pass in ParseAndValidate as soon as an
    // "MBC " block's payload is read, so a violation is rejected before CORE, register, or buffer state is applied.
    private static void ValidateMbcBlock(byte[] mbc) {
        if ((mbc.Length % 3) != 0) {
            throw new InvalidDataException(message: $"the MBC block is {mbc.Length} bytes; the spec requires a length divisible by 3.");
        }

        for (var offset = 0; (offset < mbc.Length); offset += 3) {
            var address = (ushort)(mbc[offset] | (mbc[offset + 1] << 8));

            if (!(((address >= 0x0000) && (address <= 0x7FFF)) || ((address >= 0xA000) && (address <= 0xBFFF)))) {
                throw new InvalidDataException(message: $"the MBC block record at index {(offset / 3)} writes address 0x{address:X4}, outside the spec's 0x0000-0x7FFF and 0xA000-0xBFFF ranges.");
            }
        }
    }
    // The permitted shape of a CORE buffer-table entry's declared size against its destination region's capacity —
    // the BESS spec gives some regions (OAM, HRAM, palette) a fixed size instead of the plain upper bound the rest get.
    private enum BufferSizeShape {
        /// <summary>Any size from 0 up to the destination capacity (work-RAM, video-RAM): the spec sets no fixed
        /// size for these, and a short one is the spec's own "handle size mismatches gracefully" case.</summary>
        Range,
        /// <summary>Exactly the destination capacity, no smaller (OAM "=0xA0", HRAM "=0x7F").</summary>
        Exact,
        /// <summary>Exactly the destination capacity, or exactly 0 (palette "=0x40 or 0").</summary>
        ExactOrZero,
    }
    // One CORE buffer-table entry (a size/file-offset UInt32 pair at Bess.BufferTableOffset+tableOffset) must reference
    // a span that fits entirely within the file, and — when destinationCapacity is given — must match its destination
    // region's permitted size shape; ApplyBuffer/ApplyMbcRam/ApplyPalette trust both once validation has returned, so
    // every entry the importer reads must be checked here first.
    private static void ValidateBufferTable(byte[] core, int tableOffset, int fileLength, string name, int? destinationCapacity = null, BufferSizeShape shape = BufferSizeShape.Range) {
        var absolute = (Bess.BufferTableOffset + tableOffset);
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: core.AsSpan(start: absolute));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: core.AsSpan(start: (absolute + 4)));

        if ((size < 0) || (offset < 0) || (offset > (fileLength - size))) {
            throw new InvalidDataException(message: $"the {name} buffer table entry references {size} bytes at file offset {offset}, outside the file's {fileLength} bytes.");
        }

        if (!destinationCapacity.HasValue) {
            return;
        }

        var capacity = destinationCapacity.Value;

        if (size > capacity) {
            throw new InvalidDataException(message: $"the {name} buffer table entry declares {size} bytes, exceeding its destination region's {capacity}-byte capacity.");
        }

        var shapeValid = shape switch {
            BufferSizeShape.Exact => (size == capacity),
            BufferSizeShape.ExactOrZero => ((size == 0) || (size == capacity)),
            _ => true,
        };

        if (!shapeValid) {
            throw new InvalidDataException(message: $"the {name} buffer table entry declares {size} bytes; the spec requires exactly {capacity}{(shape == BufferSizeShape.ExactOrZero ? " or 0" : string.Empty)}.");
        }
    }
    // Splices the double-speed flag via Key1Component's own SaveState/LoadState round trip (its published field order:
    // armed:bool, isDoubleSpeed:bool, stopped:bool, ...) — everything but that one byte is read back unchanged.
    private static void RestoreDoubleSpeed(Key1Component key1, bool isDoubleSpeed) {
        var writer = new StateWriter();

        key1.SaveState(writer: writer);

        var bytes = writer.ToArray();

        bytes[1] = (byte)(isDoubleSpeed ? 1 : 0); // armed:bool is byte 0; isDoubleSpeed:bool is byte 1.

        key1.LoadState(reader: new StateReader(buffer: bytes));
    }
    // Splices the divider's high byte via TimerComponent's own SaveState/LoadState round trip (its published field
    // order: counter:u16, tima:u8, tma:u8, tac:u8, lastTimaInput:bool, overflowCountdown:i32, reloadedThisCycle:bool,
    // stopLatched:bool, switchBlockLatched:bool) — everything but the counter's high byte is read back unchanged, so
    // only the divider moves.
    private static void RestoreDivider(TimerComponent timer, byte dividerByte) {
        var writer = new StateWriter();

        timer.SaveState(writer: writer);

        var bytes = writer.ToArray();

        bytes[1] = dividerByte; // counter is little-endian u16 at [0..1]; DIV is its high byte.

        timer.LoadState(reader: new StateReader(buffer: bytes));
    }
    // fillCapacity: the destination region's fixed size, for a region whose buffer-table entry is allowed to declare
    // fewer bytes than that (work-RAM, video-RAM — BufferSizeShape.Range). BESS spec: "if a too small VRAM size is
    // specified... the implementation is expected to set that extra bank to all zeros" — applied here to any
    // undersized Range region so the destination never retains the machine's prior contents past the imported span.
    // Regions validated as BufferSizeShape.Exact (OAM, HRAM) can never reach this method undersized, so they pass
    // null and skip the fill loop entirely.
    private static void ApplyBuffer(ISystemBus bus, byte[] core, int tableOffset, ushort start, byte[] file, int? fillCapacity = null) {
        var absolute = (Bess.BufferTableOffset + tableOffset);
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: core.AsSpan(start: absolute));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: core.AsSpan(start: (absolute + 4)));

        for (var index = 0; (index < size); ++index) {
            bus.WriteByte(address: (ushort)(start + index), value: file[offset + index]);
        }

        for (var index = size; (fillCapacity.HasValue && (index < fillCapacity.Value)); ++index) {
            bus.WriteByte(address: (ushort)(start + index), value: 0);
        }
    }
    private static void ApplyMbcRam(ICartridge cartridge, byte[] core, byte[] file) {
        var absolute = (Bess.BufferTableOffset + 0x10);
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: core.AsSpan(start: absolute));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: core.AsSpan(start: (absolute + 4)));

        if (size > 0) {
            cartridge.ImportExternalRam(source: file.AsSpan(start: offset, length: size));
        }
    }
    private static void ApplyPalette(ISystemBus bus, byte[] core, int tableOffset, (ushort Index, ushort Data) registers, byte indexRegisterValue, byte[] file) {
        var absolute = (Bess.BufferTableOffset + tableOffset);
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: core.AsSpan(start: absolute));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: core.AsSpan(start: (absolute + 4)));

        if (size > 0) {
            BessScope.WriteColorPalette(bus: bus, registers: registers, finalIndexRegister: indexRegisterValue, palette: file.AsSpan(start: offset, length: size));
        }
    }
}
