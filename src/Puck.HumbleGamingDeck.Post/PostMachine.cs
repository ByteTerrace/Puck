using Puck.Machines;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>One board the replay stages run: its name, its original fixture image, and how far to run it before the
/// first capture so the captures land inside the board's own activity.</summary>
/// <param name="Name">The board's name, as a stage reports it.</param>
/// <param name="Image">The fixture image.</param>
/// <param name="Prime">Runs a freshly built machine to the fixture's capture window.</param>
internal sealed record PostFixture(string Name, byte[] Image, Action<HgdMachine> Prime);

/// <summary>Builds the original CPU and board fixtures and supplies machine probes with master-tick budgets.</summary>
internal static class PostMachine {
    /// <summary>The number of master ticks in one probe batch.</summary>
    public const ulong BatchTicks = 120_000;
    /// <summary>The master ticks in one NTSC frame with rendering enabled, rounded down.</summary>
    public const ulong FrameTicks = 357_366;

    // Opcodes, by mnemonic and addressing mode.
    private const byte AndImmediate = 0x29;
    private const byte BitAbsolute = 0x2C;
    private const byte Bpl = 0x10;
    private const byte Cld = 0xD8;
    private const byte Cli = 0x58;
    private const byte EorImmediate = 0x49;
    private const byte IncZeroPage = 0xE6;
    private const byte Inx = 0xE8;
    private const byte Jmp = 0x4C;
    private const byte Jsr = 0x20;
    private const byte LdaAbsolute = 0xAD;
    private const byte LdaImmediate = 0xA9;
    private const byte LdaZeroPage = 0xA5;
    private const byte LdxImmediate = 0xA2;
    private const byte LsrAccumulator = 0x4A;
    private const byte OraImmediate = 0x09;
    private const byte Pha = 0x48;
    private const byte Pla = 0x68;
    private const byte Rti = 0x40;
    private const byte Rts = 0x60;
    private const byte Sei = 0x78;
    private const byte StaAbsolute = 0x8D;
    private const byte StaAbsoluteX = 0x9D;
    private const byte Txa = 0x8A;
    private const byte Txs = 0x9A;

    /// <summary>Gets the fixtures the replay stages run, one per implemented board.</summary>
    public static IReadOnlyList<PostFixture> Fixtures { get; } = [
        new(Name: "NROM", Image: CreateImage(), Prime: static _ => { }),
        new(Name: "MMC1", Image: CreateMmc1Image(), Prime: static machine => machine.RunCycles(masterTicks: (3 * FrameTicks))),
        new(Name: "MMC3", Image: CreateMmc3Image(), Prime: PrimeMmc3),
    ];

    /// <summary>Creates an NROM image containing an original register and RAM loop.</summary>
    /// <returns>The complete image with an NES 2.0 header and reset/interrupt vectors.</returns>
    public static byte[] CreateImage() {
        var image = new byte[(16 + 16384)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = 1;
        image[7] = 8;
        image[10] = 7;
        image[11] = 7;
        // The loop returns to INX at $8002; the vectors below enter the initial LDX at $8000.
        ReadOnlySpan<byte> program = [0xA2, 0x00, 0xE8, 0x86, 0x00, 0xEE, 0x00, 0x60, 0x4C, 0x02, 0x80];

        program.CopyTo(destination: image.AsSpan(start: 16));
        for (var vector = 0x3FFA; (vector <= 0x3FFE); vector += 2) {
            image[(16 + vector)] = 0;
            image[((16 + vector) + 1)] = 0x80;
        }

        return image;
    }
    /// <summary>Creates an MMC1 image with 64 KiB of PRG, 32 KiB of CHR and 8 KiB of battery-backed PRG-RAM whose loop,
    /// with rendering on, reloads all four registers through the serial port every pass (4 KiB CHR mode, PRG mode 3,
    /// mirroring alternating between vertical and horizontal) and copies the switched PRG bank's marker into PRG-RAM.</summary>
    /// <returns>The NES 2.0 image.</returns>
    public static byte[] CreateMmc1Image() {
        var program = Prologue();

        _ = program
            .Op(opcode: LdaImmediate, operand: ((byte)0x80)).Op(address: 0x8000, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x1F)).Jump(label: "control", opcode: Jsr)
            .Op(opcode: LdaImmediate, operand: ((byte)0x1E)).Op(address: 0x2001, opcode: StaAbsolute)
            .Op(opcode: LdxImmediate, operand: ((byte)0x00))
            .Label(name: "loop").Bytes(Inx)
            .Bytes(Txa).Op(opcode: AndImmediate, operand: ((byte)0x03)).Jump(label: "prg", opcode: Jsr)
            .Bytes(Txa).Jump(label: "chr0", opcode: Jsr)
            .Bytes(Txa).Op(opcode: EorImmediate, operand: ((byte)0xFF)).Jump(label: "chr1", opcode: Jsr)
            .Bytes(Txa).Op(opcode: AndImmediate, operand: ((byte)0x01)).Op(opcode: OraImmediate, operand: ((byte)0x1E)).Jump(label: "control", opcode: Jsr)
            .Op(address: 0x8000, opcode: LdaAbsolute).Op(address: 0x6000, opcode: StaAbsoluteX)
            .Jump(label: "loop", opcode: Jmp);
        SerialWrite(label: "control", program: program, register: 0x8000);
        SerialWrite(label: "chr0", program: program, register: 0xA000);
        SerialWrite(label: "chr1", program: program, register: 0xC000);
        SerialWrite(label: "prg", program: program, register: 0xE000);

        var chr = new byte[32768];

        for (var bank = 0; (bank < 8); ++bank) {
            chr.AsSpan(start: (bank * 4096), length: 4096).Fill(value: ((byte)(0x11 * (bank + 1))));
        }

        var image = program.Build(battery: true, chr: chr, mapper: 1, prgRomSize: 65536);

        for (var bank = 0; (bank < 3); ++bank) {
            image[(16 + (bank * 16384))] = ((byte)(0xB0 + bank));
        }

        return image;
    }
    /// <summary>Creates an MMC3 image with 64 KiB of PRG, 64 KiB of CHR and 8 KiB of PRG-RAM whose loop, with rendering
    /// on and sprites fetched from $1000, rewrites a bank register every pass, flipping the CHR inversion, while the
    /// scanline counter, latched at 1, interrupts every second line; the handler acknowledges and re-enables the IRQ and
    /// flips mirroring.</summary>
    /// <returns>The NES 2.0 image.</returns>
    public static byte[] CreateMmc3Image() {
        var program = Prologue();

        _ = program
            // The APU's frame-counter IRQ shares the CPU's IRQ line; inhibit it so the handler answers the board alone.
            .Op(opcode: LdaImmediate, operand: ((byte)0x40)).Op(address: 0x4017, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x80)).Op(address: 0xA001, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x08)).Op(address: 0x2000, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x1E)).Op(address: 0x2001, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x01)).Op(address: 0xC000, opcode: StaAbsolute)
            .Op(address: 0xC001, opcode: StaAbsolute).Op(address: 0xE001, opcode: StaAbsolute)
            .Bytes(Cli).Op(opcode: LdxImmediate, operand: ((byte)0x00))
            .Label(name: "loop").Bytes(Inx)
            // Bit 6 stays clear: PRG mode 0 keeps $C000-$FFFF, where this program runs, fixed.
            .Bytes(Txa).Op(opcode: AndImmediate, operand: ((byte)0x87)).Op(address: 0x8000, opcode: StaAbsolute)
            .Bytes(Txa).Op(address: 0x8001, opcode: StaAbsolute)
            .Op(address: 0x8000, opcode: LdaAbsolute).Op(address: 0x6000, opcode: StaAbsoluteX)
            .Jump(label: "loop", opcode: Jmp)
            .Label(name: "irq").Bytes(Pha)
            .Op(address: 0xE000, opcode: StaAbsolute).Op(address: 0xE001, opcode: StaAbsolute)
            .Op(opcode: IncZeroPage, operand: ((byte)0x10)).Op(opcode: LdaZeroPage, operand: ((byte)0x10))
            .Op(opcode: AndImmediate, operand: ((byte)0x01)).Op(address: 0xA000, opcode: StaAbsolute)
            .Bytes(Pla, Rti);

        var chr = new byte[65536];

        for (var bank = 0; (bank < 64); ++bank) {
            chr.AsSpan(start: (bank * 1024), length: 1024).Fill(value: ((byte)((bank * 37) + 1)));
        }

        var image = program.Build(chr: chr, mapper: 4, prgRomSize: 65536);

        for (var bank = 0; (bank < 6); ++bank) {
            image[(16 + (bank * 8192))] = ((byte)(0xC0 + bank));
        }

        return image;
    }
    /// <summary>Creates an independent machine over the original NROM fixture image.</summary>
    /// <returns>The machine instance owned and disposed by the probe.</returns>
    public static MachineInstance<HgdMachine, HgdMachineConfiguration> Build() =>
        Build(image: CreateImage());
    /// <summary>Creates an independent machine over an image.</summary>
    /// <param name="image">The cartridge image.</param>
    /// <returns>The machine instance owned and disposed by the probe.</returns>
    public static MachineInstance<HgdMachine, HgdMachineConfiguration> Build(byte[] image) =>
        HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(cartridge: HgdCartridge.Load(image: image)));
    /// <summary>Advances a probe's batch budget in master ticks.</summary>
    /// <param name="instance">The machine instance to advance.</param>
    /// <param name="count">The nonnegative number of batches.</param>
    /// <exception cref="OverflowException">The accumulated master-tick target exceeds the clock range.</exception>
    public static void RunBatches(MachineInstance<HgdMachine, HgdMachineConfiguration> instance, int count) {
        instance.Machine.RunCycles(masterTicks: (((ulong)count) * BatchTicks));
    }
    /// <summary>Locates the first differing component byte in two captures.</summary>
    /// <param name="first">The expected capture with a complete section table.</param>
    /// <param name="second">The actual capture.</param>
    /// <returns>The component and byte offset, or the identity/timestamp/length category when bytes agree.</returns>
    public static string Describe(HgdMachineSnapshot first, HgdMachineSnapshot second) {
        var length = Math.Min(val1: first.Size, val2: second.Size);

        for (var offset = 0; (offset < length); ++offset) {
            if (first.Data[offset] != second.Data[offset]) {
                var section = first.Sections.First(predicate: candidate => ((offset >= candidate.Offset) && (offset < (candidate.Offset + candidate.Length))));

                return $"{section.Name}+{(offset - section.Offset)}: {first.Data[offset]:X2} versus {second.Data[offset]:X2}";
            }
        }

        return "identity, timestamp, or image length differs";
    }

    // Reset at $C000: interrupts and decimal mode off, the stack at $01FF, then two vertical blanks for the PPU to warm
    // up.
    private static DeckProgram Prologue() =>
        new DeckProgram(origin: 0xC000)
            .Bytes(Sei, Cld).Op(opcode: LdxImmediate, operand: ((byte)0xFF)).Bytes(Txs)
            .Label(name: "warm-up-1").Op(address: 0x2002, opcode: BitAbsolute).Branch(label: "warm-up-1", opcode: Bpl)
            .Label(name: "warm-up-2").Op(address: 0x2002, opcode: BitAbsolute).Branch(label: "warm-up-2", opcode: Bpl);
    // Shifts A's low five bits, lowest first, into the MMC1 register at the given address.
    private static void SerialWrite(DeckProgram program, string label, ushort register) {
        _ = program.Label(name: label);
        for (var bit = 0; (bit < 5); ++bit) {
            _ = program.Op(address: register, opcode: StaAbsolute);
            if (bit < 4) {
                _ = program.Bytes(LsrAccumulator);
            }
        }
        _ = program.Bytes(Rts);
    }
    // Runs past the warm-up, then to the line clock that leaves the counter at 1 and 400 ticks beyond it, so the next
    // capture window holds the following clock, the IRQ it raises and the handler's first cycles.
    private static void PrimeMmc3(HgdMachine machine) {
        machine.RunCycles(masterTicks: (3 * FrameTicks));

        var mapper = ((HgdMmc3)machine.Bus.Mapper);

        for (var tick = 0UL; ((tick < FrameTicks) && (mapper.IrqCounter == 1)); ++tick) {
            machine.RunCycles(masterTicks: 1);
        }
        for (var tick = 0UL; ((tick < FrameTicks) && (mapper.IrqCounter != 1)); ++tick) {
            machine.RunCycles(masterTicks: 1);
        }
        machine.RunCycles(masterTicks: 400);
    }
}
