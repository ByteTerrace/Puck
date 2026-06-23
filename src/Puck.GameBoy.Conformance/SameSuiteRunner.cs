namespace Puck.GameBoy.Conformance;

/// <summary>
/// Runs LIJI32's SameSuite test ROMs. Like mooneye, each test signals completion by executing <c>LD B,B</c> (the
/// magic breakpoint) with the registers holding a Fibonacci fingerprint — <c>B=3, C=5, D=8, E=13, H=21, L=34</c> —
/// on success, or <c>0x42</c> in every register on failure. The runner steps a <see cref="GameBoyMachine"/> from
/// its post-boot state until the breakpoint appears, then reads the registers. SameSuite carries no model suffix in
/// its filenames, so every ROM is run; the many Game Boy Color-only tests are expected to fail on this DMG build and
/// are reported grouped under their subdirectory.
/// </summary>
internal static class SameSuiteRunner {
    private const long InstructionBudget = 20_000_000;
    private const byte MagicBreakpoint = 0x40;

    public static (int Passed, int Failed) Run(string root, TextWriter output) {
        if (!Directory.Exists(path: root)) {
            output.WriteLine(value: $"  (SameSuite directory not found: {root})");

            return (0, 0);
        }

        var passed = 0;
        var failed = 0;
        var roms = Directory
            .GetFiles(path: root, searchPattern: "*.gb", searchOption: SearchOption.AllDirectories)
            .OrderBy(keySelector: static path => path, comparer: StringComparer.Ordinal);

        var dump = (Environment.GetEnvironmentVariable(variable: "PUCK_SS_DUMP") is not null);

        foreach (var rom in roms) {
            var (ok, detail) = RunOne(romPath: rom, dump: dump, output: output);
            var label = Path.GetRelativePath(relativeTo: root, path: rom).Replace(oldChar: '\\', newChar: '/');

            if (ok) {
                passed += 1;

                output.WriteLine(value: $"  PASS  {label}");
            }
            else {
                failed += 1;

                output.WriteLine(value: $"  FAIL  {label}: {detail}");
            }
        }

        return (passed, failed);
    }

    private static (bool Ok, string Detail) RunOne(string romPath, bool dump = false, TextWriter? output = null) {
        var rom = File.ReadAllBytes(path: romPath);
        ICartridge cartridge;

        try {
            cartridge = Cartridge.Load(rom: rom);
        }
        catch (NotSupportedException) {
            return (false, "mapper not implemented");
        }

        // The cartridge header's CGB flag (byte 0x143, bit 7) selects the console: SameSuite's tests set it when they
        // exercise Color hardware, so run those in CGB mode. The DMG-only tests leave it clear.
        var model = (((rom.Length > 0x143) && ((rom[0x143] & 0x80) != 0))
            ? ConsoleModel.Cgb
            : ConsoleModel.Dmg);
        var machine = new GameBoyMachine(model: model, cartridge: cartridge);

        for (var step = 0L; step < InstructionBudget; step += 1) {
            if (machine.Bus.ReadByte(address: machine.Cpu.ProgramCounter) == MagicBreakpoint) {
                var cpu = machine.Cpu;
                var fingerprint = (
                    (cpu.B == 3) && (cpu.C == 5) && (cpu.D == 8) &&
                    (cpu.E == 13) && (cpu.H == 21) && (cpu.L == 34)
                );

                if (dump && (output is not null)) {
                    // The test wrote its actual per-subtest results starting at RESULTS_START (0xC000); dump them so a
                    // calibration script can diff against the CorrectResults table parsed from the test's .asm.
                    var builder = new System.Text.StringBuilder(value: "DUMP ");

                    for (var i = 0; i < 160; i += 1) {
                        builder.Append(value: machine.Bus.ReadByte(address: (ushort)(0xC000 + i)).ToString(format: "X2"));
                    }

                    output.WriteLine(value: builder.ToString());
                }

                return (fingerprint
                    ? (true, string.Empty)
                    : (false, $"regs B={cpu.B} C={cpu.C} D={cpu.D} E={cpu.E} H={cpu.H} L={cpu.L}"));
            }

            machine.Step();
        }

        return (false, "timeout (no breakpoint reached)");
    }
}
