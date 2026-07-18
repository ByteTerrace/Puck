using Puck.Capture;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;
using Puck.Snapshots;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The diagnostic surface of the HumbleGamingBrick POST: single-ROM inspectors dispatched from CLI flags before the
/// battery runs, mirroring the Advanced POST's toolbox. These are investigative tools, not self-checking stages; <see cref="TryRun"/>
/// dispatches them so the battery stays the default.
/// </summary>
internal static class Diagnostics {
    /// <summary>The frame budget a render runs when none is given — ten seconds of emulated time, enough for a
    /// commercial ROM to clear its logo screens and start drawing.</summary>
    private const int DefaultRenderFrames = 600;

    /// <summary>The frame budget a snapshot dump runs when <c>--frames</c> is absent.</summary>
    private const int DefaultDumpSnapshotFrames = 300;

    /// <summary>Dispatches the diagnostic CLI flags — each runs a single investigative mode and returns; when none
    /// matches, the caller proceeds to the POST battery.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="exitCode">The exit code the handled mode produced (0 when it does not gate).</param>
    /// <returns><see langword="true"/> when a diagnostic flag was handled (return <paramref name="exitCode"/>, skip the
    /// battery); otherwise <see langword="false"/>.</returns>
    public static bool TryRun(string[] args, out int exitCode) {
        exitCode = 0;

        // --hash-divergence [<romA>] [<romB>] [--fine] [--frames <n>] [--perturb-at <f>]: the per-tick hash-divergence
        // localizer — lockstep two machines, snapshot-hash them each frame (or scanline with --fine), and on the first
        // mismatch name the component + byte offset that diverged. Split into its own helper to bound this method's
        // cyclomatic complexity. No ROM path falls back to the built-in synthetic cartridge (runs anywhere).
        if (TryHashDivergence(args: args, exitCode: out var hashDivergenceExitCode)) {
            exitCode = hashDivergenceExitCode;

            return true;
        }

        // --link-explore <romA> <scriptA> [<romB> <scriptB>] [--frames N] [--dump-every M] [--out DIR] [--modelA/B]:
        // the interactive link explorer — drive one or two commercial ROMs under text input scripts and dump frames, to
        // author the scripts the cross-generation link-game gate later replays. Its own file to bound this method.
        if (LinkExplore.TryRun(args: args, exitCode: out var linkExploreExitCode)) {
            exitCode = linkExploreExitCode;

            return true;
        }

        // --trade-explore <rom> [--linked] [--scriptA path] [--scriptB path] [--frames N] [--dump-every M] [--out DIR]
        // [--bootrom path] [--spawn g:m:y:x] [--model cgb]: the cross-gen-cart trade explorer — boot one or two Cgb
        // trade-cart machines with crafted saves and dump framebuffers + a peek panel while authoring the scripted-trade
        // harness. --trade-export [--out DIR] writes the two crafted trade saves + README for the demo's per-cabinet
        // saves. Its own file to bound this method.
        if (ScriptedTradeExplore.TryRun(args: args, exitCode: out var tradeExploreExitCode)) {
            exitCode = tradeExploreExitCode;

            return true;
        }

        // --bess-export <out> [--rom <path>] [--frames N]: write a BESS-compliant savestate and self-check the
        // export/import round trip into a fresh machine. --bess-import <file> [--rom <path>]: load a BESS file (ours
        // or a foreign one) and report the state it restored. Its own file to bound this method.
        if (BessDiagnostic.TryRun(args: args, exitCode: out var bessExitCode)) {
            exitCode = bessExitCode;

            return true;
        }

        // --bench [--bench-rom <rom>] [--bench-frames <n>] [--bench-fleet <csv>]: the machine-fleet performance
        // instrument (scaling curves, burst catch-up, snapshot/fork latencies, mailbox cycle, footprint).
        foreach (var arg in args) {
            if (string.Equals(a: arg, b: "--bench", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                exitCode = BenchDiagnostic.Run(args: args);

                return true;
            }
        }

        // --halt-share <rom> [warmFrames] [measureFrames] [dmg|cgb|agb]: measure the fraction of machine time the CPU
        // spends halted — the idle-fast-forward lever's gate (machine-fleet-plan.md lever 4): the lever's ceiling is
        // bounded by this share, and by how much of a halted cycle's cost is skippable at all (the PPU still draws).
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(a: args[index], b: "--halt-share", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                var romPath = args[(index + 1)];
                var warmFrames = ((((index + 2) < args.Length) && int.TryParse(s: args[(index + 2)], result: out var parsedWarm)) ? parsedWarm : 300);
                var measureFrames = ((((index + 3) < args.Length) && int.TryParse(s: args[(index + 3)], result: out var parsedMeasure)) ? parsedMeasure : 300);
                var model = ((((index + 4) < args.Length) && TryParseModel(value: args[(index + 4)], model: out var parsedModel))
                    ? parsedModel
                    : ModelFromHeader(rom: File.ReadAllBytes(path: romPath)));

                HaltShare(romPath: romPath, warmFrames: warmFrames, measureFrames: measureFrames, model: model);

                return true;
            }
        }

        // --stat-trace <rom> <out.txt> [frames] [dmg|cgb|agb] [lyMin] [lyMax]: instruction-level STAT/LY/IF trace for
        // diagnosing the acceptance STAT-timing family — one line per instruction while LY is inside the window (plus every
        // interrupt-vector entry), carrying the master-clock cycle before the step so wake and read cycles are exact.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (string.Equals(a: args[index], b: "--stat-trace", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                var romPath = args[(index + 1)];
                var frames = ((((index + 3) < args.Length) && int.TryParse(s: args[(index + 3)], result: out var parsedFrames)) ? parsedFrames : 20);
                var model = ((((index + 4) < args.Length) && TryParseModel(value: args[(index + 4)], model: out var parsedModel))
                    ? parsedModel
                    : ModelFromHeader(rom: File.ReadAllBytes(path: romPath)));
                var lyMin = ((((index + 5) < args.Length) && int.TryParse(s: args[(index + 5)], result: out var parsedMin)) ? parsedMin : 0x40);
                var lyMax = ((((index + 6) < args.Length) && int.TryParse(s: args[(index + 6)], result: out var parsedMax)) ? parsedMax : 0x46);

                StatTrace(romPath: romPath, outputPath: args[(index + 2)], frames: frames, model: model, lyMin: lyMin, lyMax: lyMax);

                return true;
            }
        }

        // --render <rom> <out.png> [frames] [dmg|cgb|agb]: boot a ROM (no boot ROM, seeded post-boot state), run N frames,
        // and dump the framebuffer, to eyeball the PPU output. The model defaults to what the cartridge header asks
        // for (CGB flag at 0x0143), so a dual-mode cart renders in color unless "dmg" forces the monochrome costume.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (string.Equals(a: args[index], b: "--render", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                var romPath = args[(index + 1)];
                var frames = ((((index + 3) < args.Length) && int.TryParse(s: args[(index + 3)], result: out var parsedFrames))
                    ? parsedFrames
                    : DefaultRenderFrames);
                var model = ((((index + 4) < args.Length) && TryParseModel(value: args[(index + 4)], model: out var parsedModel))
                    ? parsedModel
                    : ModelFromHeader(rom: File.ReadAllBytes(path: romPath)));

                Render(romPath: romPath, outputPath: args[(index + 2)], frames: frames, model: model);

                return true;
            }
        }

        // --dump-snapshot [--frames N] [--rom <path>] [--out <file>]: boot the synthetic ROM (or --rom), run N frames
        // (default 300), and write the raw snapshot image + a sidecar section table to disk — offline cross-build
        // diffing input for C1's zero-byte-shift proof (--hash-divergence has no cross-build mode). Split into its own
        // helper to bound this method's cyclomatic complexity.
        if (Array.IndexOf(array: args, value: "--dump-snapshot") >= 0) {
            exitCode = DumpSnapshot(args: args);

            return true;
        }

        return false;
    }

    // Parses the --hash-divergence flag and its knobs, then runs the localizer. Returns false (leaving the battery to
    // run) when the flag is absent. The first non-flag token after --hash-divergence is romA (omitted = the synthetic
    // cartridge), the second is romB; --fine, --frames <n> (default 600), and --perturb-at <f> are order-independent.
    private static bool TryHashDivergence(string[] args, out int exitCode) {
        exitCode = 0;

        var hashDivergenceIndex = Array.IndexOf(array: args, value: "--hash-divergence");

        if (hashDivergenceIndex < 0) {
            return false;
        }

        var romAPath = PositionalAfter(args: args, index: hashDivergenceIndex, offset: 1);
        // romB is the SECOND positional after the flag, so it only exists once romA was given; without romA (the
        // synthetic-cartridge self-check) a following knob like "--frames 120" must not be mistaken for a ROM path.
        var romBPath = ((romAPath is not null) ? PositionalAfter(args: args, index: hashDivergenceIndex, offset: 2) : null);
        var fine = (Array.IndexOf(array: args, value: "--fine") >= 0);
        var framesArg = ArgValue(args: args, name: "--frames");
        var frames = (((framesArg is not null) && int.TryParse(s: framesArg, result: out var parsedFrames)) ? parsedFrames : 600);
        var perturbArg = ArgValue(args: args, name: "--perturb-at");
        var perturbAtFrame = (((perturbArg is not null) && int.TryParse(s: perturbArg, result: out var parsedPerturb)) ? parsedPerturb : (int?)null);

        exitCode = HashDivergenceProbe.Run(romAPath: romAPath, romBPath: romBPath, frames: frames, fine: fine, perturbAtFrame: perturbAtFrame);

        return true;
    }
    // The positional token `offset` positions after `index`, or null when it is absent or is itself a flag (starts "--").
    private static string? PositionalAfter(string[] args, int index, int offset) =>
        ((((index + offset) < args.Length) && !args[(index + offset)].StartsWith(value: "--", comparisonType: StringComparison.Ordinal))
            ? args[(index + offset)]
            : null);
    // The value following a named flag (e.g. --frames 300), or null when the flag is absent or has no following token.
    private static string? ArgValue(string[] args, string name) {
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return args[(index + 1)];
            }
        }

        return null;
    }
    private static bool TryParseModel(string value, out ConsoleModel model) {
        if (string.Equals(a: value, b: "dmg", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            model = ConsoleModel.Dmg;

            return true;
        }

        if (string.Equals(a: value, b: "cgb", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            model = ConsoleModel.Cgb;

            return true;
        }

        if (string.Equals(a: value, b: "agb", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            model = ConsoleModel.Agb;

            return true;
        }

        model = ConsoleModel.Dmg;

        return false;
    }
    private static ConsoleModel ModelFromHeader(byte[] rom) =>
        (((rom.Length > 0x0143) && (0 != (rom[0x0143] & 0x80))) ? ConsoleModel.Cgb : ConsoleModel.Dmg);
    // Warm the machine with the fast Run path, then instruction-step under the clock, attributing each instruction's
    // consumed cycles to halted time when it began halted (a wake instruction lands in the halted bucket — off by one
    // instruction, immaterial at this scale).
    private static void HaltShare(string romPath, int warmFrames, int measureFrames, ConsoleModel model) {
        using var machine = PostMachine.Build(model: model, rom: File.ReadAllBytes(path: romPath));

        var cpu = machine.GetRequiredService<ICpu>();
        var clock = machine.GetRequiredService<MasterClock>();

        PostMachine.RunFrames(instance: machine, frames: warmFrames);

        var targetCycles = ((ulong)measureFrames * (ulong)PostMachine.TCyclesPerFrame);
        var startCycles = clock.CycleCount;
        var haltedCycles = 0UL;

        while ((clock.CycleCount - startCycles) < targetCycles) {
            var wasHalted = cpu.IsHalted;
            var before = clock.CycleCount;

            machine.Machine.StepInstruction();

            if (wasHalted) {
                haltedCycles += (clock.CycleCount - before);
            }
        }

        var totalCycles = (clock.CycleCount - startCycles);

        Console.WriteLine(value: $"  halt-share {Path.GetFileName(path: romPath)} ({model}): {haltedCycles:N0} of {totalCycles:N0} cycles halted over {measureFrames} frames (after {warmFrames} warm) = {((100.0 * haltedCycles) / totalCycles):F1}%");
    }
    // Step the machine one instruction at a time and log, for every instruction executed while LY sits inside the
    // window (plus every entry into the interrupt-vector page), the master-clock cycle BEFORE the step, the program
    // counter, LY, STAT, the raw interrupt-request lines, and A/B — enough to reconstruct exact wake and bus-read
    // cycles for the acceptance STAT-timing family offline.
    private static void StatTrace(string romPath, string outputPath, int frames, ConsoleModel model, int lyMin, int lyMax) {
        using var machine = PostMachine.Build(model: model, rom: File.ReadAllBytes(path: romPath));

        var clock = machine.GetRequiredService<MasterClock>();
        var cpu = machine.GetRequiredService<ICpu>();
        var interrupts = machine.GetRequiredService<IInterruptController>();
        var ppu = machine.GetRequiredService<IPpu>();
        var targetCycles = ((ulong)frames * (ulong)PostMachine.TCyclesPerFrame);

        using var writer = new StreamWriter(path: outputPath);

        while (clock.CycleCount < targetCycles) {
            var before = clock.CycleCount;
            var pc = cpu.ProgramCounter;
            var ly = ppu.ReadRegister(address: MemoryMap.LcdY);
            var stat = ppu.ReadRegister(address: MemoryMap.LcdStatus);
            var requested = (byte)interrupts.Requested;
            var halted = cpu.IsHalted;

            machine.Machine.StepInstruction();

            if (((ly >= lyMin) && (ly <= lyMax)) || (pc < 0x0100)) {
                writer.WriteLine(value: $"{before} pc={pc:X4} ly={ly:X2} stat={stat:X2} if={requested:X2} a={cpu.A:X2} b={cpu.B:X2}{(halted ? " halt" : string.Empty)}");
            }
        }

        Console.WriteLine(value: $"  stat-trace {Path.GetFileName(path: romPath)} ({model}, {frames} frames, ly {lyMin:X2}-{lyMax:X2}) -> {outputPath}");
    }
    private static void Render(string romPath, string outputPath, int frames, ConsoleModel model) {
        using var machine = PostMachine.Build(model: model, rom: File.ReadAllBytes(path: romPath));

        // Frame-at-a-time so the KEY1 speed switch is caught at the frame it happens — the observable that separates
        // "the game runs double-speed on Color hardware" from "the game paces identically on every costume".
        var key1 = machine.GetRequiredService<IKey1>();
        var speedSwitchFrame = -1;

        for (var frame = 0; (frame < frames); ++frame) {
            PostMachine.RunFrames(instance: machine, frames: 1);

            if ((speedSwitchFrame < 0) && key1.IsDoubleSpeed) {
                speedSwitchFrame = frame;
            }
        }

        var speedDetail = ((speedSwitchFrame >= 0)
            ? $"double-speed since frame {speedSwitchFrame}"
            : "normal speed throughout");
        var framebuffer = machine.GetRequiredService<IFramebuffer>();
        var pixels = framebuffer.Pixels;
        var rgba = new byte[(pixels.Length * 4)];

        // The framebuffer packs 0x00RRGGBB; the encoder wants R,G,B,A bytes, so repack with an opaque alpha.
        for (var index = 0; (index < pixels.Length); ++index) {
            var offset = (index * 4);
            var pixel = pixels[index];

            rgba[offset] = (byte)(pixel >> 16);
            rgba[(offset + 1)] = (byte)(pixel >> 8);
            rgba[(offset + 2)] = (byte)pixel;
            rgba[(offset + 3)] = 0xFF;
        }

        PngEncoder.Write(path: outputPath, rgba: rgba, width: framebuffer.Width, height: framebuffer.Height);

        Console.WriteLine(value: $"  rendered {Path.GetFileName(path: romPath)} ({model}, {frames} frames, {speedDetail}) -> {outputPath} [fb-hash 0x{HashPixels(pixels: pixels):X16}]");
    }
    private static ulong HashPixels(ReadOnlySpan<uint> pixels) {
        var hash = 14_695_981_039_346_656_037ul;

        foreach (var pixel in pixels) {
            hash = ((hash ^ pixel) * 1_099_511_628_211ul);
        }

        return hash;
    }
    // Parses --dump-snapshot's knobs, boots the machine, runs the requested frames, and writes the snapshot image plus
    // its section-table sidecar. Returns 2 when --rom names a missing file, otherwise 0.
    private static int DumpSnapshot(string[] args) {
        var romPath = ArgValue(args: args, name: "--rom");
        byte[] rom;
        string romLabel;
        var model = ConsoleModel.Dmg;

        if (string.IsNullOrEmpty(value: romPath)) {
            rom = SyntheticRom.Create();
            romLabel = "synthetic";
        } else if (File.Exists(path: romPath)) {
            rom = File.ReadAllBytes(path: romPath);
            romLabel = Path.GetFileName(path: romPath);
            model = ModelFromHeader(rom: rom);
        } else {
            Console.WriteLine(value: $"  [SKIP] --dump-snapshot: rom not found at {romPath}");

            return 2;
        }

        var framesArg = ArgValue(args: args, name: "--frames");
        var frames = (((framesArg is not null) && int.TryParse(s: framesArg, result: out var parsedFrames)) ? parsedFrames : DefaultDumpSnapshotFrames);
        var imagePath = (ArgValue(args: args, name: "--out") ?? Path.Combine("artifacts", "gb-post", "snapshot.bin"));
        var imageDirectory = Path.GetDirectoryName(path: Path.GetFullPath(path: imagePath));

        if (!string.IsNullOrEmpty(value: imageDirectory)) {
            Directory.CreateDirectory(path: imageDirectory);
        }

        using var machine = PostMachine.Build(model: model, rom: rom);

        PostMachine.RunFrames(instance: machine, frames: frames);

        var snapshot = machine.Machine.Snapshot();

        File.WriteAllBytes(path: imagePath, bytes: snapshot.Data.ToArray());

        var sectionsPath = $"{imagePath}.sections.txt";

        WriteSectionTable(path: sectionsPath, sections: snapshot.Sections);

        // The same repo fingerprint HashDivergenceProbe hashes a snapshot with, so a --dump-snapshot fingerprint and a
        // --hash-divergence report describe the same instant the same way.
        var fingerprint = StateFingerprint.Compute(data: snapshot.Data);

        Console.WriteLine(value: $"  dump-snapshot {romLabel} ({model}, {frames} frames) -> {imagePath} ({snapshot.Size:N0} bytes) [fingerprint 0x{fingerprint:X16}], sections -> {sectionsPath}");

        return 0;
    }
    // One line per section: name, offset, length — enough to localize an offline byte-shift between two snapshot
    // images to the component that owns it (a cross-build diff has no running machine to walk).
    private static void WriteSectionTable(string path, IReadOnlyList<SnapshotSection> sections) {
        using var writer = new StreamWriter(path: path);

        foreach (var section in sections) {
            writer.WriteLine(value: $"{section.Name}\t{section.Offset}\t{section.Length}");
        }
    }
}
