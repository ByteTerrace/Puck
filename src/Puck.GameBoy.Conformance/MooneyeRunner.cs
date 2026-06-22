namespace Puck.GameBoy.Conformance;

/// <summary>
/// Runs Gekkio's mooneye acceptance test ROMs. Each test signals completion by executing <c>LD B,B</c> (the
/// magic breakpoint) with the registers holding a Fibonacci fingerprint — <c>B=3, C=5, D=8, E=13, H=21,
/// L=34</c> on success. The runner steps a <see cref="GameBoyMachine"/> from its post-boot state until the
/// breakpoint opcode appears at the program counter, then reads the registers. Tests whose filename carries a
/// non-DMG model suffix are skipped, since this build targets the DMG.
/// </summary>
internal static class MooneyeRunner {
    private const long InstructionBudget = 5_000_000;
    private const byte MagicBreakpoint = 0x40;

    private static readonly HashSet<string> NonDmgModelSuffixes = new(comparer: StringComparer.Ordinal) {
        "dmg0",
        "mgb",
        "S",
        "sgb",
        "sgb2",
        "C",
        "cgb0",
        "A",
    };

    public static (int Passed, int Failed, int Skipped) RunAcceptance(string assetRoot, TextWriter output) {
        var acceptanceRoot = Path.Combine(path1: assetRoot, path2: "acceptance");

        if (!Directory.Exists(path: acceptanceRoot)) {
            output.WriteLine(value: $"  (no acceptance/ directory under {assetRoot})");

            return (0, 0, 0);
        }

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var roms = Directory
            .GetFiles(path: acceptanceRoot, searchPattern: "*.gb", searchOption: SearchOption.AllDirectories)
            .OrderBy(keySelector: static path => path, comparer: StringComparer.Ordinal);

        foreach (var rom in roms) {
            if (!IsDmgApplicable(name: Path.GetFileNameWithoutExtension(path: rom))) {
                skipped += 1;

                continue;
            }

            var (ok, detail) = RunOne(romPath: rom);
            var label = Path.GetRelativePath(relativeTo: acceptanceRoot, path: rom);

            if (ok) {
                passed += 1;
            }
            else {
                failed += 1;

                output.WriteLine(value: $"  FAIL  {label}: {detail}");
            }
        }

        return (passed, failed, skipped);
    }

    private static bool IsDmgApplicable(string name) {
        var dash = name.LastIndexOf(value: '-');

        return ((dash < 0) || !NonDmgModelSuffixes.Contains(item: name[(dash + 1)..]));
    }

    private static (bool Ok, string Detail) RunOne(string romPath) {
        var rom = File.ReadAllBytes(path: romPath);
        ICartridge cartridge;

        try {
            cartridge = Cartridge.Load(rom: rom);
        }
        catch (NotSupportedException) {
            return (false, "mapper not implemented");
        }

        var machine = new GameBoyMachine(model: ConsoleModel.Dmg, cartridge: cartridge);

        for (var step = 0L; step < InstructionBudget; step += 1) {
            if (machine.Bus.ReadByte(address: machine.Cpu.ProgramCounter) == MagicBreakpoint) {
                var cpu = machine.Cpu;
                var fingerprint = (
                    (cpu.B == 3) && (cpu.C == 5) && (cpu.D == 8) &&
                    (cpu.E == 13) && (cpu.H == 21) && (cpu.L == 34)
                );

                return (fingerprint
                    ? (true, string.Empty)
                    : (false, $"regs B={cpu.B} C={cpu.C} D={cpu.D} E={cpu.E} H={cpu.H} L={cpu.L}"));
            }

            machine.Step();
        }

        return (false, "timeout (no breakpoint reached)");
    }
}
