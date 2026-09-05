using System.Diagnostics;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The SameBoy co-simulation diagnostic: boots the same ROM through the same boot ROM on both Puck and SameBoy's
/// <c>sb-trace events</c> tool, and reports the FIRST divergent conceptual event between them — so PPU/APU accuracy
/// work can be trace-led instead of knob-swept. Investigative, not a gate (see "Oracle discipline" in
/// docs/../.claude/skills/gaming-bricks/references/hardware-and-oracles.md): a foreign emulator's agreement or
/// disagreement never blocks the battery, it only says where to look next.
/// </summary>
internal static class CosimDiagnostic {
    /// <summary>The <c>--boot</c> value that selects the forge's authored boot image instead of a directory of
    /// hardware boot ROM dumps.</summary>
    public const string AuthoredBootKeyword = "puck";

    private const int DefaultFrames = 10;
    private const int HistoryDepth = 8;

    /// <summary><c>--cosim &lt;rom&gt; --sameboy &lt;sb-trace.exe&gt; --boot &lt;dir&gt;|puck [--model dmg|cgb]
    /// [--frames N] [--kind cpu|ppu|pcm|all] [--out &lt;dir&gt;]</c>: run the co-simulation and report the first
    /// divergence. Returns <see langword="false"/> (leaving the battery to run) when <c>--cosim</c> is absent.</summary>
    public static bool TryRun(string[] args, out int exitCode) {
        exitCode = 0;

        var cosimIndex = Array.IndexOf(
            array: args,
            value: "--cosim"
        );

        if (cosimIndex < 0) {
            return false;
        }

        var romPath = (((cosimIndex + 1) < args.Length)
            ? args[(cosimIndex + 1)]
            : null);
        var sameboyExe = CommandLineArguments.Value(
            args: args,
            name: "--sameboy"
        );
        var bootDir = CommandLineArguments.Value(
            args: args,
            name: "--boot"
        );

        if (
            (romPath is null) ||
            (sameboyExe is null) ||
            (bootDir is null)
        ) {
            Console.WriteLine(value: "  [ERROR] --cosim requires a ROM path, --sameboy <sb-trace.exe>, and --boot <dir>|puck");

            exitCode = 2;

            return true;
        }

        var modelArg = CommandLineArguments.Value(
            args: args,
            name: "--model"
        );
        var framesArg = CommandLineArguments.Value(
            args: args,
            name: "--frames"
        );
        var frames = (((framesArg is not null) && int.TryParse(
            s: framesArg,
            result: out var parsedFrames
        ))
            ? parsedFrames
            : DefaultFrames);
        var kindArg = (CommandLineArguments.Value(
            args: args,
            name: "--kind"
        ) ?? "all");
        var outDir = (CommandLineArguments.Value(
            args: args,
            name: "--out"
        ) ?? Path.Combine("artifacts", "gb-post", "cosim"));

        exitCode = Run(
            bootDir: bootDir,
            frames: frames,
            kindArg: kindArg,
            modelArg: modelArg,
            outDir: outDir,
            romPath: romPath,
            sameboyExe: sameboyExe
        );

        return true;
    }

    private static int Run(string romPath, string sameboyExe, string bootDir, string? modelArg, int frames, string kindArg, string outDir) {
        if (!File.Exists(path: romPath)) {
            Console.WriteLine(value: $"  [SKIP] cosim: rom not found at {romPath}");

            return 0;
        }

        if (!File.Exists(path: sameboyExe)) {
            Console.WriteLine(value: $"  [SKIP] cosim: sb-trace not found at {sameboyExe} (build it — see src/Puck.HumbleGamingBrick.Post/README.md)");

            return 0;
        }

        if (!TryParseKind(
            kindArg: kindArg,
            wantCpu: out var wantCpu,
            wantPcm: out var wantPcm,
            wantPpu: out var wantPpu
        )) {
            Console.WriteLine(value: $"  [ERROR] cosim: unknown --kind {kindArg} (expected cpu|ppu|pcm|all)");

            return 2;
        }

        var rom = File.ReadAllBytes(path: romPath);
        var model = (((modelArg is not null) && TryParseModel(
            model: out var parsedModel,
            value: modelArg
        ))
            ? parsedModel
            : ModelFromHeader(rom: rom));
        Directory.CreateDirectory(path: outDir);

        string bootPath;

        if (string.Equals(
            a: bootDir,
            b: AuthoredBootKeyword,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            // Both emulators execute the SAME authored image, so a divergence is a difference between the two
            // machines rather than between two boot programs.
            bootPath = Path.Combine(outDir, $"puck_boot_{model}.bin");

            File.WriteAllBytes(
                bytes: BootRomBuilder.Build(model: model),
                path: bootPath
            );
        } else {
            bootPath = Path.Combine(bootDir, ((model == ConsoleModel.CgbE)
                ? "cgb_boot.bin"
                : "dmg_boot.bin"));

            if (!File.Exists(path: bootPath)) {
                Console.WriteLine(value: $"  [SKIP] cosim: boot rom not found at {bootPath}");

                return 0;
            }
        }

        var bootRom = File.ReadAllBytes(path: bootPath);

        var sameboyTracePath = Path.Combine(outDir, "sameboy.cosim.bin");
        var puckTracePath = Path.Combine(outDir, "puck.cosim.bin");

        Console.WriteLine(value: $"  cosim {Path.GetFileName(path: romPath)} ({model}, {frames} frames, kind={kindArg})");

        var sameboyExitCode = RunSameBoy(
            bootPath: bootPath,
            frames: frames,
            kindArg: kindArg,
            model: model,
            outputPath: sameboyTracePath,
            romPath: romPath,
            sameboyExe: sameboyExe
        );

        if (sameboyExitCode != 0) {
            Console.WriteLine(value: $"  [INFRA] sb-trace exited {sameboyExitCode}");

            return 2;
        }

        RunPuck(
            bootRom: bootRom,
            frames: frames,
            model: model,
            outputPath: puckTracePath,
            rom: rom,
            wantCpu: wantCpu,
            wantPcm: wantPcm,
            wantPpu: wantPpu
        );

        return Compare(
            puckPath: puckTracePath,
            sameboyPath: sameboyTracePath
        );
    }

    // Spawns sb-trace in events mode: `sb-trace events <rom> <bootrom> <dmg|cgb> <frames> <cpu|ppu|pcm|all> <outfile>`.
    private static int RunSameBoy(string sameboyExe, string romPath, string bootPath, ConsoleModel model, int frames, string kindArg, string outputPath) {
        var psi = new ProcessStartInfo {
            CreateNoWindow = true,
            FileName = sameboyExe,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(item: "events");
        psi.ArgumentList.Add(item: romPath);
        psi.ArgumentList.Add(item: bootPath);
        psi.ArgumentList.Add(item: ((model == ConsoleModel.CgbE)
            ? "cgb"
            : "dmg"));
        psi.ArgumentList.Add(item: frames.ToString());
        psi.ArgumentList.Add(item: kindArg);
        psi.ArgumentList.Add(item: outputPath);

        using var process = Process.Start(startInfo: psi)!;

        process.WaitForExit();

        if (process.ExitCode != 0) {
            Console.WriteLine(value: $"    sb-trace stderr: {process.StandardError.ReadToEnd()}");
        }

        return process.ExitCode;
    }
    // Builds a Puck machine booted through the SAME boot ROM image sb-trace used, arms the core's dormant trace seam
    // (Sm83.SetTraceSink / Ppu.SetTraceSink), and runs it to the frame budget, writing the mirrored record stream.
    private static void RunPuck(byte[] rom, byte[] bootRom, ConsoleModel model, int frames, bool wantCpu, bool wantPpu, bool wantPcm, string outputPath) {
        using var machine = PostMachine.Build(
            bootRom: bootRom,
            model: model,
            rom: rom
        );

        var cpu = machine.GetRequiredService<Sm83>();
        var ppu = machine.GetRequiredService<Ppu>();
        var clock = machine.GetRequiredService<MasterClock>();
        var apu = machine.GetRequiredService<IApu>();

        using (var output = File.Create(path: outputPath))
        using (var sink = new CosimTraceSink(
            apu: apu,
            clock: clock,
            output: output,
            wantCpu: wantCpu,
            wantPcm: wantPcm,
            wantPpu: wantPpu
        )) {
            cpu.SetTraceSink(sink: sink);
            ppu.SetTraceSink(sink: sink);

            var targetCycles = (((ulong)frames) * ((ulong)PostMachine.TCyclesPerFrame));

            while (clock.CycleCount < targetCycles) {
                machine.Machine.StepInstruction();
            }

            cpu.SetTraceSink(sink: null);
            ppu.SetTraceSink(sink: null);

            Console.WriteLine(value: $"    puck: {sink.RecordCount:N0} records -> {outputPath}");
        }
    }
    // Reads both trace files fully, splits each into the cpu/ppu/pcm groups (ppu = PpuMode + PpuPixel, in file order),
    // and walks each requested group index-by-index. The first divergence across every requested group — by Puck's own
    // (always exact) cycle — is reported with its preceding HistoryDepth events from both sides.
    private static int Compare(string sameboyPath, string puckPath) {
        var sameboyAll = ReadAll(path: sameboyPath);
        var puckAll = ReadAll(path: puckPath);

        // The two PPU sub-streams carry different evidence and are compared separately. Pixel records are exact and
        // index-aligned on both sides (160 per visible line, in order). Mode records are not: SameBoy re-reads STAT
        // once per GB_run() call, so a polled mode that holds for less than the instruction in flight is invisible to
        // it — the line-start mode-0 window is one 8 MHz unit wide in Core/display.c — while Puck's seam sees every
        // dot. That sub-stream therefore compares with a one-step resynchronization (AllowSampleSkew).
        var groups = new (string Name, CosimEventKind[] Kinds, bool AllowSampleSkew)[] {
            ("cpu", [CosimEventKind.Cpu], false),
            ("ppu-mode", [CosimEventKind.PpuMode], true),
            ("ppu-pixel", [CosimEventKind.PpuPixel], false),
            ("pcm", [CosimEventKind.Pcm], false),
        };

        CosimDivergence? firstDivergence = null;

        foreach (var group in groups) {
            var sameboyGroup = Filter(
                events: sameboyAll,
                kinds: group.Kinds
            );
            var puckGroup = Filter(
                events: puckAll,
                kinds: group.Kinds
            );

            if (
                (sameboyGroup.Count == 0) &&
                (puckGroup.Count == 0)
            ) {
                continue;
            }

            var divergence = FindDivergence(
                allowSampleSkew: group.AllowSampleSkew,
                groupName: group.Name,
                puckGroup: puckGroup,
                sameboyGroup: sameboyGroup
            );

            if (divergence is null) {
                Console.WriteLine(value: $"  cosim {group.Name}: no divergence in {Math.Min(sameboyGroup.Count, puckGroup.Count)} events ({sameboyGroup.Count} sameboy / {puckGroup.Count} puck).");

                continue;
            }

            // A real content divergence always outranks a trailing length mismatch, regardless of cycle number — the
            // latter is only ever a budget-cutoff artifact at the very end of a stream (see FindDivergence).
            var beatsCurrent = ((firstDivergence is null) ||
                (!divergence.Value.IsTrailingLengthMismatch && firstDivergence.Value.IsTrailingLengthMismatch) ||
                ((divergence.Value.IsTrailingLengthMismatch == firstDivergence.Value.IsTrailingLengthMismatch) &&
                    (divergence.Value.Cycle < firstDivergence.Value.Cycle)));

            if (beatsCurrent) {
                firstDivergence = divergence;
            }
        }

        if (firstDivergence is null) {
            Console.WriteLine(value: "  == cosim: NO divergence in any requested kind ==");

            return 0;
        }

        Report(divergence: firstDivergence.Value);

        // A trailing length mismatch is a run-loop artifact, not an accuracy finding (see FindDivergence): every
        // record the two sides both produced agreed exactly. Only a real content divergence gates the exit code.
        return (firstDivergence.Value.IsTrailingLengthMismatch
            ? 0
            : 1);
    }
    // Walks the two streams with independent cursors. They advance in lockstep while records agree; with
    // allowSampleSkew set, a single record present on one side only is stepped over — the record after it must match
    // the other side's current record — and counted, which absorbs SameBoy's coarser once-per-GB_run() sampling
    // without letting a genuinely different record slip past.
    private static CosimDivergence? FindDivergence(string groupName, List<CosimEvent> sameboyGroup, List<CosimEvent> puckGroup, bool allowSampleSkew) {
        var sameboyIndex = 0;
        var puckIndex = 0;
        var skips = 0;

        while (
            (sameboyIndex < sameboyGroup.Count) &&
            (puckIndex < puckGroup.Count)
        ) {
            var sameboyEvent = sameboyGroup[sameboyIndex];
            var puckEvent = puckGroup[puckIndex];
            var contentEqual = sameboyEvent.ContentEquals(other: puckEvent);
            var cycleEqual = (!sameboyEvent.CycleIsExact || (sameboyEvent.Cycle == puckEvent.Cycle));

            if (contentEqual && cycleEqual) {
                ++puckIndex;
                ++sameboyIndex;

                continue;
            }

            if (allowSampleSkew) {
                if (Matches(
                    left: sameboyGroup,
                    leftIndex: sameboyIndex,
                    right: puckGroup,
                    rightIndex: (puckIndex + 1)
                )) {
                    ++puckIndex;
                    ++skips;

                    continue;
                }

                if (Matches(
                    left: puckGroup,
                    leftIndex: puckIndex,
                    right: sameboyGroup,
                    rightIndex: (sameboyIndex + 1)
                )) {
                    ++sameboyIndex;
                    ++skips;

                    continue;
                }
            }

            return Divergence(
                groupName: groupName,
                isTrailingLengthMismatch: false,
                puckGroup: puckGroup,
                puckIndex: puckIndex,
                sameboyGroup: sameboyGroup,
                sameboyIndex: sameboyIndex,
                skips: skips
            );
        }

        if (
            (sameboyIndex < sameboyGroup.Count) ||
            (puckIndex < puckGroup.Count)
        ) {
            // A length mismatch with every compared record equal up to it is the two run loops' differing
            // instruction-atomic overshoot at the --frames budget cutoff (GB_run() vs Machine.StepInstruction each
            // finish the whole unit of work in flight rather than stopping exactly on the target cycle) — a harness
            // artifact, not a content divergence, so Compare treats it as evidence, not a failure.
            return Divergence(
                groupName: groupName,
                isTrailingLengthMismatch: true,
                puckGroup: puckGroup,
                puckIndex: puckIndex,
                sameboyGroup: sameboyGroup,
                sameboyIndex: sameboyIndex,
                skips: skips
            );
        }

        if (skips != 0) {
            Console.WriteLine(value: $"  cosim {groupName}: content agreed after stepping over {skips} sampling-granularity record(s).");
        }

        return null;
    }
    // Whether the record at leftIndex equals the one at rightIndex, treating an out-of-range index as no match.
    private static bool Matches(List<CosimEvent> left, int leftIndex, List<CosimEvent> right, int rightIndex) =>
        ((leftIndex < left.Count) &&
        (rightIndex < right.Count) &&
        left[leftIndex].ContentEquals(other: right[rightIndex]));
    private static CosimDivergence Divergence(string groupName, List<CosimEvent> sameboyGroup, int sameboyIndex, List<CosimEvent> puckGroup, int puckIndex, bool isTrailingLengthMismatch, int skips) {
        var referenceCycle = ((puckIndex < puckGroup.Count)
            ? puckGroup[puckIndex].Cycle
            : ((puckGroup.Count > 0)
                ? puckGroup[^1].Cycle
                : ((sameboyGroup.Count > 0)
                    ? sameboyGroup[^1].Cycle
                    : 0UL)));

        return new CosimDivergence(
            Cycle: referenceCycle,
            GroupName: groupName,
            Index: puckIndex,
            IsTrailingLengthMismatch: isTrailingLengthMismatch,
            Puck: History(
                events: puckGroup,
                index: puckIndex
            ),
            Sameboy: History(
                events: sameboyGroup,
                index: sameboyIndex
            ),
            Skips: skips
        );
    }
    private static void Report(CosimDivergence divergence) {
        Console.WriteLine(value: (divergence.IsTrailingLengthMismatch
            ? $"  == TRAILING LENGTH MISMATCH in kind '{divergence.GroupName}' at index {divergence.Index} (puck cycle {divergence.Cycle}, frame {(divergence.Cycle / ((ulong)PostMachine.TCyclesPerFrame))}) — every compared record agreed; one side simply ran a few more T-cycles past the --frames budget than the other before stopping =="
            : $"  == FIRST DIVERGENCE in kind '{divergence.GroupName}' at index {divergence.Index} (puck cycle {divergence.Cycle}, frame {(divergence.Cycle / ((ulong)PostMachine.TCyclesPerFrame))}) =="));
        if (divergence.Skips != 0) {
            Console.WriteLine(value: $"     ({divergence.Skips} sampling-granularity record(s) stepped over before this point)");
        }

        Console.WriteLine(value: "     -- preceding sameboy events (oldest first) --");

        foreach (var record in divergence.Sameboy.History) {
            Console.WriteLine(value: $"       {record.Describe()}");
        }

        Console.WriteLine(value: $"     sameboy: {(divergence.Sameboy.Current?.Describe() ?? "<stream ended>")}");
        Console.WriteLine(value: "     -- preceding puck events (oldest first) --");

        foreach (var record in divergence.Puck.History) {
            Console.WriteLine(value: $"       {record.Describe()}");
        }

        Console.WriteLine(value: $"     puck:    {(divergence.Puck.Current?.Describe() ?? "<stream ended>")}");
    }
    private static (List<CosimEvent> History, CosimEvent? Current) History(List<CosimEvent> events, int index) {
        var start = Math.Max(0, (index - HistoryDepth));
        var history = new List<CosimEvent>(capacity: (index - start));

        for (var i = start; (i < index); ++i) {
            history.Add(item: events[i]);
        }

        var current = ((index < events.Count)
            ? events[index]
            : ((CosimEvent?)null));

        return (history, current);
    }
    private static List<CosimEvent> Filter(List<CosimEvent> events, CosimEventKind[] kinds) {
        var filtered = new List<CosimEvent>();

        foreach (var candidate in events) {
            if (Array.IndexOf(
                array: kinds,
                value: candidate.Kind
            ) >= 0) {
                filtered.Add(item: candidate);
            }
        }

        return filtered;
    }
    private static List<CosimEvent> ReadAll(string path) {
        var events = new List<CosimEvent>();

        using var stream = File.OpenRead(path: path);
        using var reader = new BinaryReader(stream);

        while (true) {
            var record = CosimEvent.TryReadFrom(reader: reader);

            if (record is null) {
                break;
            }

            events.Add(item: record.Value);
        }

        return events;
    }
    private static bool TryParseKind(string kindArg, out bool wantCpu, out bool wantPpu, out bool wantPcm) {
        switch (kindArg.ToLowerInvariant()) {
            case "cpu":
                (wantCpu, wantPpu, wantPcm) = (true, false, false);

                return true;
            case "ppu":
                (wantCpu, wantPpu, wantPcm) = (false, true, false);

                return true;
            case "pcm":
                (wantCpu, wantPpu, wantPcm) = (false, false, true);

                return true;
            case "all":
                (wantCpu, wantPpu, wantPcm) = (true, true, true);

                return true;
            default:
                (wantCpu, wantPpu, wantPcm) = (false, false, false);

                return false;
        }
    }
    private static bool TryParseModel(string value, out ConsoleModel model) {
        if (string.Equals(
            a: value,
            b: "cgb",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            model = ConsoleModel.CgbE;

            return true;
        }

        if (string.Equals(
            a: value,
            b: "dmg",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            model = ConsoleModel.DmgC;

            return true;
        }

        model = ConsoleModel.DmgC;

        return false;
    }
    private static ConsoleModel ModelFromHeader(byte[] rom) =>
        (((rom.Length > 0x0143) && (0 != (rom[0x0143] & 0x80)))
        ? ConsoleModel.CgbE
        : ConsoleModel.DmgC);

    private readonly record struct CosimDivergence(
        string GroupName,
        int Index,
        ulong Cycle,
        bool IsTrailingLengthMismatch,
        (List<CosimEvent> History, CosimEvent? Current) Sameboy,
        (List<CosimEvent> History, CosimEvent? Current) Puck,
        int Skips
    );
}
