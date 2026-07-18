namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The BESS savestate diagnostics: <c>--bess-export</c> writes a BESS-compliant file and immediately proves the
/// export/import round trip is self-consistent (into a second, freshly built machine of the same configuration);
/// <c>--bess-import</c> loads a BESS file — ours or a foreign one — into a machine and reports the state it restored,
/// so states can be eyeballed against another BESS-compliant tool as evidence. Diagnostics, never gates.
/// <para>
/// M-08: every <c>--bess-export</c> run also feeds the shared <see cref="BessMalformedCorpus"/> into
/// <see cref="BessImporter.Import"/> against a dedicated probe machine, asserting each case is rejected with
/// <see cref="InvalidDataException"/> and leaves that machine's snapshot byte-for-byte unchanged. This is a
/// convenience re-run of the same corpus, not the gate — <see cref="BessImportGuardStage"/> is the always-run POST
/// stage that actually gates the validate-then-apply contract.
/// </para>
/// </summary>
internal static class BessDiagnostic {
    private const int DefaultExportFrames = 60;

    /// <summary>Dispatches <c>--bess-export</c>/<c>--bess-import</c>.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="exitCode">The exit code the handled mode produced.</param>
    /// <returns><see langword="true"/> when a BESS flag was handled.</returns>
    public static bool TryRun(string[] args, out int exitCode) {
        exitCode = 0;

        if (Array.IndexOf(array: args, value: "--bess-export") >= 0) {
            exitCode = RunExport(args: args);

            return true;
        }

        if (Array.IndexOf(array: args, value: "--bess-import") >= 0) {
            exitCode = RunImport(args: args);

            return true;
        }

        return false;
    }

    private static int RunExport(string[] args) {
        var outPath = ArgValue(args: args, name: "--bess-export");

        if (string.IsNullOrEmpty(value: outPath)) {
            Console.WriteLine(value: "  [SKIP] --bess-export: no output path given");

            return 2;
        }

        if (!TryResolveRom(args: args, rom: out var rom, romLabel: out var romLabel, model: out var model)) {
            return 2;
        }

        var framesArg = ArgValue(args: args, name: "--frames");
        var frames = (((framesArg is not null) && int.TryParse(s: framesArg, result: out var parsedFrames)) ? parsedFrames : DefaultExportFrames);

        using var source = PostMachine.Build(model: model, rom: rom);

        PostMachine.RunFrames(instance: source, frames: frames);

        var (file, exportedScope) = BessExporter.Export(instance: source, model: model);
        var exportedFingerprint = BessScope.Fingerprint(capture: exportedScope);

        var outputDirectory = Path.GetDirectoryName(path: Path.GetFullPath(path: outPath));

        if (!string.IsNullOrEmpty(value: outputDirectory)) {
            Directory.CreateDirectory(path: outputDirectory);
        }

        File.WriteAllBytes(path: outPath, bytes: file);

        // Self-consistency round trip: import the just-exported bytes into a SECOND, independently built machine of
        // the same configuration and re-capture the same scope. A matching fingerprint proves the export/import pair
        // round-trips the BESS-modeled state exactly (registers, IME/IE/execution-state, the memory regions, and CGB
        // palettes when applicable) — the evidence the task asks for, without depending on an external tool.
        using var target = PostMachine.Build(model: model, rom: rom);

        var report = BessImporter.Import(instance: target, file: file);
        var roundTripScope = BessScope.Capture(instance: target, model: model);
        var roundTripFingerprint = BessScope.Fingerprint(capture: roundTripScope);
        var roundTripVerdict = ((exportedFingerprint == roundTripFingerprint) ? "MATCH" : "MISMATCH");

        Console.WriteLine(value: $"  bess-export {romLabel} ({model}, {frames} frames) -> {outPath} ({file.Length:N0} bytes)");
        Console.WriteLine(value: $"    scope fingerprint 0x{exportedFingerprint:X16}; round-trip (export -> import -> re-capture) into a fresh machine: 0x{roundTripFingerprint:X16} [{roundTripVerdict}]");
        Console.WriteLine(value: $"    restored: pc={report.Pc:X4} af={report.Af:X4} bc={report.Bc:X4} de={report.De:X4} hl={report.Hl:X4} sp={report.Sp:X4} ime={(report.Ime ? 1 : 0)} ie={report.Ie:X2} if={report.If:X2} lcdc={report.Lcdc:X2} stat={report.Stat:X2} ly={report.Ly:X2} romBank={report.RomBank} ramBank={report.RamBank}");

        var malformedCorpusClean = RunMalformedImportSelfCheck(goodFile: file, rom: rom, model: model);

        PrintReferenceEmulatorNote();

        return (((roundTripVerdict == "MATCH") && malformedCorpusClean) ? 0 : 1);
    }
    // M-08 self-check: a malformed BESS file must be rejected before anything is applied. Each corpus case is imported
    // against ONE dedicated probe machine (never the export/import pair above, so a bug here cannot contaminate the
    // round-trip evidence); a snapshot taken before the whole run and after every attempt must stay byte-identical,
    // proving a rejected import is a true no-op, not merely a caught exception over already-mutated state.
    private static bool RunMalformedImportSelfCheck(byte[] goodFile, byte[] rom, ConsoleModel model) {
        using var probe = PostMachine.Build(model: model, rom: rom);
        var baseline = probe.Machine.Snapshot();
        var allRejectedCleanly = true;

        foreach (var (label, malformed) in BessMalformedCorpus.Build(goodFile: goodFile)) {
            InvalidDataException? rejection = null;

            try {
                BessImporter.Import(instance: probe, file: malformed);
            } catch (InvalidDataException exception) {
                rejection = exception;
            }

            if (rejection is null) {
                Console.WriteLine(value: $"    bess-import malformed-corpus \"{label}\": [FAIL] the import did not throw InvalidDataException");
                allRejectedCleanly = false;

                continue;
            }

            var untouched = baseline.ContentEquals(other: probe.Machine.Snapshot());

            Console.WriteLine(value: $"    bess-import malformed-corpus \"{label}\": rejected ({rejection.Message}); probe machine {(untouched ? "untouched" : "[FAIL] MUTATED")}");
            allRejectedCleanly &= untouched;
        }

        return allRejectedCleanly;
    }
    private static int RunImport(string[] args) {
        var filePath = ArgValue(args: args, name: "--bess-import");

        if (string.IsNullOrEmpty(value: filePath) || !File.Exists(path: filePath)) {
            Console.WriteLine(value: $"  [SKIP] --bess-import: file not found at {filePath}");

            return 2;
        }

        if (!TryResolveRom(args: args, rom: out var rom, romLabel: out var romLabel, model: out var model)) {
            return 2;
        }

        using var machine = PostMachine.Build(model: model, rom: rom);
        var report = BessImporter.Import(instance: machine, file: File.ReadAllBytes(path: filePath));

        Console.WriteLine(value: $"  bess-import {Path.GetFileName(path: filePath)} into {romLabel} ({model})");
        Console.WriteLine(value: $"    emulator=\"{report.EmulatorName}\" model={report.ModelTag}");
        Console.WriteLine(value: $"    pc={report.Pc:X4} af={report.Af:X4} bc={report.Bc:X4} de={report.De:X4} hl={report.Hl:X4} sp={report.Sp:X4} ime={(report.Ime ? 1 : 0)} ie={report.Ie:X2} if={report.If:X2} lcdc={report.Lcdc:X2} stat={report.Stat:X2} ly={report.Ly:X2} romBank={report.RomBank} ramBank={report.RamBank}");
        PrintReferenceEmulatorNote();

        return 0;
    }
    // --rom <path> selects the cartridge (model inferred from its header); absent, the synthetic Tier-A cartridge on
    // Dmg. Returns false (with a printed [SKIP]) when --rom names a missing file.
    private static bool TryResolveRom(string[] args, out byte[] rom, out string romLabel, out ConsoleModel model) {
        var romPath = ArgValue(args: args, name: "--rom");

        if (string.IsNullOrEmpty(value: romPath)) {
            rom = SyntheticRom.Create();
            romLabel = "synthetic";
            model = ConsoleModel.Dmg;

            return true;
        }

        if (!File.Exists(path: romPath)) {
            Console.WriteLine(value: $"  [SKIP] --rom not found at {romPath}");

            rom = [];
            romLabel = string.Empty;
            model = ConsoleModel.Dmg;

            return false;
        }

        rom = File.ReadAllBytes(path: romPath);
        romLabel = Path.GetFileName(path: romPath);
        model = (((rom.Length > 0x0143) && (0 != (rom[0x0143] & 0x80))) ? ConsoleModel.Cgb : ConsoleModel.Dmg);

        return true;
    }
    private static string? ArgValue(string[] args, string name) {
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return args[(index + 1)];
            }
        }

        return null;
    }
    // Prints a note that no headless cross-emulator round trip is available: a reference emulator's prebuilt tester
    // binary accepts only a ROM, boot ROM, battery save, and render target — no savestate-import flag — so a BESS file
    // cannot be handed to it headlessly. Printed rather than silently skipped, since it is evidence about the file's
    // real-world portability. (See the README's BESS section for the reference-emulator setup.)
    private static void PrintReferenceEmulatorNote() =>
        Console.WriteLine(value: "    cross-emulator note: a reference emulator's prebuilt tester has no savestate-import CLI flag (ROM/boot/battery/render only), so a live cross-emulator round trip is not invokable headlessly; the file's block/footer structure was instead verified against the BESS spec by hand.");
}
