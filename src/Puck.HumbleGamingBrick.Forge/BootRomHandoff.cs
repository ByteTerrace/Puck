using Puck.HumbleGamingBrick.Interfaces;
using MachineInstance = Puck.GamingBricks.MachineInstance<Puck.HumbleGamingBrick.Machine, Puck.HumbleGamingBrick.MachineConfiguration>;

namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// Reads the machine state a cartridge can observe at <c>0x0100</c>: the processor's register file, the divider
/// counter, every readable high-page register, the interrupt-enable register, and Color palette RAM. It is the surface
/// a boot program and the seeded post-boot handoff have to agree on, captured as a named field list so a comparison can
/// name the first field that differs rather than a byte offset.
/// </summary>
public static class BootRomHandoff {
    // The high-page registers whose value depends on the palette index register rather than on the handoff, read
    // through their index instead.
    private const ushort BackgroundPaletteData = 0xFF69;
    private const ushort ObjectPaletteData = 0xFF6B;
    private const int PaletteRamSize = 64;

    /// <summary>The instruction budget a boot is allowed before it is declared wedged: several times the longest boot,
    /// which is the mark's scroll plus the divider delay its header asks for.</summary>
    public const int DefaultInstructionCeiling = 4_000_000;

    private static MachineInstance Create(ConsoleModel model, byte[] rom, byte[]? bootRom) =>
        MachineFactory.Create(
        configuration: new MachineConfiguration(
            bootRom: bootRom,
            cartridgeRom: rom,
            model: model
        ),
        compose: static services => services.AddHumbleGamingBrickComponents()
    );

    /// <summary>Boots one cartridge through an authored image and against the seeded post-boot handoff, and names the
    /// first observable field the two disagree on.</summary>
    /// <param name="model">The revision to emulate.</param>
    /// <param name="bootRom">The authored boot image.</param>
    /// <param name="rom">The cartridge ROM to boot.</param>
    /// <param name="instructionCeiling">The instruction budget before the boot is declared wedged.</param>
    /// <returns>A description of the first difference, or <see langword="null"/> when the two agree.</returns>
    public static string? Compare(ConsoleModel model, byte[] bootRom, byte[] rom, int instructionCeiling = DefaultInstructionCeiling) {
        // The seeded first-line handoff carries a comparison latch the running processor cannot hold.
        var maskCoincidence = (!model.SupportsColor() && (model != ConsoleModel.Dmg0));

        using var seeded = Create(
            bootRom: null,
            model: model,
            rom: rom
        );
        using var booted = Create(
            bootRom: bootRom,
            model: model,
            rom: rom
        );

        if (!TryRunToHandoff(
            instance: booted,
            instructionCeiling: instructionCeiling
        )) {
            return $"the boot program never unmapped itself within {instructionCeiling} instructions";
        }

        return FirstDifference(
            actual: Capture(
                instance: booted,
                maskCoincidence: maskCoincidence,
                model: model
            ),
            expected: Capture(
                instance: seeded,
                maskCoincidence: maskCoincidence,
                model: model
            )
        );
    }
    /// <summary>Steps a machine until its boot program unmaps itself, leaving it at the cartridge's entry point.</summary>
    /// <param name="instance">The machine, configured with a boot ROM.</param>
    /// <param name="instructionCeiling">The instruction budget before the boot is declared wedged.</param>
    /// <returns><see langword="true"/> when the machine reached its handoff.</returns>
    public static bool TryRunToHandoff(MachineInstance instance, int instructionCeiling) {
        var bus = instance.GetRequiredService<ISystemBus>();

        for (var step = 0; (step < instructionCeiling); ++step) {
            if ((bus.ReadByte(address: MemoryMap.BootRomDisable) & 0x01) != 0) {
                return true;
            }

            instance.Machine.StepInstruction();
        }

        return false;
    }
    /// <summary>Captures the observable handoff surface of a machine.</summary>
    /// <param name="instance">The machine to read.</param>
    /// <param name="model">The revision it emulates, which selects the Color-only fields.</param>
    /// <param name="maskCoincidence">Whether to clear the LY-comparison bit of the status register before comparing.
    /// The seeded handoff of a revision that parks on the first line carries the comparison latch clear while its LY
    /// and LYC are both zero, which the running picture processor cannot hold: it recomputes the latch every dot.</param>
    /// <returns>The captured fields, in a stable order.</returns>
    public static List<(string Name, int Value)> Capture(MachineInstance instance, ConsoleModel model, bool maskCoincidence) {
        var bus = instance.GetRequiredService<ISystemBus>();
        var cpu = instance.GetRequiredService<ICpu>();
        var fields = new List<(string Name, int Value)>(capacity: 256) {
            ("cpu.a", cpu.A),
            ("cpu.f", cpu.F),
            ("cpu.b", cpu.B),
            ("cpu.c", cpu.C),
            ("cpu.d", cpu.D),
            ("cpu.e", cpu.E),
            ("cpu.h", cpu.H),
            ("cpu.l", cpu.L),
            ("cpu.sp", cpu.StackPointer),
            ("cpu.pc", cpu.ProgramCounter),
            ("timer.divCounter", instance.GetRequiredService<Interfaces.ITimer>().DivCounter),
        };

        for (var address = MemoryMap.IoRegistersStart; (address <= MemoryMap.IoRegistersEnd); ++address) {
            if (
                (address == BackgroundPaletteData) ||
                (address == ObjectPaletteData)
            ) {
                continue;
            }

            var value = ((int)bus.ReadByte(address: address));

            if (
                maskCoincidence &&
                (address == MemoryMap.LcdStatus)
            ) {
                value &= ~0x04;
            }

            fields.Add(item: (RegisterName(address: address), value));
        }

        fields.Add(item: ("io.ie", bus.ReadByte(address: MemoryMap.InterruptEnable)));

        if (!model.SupportsColor()) {
            return fields;
        }

        var backgroundIndex = bus.ReadByte(address: MemoryMap.BackgroundColorPaletteIndex);
        var objectIndex = bus.ReadByte(address: MemoryMap.ObjectColorPaletteIndex);

        for (var slot = 0; (slot < PaletteRamSize); ++slot) {
            bus.WriteByte(
                address: MemoryMap.BackgroundColorPaletteIndex,
                value: ((byte)slot)
            );
            fields.Add(item: ($"palette.background[{slot}]", bus.ReadByte(address: BackgroundPaletteData)));
            bus.WriteByte(
                address: MemoryMap.ObjectColorPaletteIndex,
                value: ((byte)slot)
            );
            fields.Add(item: ($"palette.object[{slot}]", bus.ReadByte(address: ObjectPaletteData)));
        }

        // The index registers are the probe's own scratch; put back what the handoff left so the captured pair is the
        // machine's state and not the probe's.
        bus.WriteByte(
            address: MemoryMap.BackgroundColorPaletteIndex,
            value: ((byte)(backgroundIndex & 0xBF))
        );
        bus.WriteByte(
            address: MemoryMap.ObjectColorPaletteIndex,
            value: ((byte)(objectIndex & 0xBF))
        );

        return fields;
    }
    /// <summary>Returns the first field whose value differs between two captures, or <see langword="null"/> when they
    /// agree.</summary>
    /// <param name="expected">The seeded machine's capture.</param>
    /// <param name="actual">The booted machine's capture.</param>
    /// <returns>A description of the first difference.</returns>
    public static string? FirstDifference(List<(string Name, int Value)> expected, List<(string Name, int Value)> actual) {
        if (expected.Count != actual.Count) {
            return $"captured {actual.Count} fields against {expected.Count}";
        }

        for (var index = 0; (index < expected.Count); ++index) {
            if (expected[index].Value != actual[index].Value) {
                return $"{expected[index].Name} seeded 0x{expected[index].Value:X2}, booted 0x{actual[index].Value:X2}";
            }
        }

        return null;
    }

    private static string RegisterName(ushort address) =>
        address switch {
            MemoryMap.Joypad => "joypad.p1",
            MemoryMap.SerialData => "serial.sb",
            MemoryMap.SerialControl => "serial.sc",
            MemoryMap.Divider => "timer.div",
            MemoryMap.TimerCounter => "timer.tima",
            MemoryMap.TimerModulo => "timer.tma",
            MemoryMap.TimerControl => "timer.tac",
            MemoryMap.InterruptFlag => "interrupts.if",
            MemoryMap.LcdControl => "ppu.lcdc",
            MemoryMap.LcdStatus => "ppu.stat",
            MemoryMap.ScrollY => "ppu.scy",
            MemoryMap.ScrollX => "ppu.scx",
            MemoryMap.LcdY => "ppu.ly",
            MemoryMap.LcdYCompare => "ppu.lyc",
            MemoryMap.OamDmaSource => "oamDma.dma",
            MemoryMap.BackgroundPalette => "ppu.bgp",
            MemoryMap.ObjectPalette0 => "ppu.obp0",
            MemoryMap.ObjectPalette1 => "ppu.obp1",
            MemoryMap.WindowY => "ppu.wy",
            MemoryMap.WindowX => "ppu.wx",
            MemoryMap.SystemModeSelect => "key0",
            MemoryMap.SpeedSwitch => "key1",
            MemoryMap.VramBankSelect => "vbk",
            MemoryMap.BootRomDisable => "bank",
            MemoryMap.HdmaSourceHigh => "hdma1",
            MemoryMap.HdmaSourceLow => "hdma2",
            MemoryMap.HdmaDestinationHigh => "hdma3",
            MemoryMap.HdmaDestinationLow => "hdma4",
            MemoryMap.HdmaControl => "hdma5",
            MemoryMap.InfraredPort => "rp",
            MemoryMap.BackgroundColorPaletteIndex => "ppu.bcps",
            MemoryMap.ObjectColorPaletteIndex => "ppu.ocps",
            MemoryMap.WorkRamBankSelect => "svbk",
            _ => ((address >= MemoryMap.AudioStart) && (address <= MemoryMap.WaveRamEnd))
                ? $"apu.{address:X4}"
                : $"io.{address:X4}",
        };
}
