using System.Runtime.InteropServices;
using Puck.Assets;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;
using Puck.Maths;

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
        if (TryHashDivergence(
            args: args,
            exitCode: out var hashDivergenceExitCode
        )) {
            exitCode = hashDivergenceExitCode;

            return true;
        }

        // --link-explore <romA> <scriptA> [<romB> <scriptB>] [--frames N] [--dump-every M] [--out DIR] [--modelA/B]:
        // the interactive link explorer — drive one or two commercial ROMs under text input scripts and dump frames, to
        // author the scripts the cross-generation link-game gate later replays. Its own file to bound this method.
        if (LinkExplore.TryRun(
            args: args,
            exitCode: out var linkExploreExitCode
        )) {
            exitCode = linkExploreExitCode;

            return true;
        }

        // --trade-explore <rom> [--linked] [--scriptA path] [--scriptB path] [--frames N] [--dump-every M] [--out DIR]
        // [--bootrom path] [--spawn g:m:y:x] [--model cgb]: the cross-gen-cart trade explorer — boot one or two Cgb
        // trade-cart machines with crafted saves and dump framebuffers + a peek panel while authoring the scripted-trade
        // harness. --trade-export [--out DIR] writes the two crafted trade saves + README for the demo's per-cabinet
        // saves. Its own file to bound this method.
        if (ScriptedTradeExplore.TryRun(
            args: args,
            exitCode: out var tradeExploreExitCode
        )) {
            exitCode = tradeExploreExitCode;

            return true;
        }

        // --bess-export <out> [--rom <path>] [--frames N]: write a BESS-compliant savestate and self-check the
        // export/import round trip into a fresh machine. --bess-import <file> [--rom <path>]: load a BESS file (ours
        // or a foreign one) and report the state it restored. Its own file to bound this method.
        if (BessDiagnostic.TryRun(
            args: args,
            exitCode: out var bessExitCode
        )) {
            exitCode = bessExitCode;

            return true;
        }

        // --cosim <rom> --sameboy <sb-trace.exe> --boot <dir> [--model dmg|cgb] [--frames N] [--kind cpu|ppu|pcm|all]
        // [--out <dir>]: the SameBoy co-simulation diagnostic — reports the first divergent conceptual event between
        // Puck and SameBoy for a ROM. Its own file to bound this method.
        if (CosimDiagnostic.TryRun(
            args: args,
            exitCode: out var cosimExitCode
        )) {
            exitCode = cosimExitCode;

            return true;
        }

        // --bench [--bench-rom <rom>] [--bench-frames <n>] [--bench-fleet <csv>]: the machine-fleet performance
        // instrument (scaling curves, burst catch-up, snapshot/fork latencies, mailbox cycle, footprint).
        foreach (var arg in args) {
            if (string.Equals(
                a: arg,
                b: "--bench",
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                exitCode = BenchDiagnostic.Run(args: args);

                return true;
            }
        }

        // --halt-share <rom> [warmFrames] [measureFrames] [dmg|cgb|agb]: measure the fraction of machine time the CPU
        // spends halted — the measurement that bounds an idle-fast-forward lever: its ceiling is capped by this share,
        // and by how much of a halted cycle's cost is skippable at all (the PPU still draws).
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(
                a: args[index],
                b: "--halt-share",
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                var romPath = args[(index + 1)];
                var warmFrames = ((((index + 2) < args.Length) && int.TryParse(
                    s: args[(index + 2)],
                    result: out var parsedWarm
                ))
                    ? parsedWarm
                    : 300);
                var measureFrames = ((((index + 3) < args.Length) && int.TryParse(
                    s: args[(index + 3)],
                    result: out var parsedMeasure
                ))
                    ? parsedMeasure
                    : 300);
                var model = ((((index + 4) < args.Length) && TryParseModel(
                    value: args[(index + 4)],
                    model: out var parsedModel
                ))
                    ? parsedModel
                    : ModelFromHeader(rom: File.ReadAllBytes(path: romPath)));

                HaltShare(
                    measureFrames: measureFrames,
                    model: model,
                    romPath: romPath,
                    warmFrames: warmFrames
                );

                return true;
            }
        }

        // --stat-trace <rom> <out.txt> [frames] [dmg|cgb|agb] [lyMin] [lyMax]: instruction-level STAT/LY/IF trace for
        // diagnosing the acceptance STAT-timing family — one line per instruction while LY is inside the window (plus every
        // interrupt-vector entry), carrying the master-clock cycle before the step so wake and read cycles are exact.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (string.Equals(
                a: args[index],
                b: "--stat-trace",
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                var romPath = args[(index + 1)];
                var frames = ((((index + 3) < args.Length) && int.TryParse(
                    s: args[(index + 3)],
                    result: out var parsedFrames
                ))
                    ? parsedFrames
                    : 20);
                var model = ((((index + 4) < args.Length) && TryParseModel(
                    value: args[(index + 4)],
                    model: out var parsedModel
                ))
                    ? parsedModel
                    : ModelFromHeader(rom: File.ReadAllBytes(path: romPath)));
                var lyMin = ((((index + 5) < args.Length) && int.TryParse(
                    s: args[(index + 5)],
                    result: out var parsedMin
                ))
                    ? parsedMin
                    : 0x40);
                var lyMax = ((((index + 6) < args.Length) && int.TryParse(
                    s: args[(index + 6)],
                    result: out var parsedMax
                ))
                    ? parsedMax
                    : 0x46);

                StatTrace(
                    romPath: romPath,
                    outputPath: args[(index + 2)],
                    frames: frames,
                    model: model,
                    lyMin: lyMin,
                    lyMax: lyMax
                );

                return true;
            }
        }

        // --render <rom> <out.png> [frames] [dmg|cgb|agb] [--boot puck]: boot a ROM, run N frames, and dump the
        // framebuffer, to eyeball the PPU output. The model defaults to what the cartridge header asks for (CGB flag at
        // 0x0143), so a dual-mode cart renders in color unless "dmg" forces the monochrome costume. Without --boot the
        // machine starts at the seeded post-boot state; --boot puck runs the forge's authored boot ROM from reset.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (string.Equals(
                a: args[index],
                b: "--render",
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                var romPath = args[(index + 1)];
                var frames = ((((index + 3) < args.Length) && int.TryParse(
                    s: args[(index + 3)],
                    result: out var parsedFrames
                ))
                    ? parsedFrames
                    : DefaultRenderFrames);
                var model = ((((index + 4) < args.Length) && TryParseModel(
                    value: args[(index + 4)],
                    model: out var parsedModel
                ))
                    ? parsedModel
                    : ModelFromHeader(rom: File.ReadAllBytes(path: romPath)));

                Render(
                    romPath: romPath,
                    outputPath: args[(index + 2)],
                    frames: frames,
                    model: model,
                    bootRom: AuthoredBootRom(
                        args: args,
                        model: model
                    )
                );

                return true;
            }
        }

        // --dump-snapshot [--frames N] [--rom <path>] [--out <file>]: boot the synthetic ROM (or --rom), run N frames
        // (default 300), and write the raw snapshot image + a sidecar section table to disk — offline cross-build
        // diffing input for C1's zero-byte-shift proof (--hash-divergence has no cross-build mode). Split into its own
        // helper to bound this method's cyclomatic complexity.
        if (Array.IndexOf(
            array: args,
            value: "--dump-snapshot"
        ) >= 0) {
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

        var hashDivergenceIndex = Array.IndexOf(
            array: args,
            value: "--hash-divergence"
        );

        if (hashDivergenceIndex < 0) {
            return false;
        }

        var romAPath = PositionalAfter(
            args: args,
            index: hashDivergenceIndex,
            offset: 1
        );
        // romB is the SECOND positional after the flag, so it only exists once romA was given; without romA (the
        // synthetic-cartridge self-check) a following knob like "--frames 120" must not be mistaken for a ROM path.
        var romBPath = ((romAPath is not null)
            ? PositionalAfter(
            args: args,
            index: hashDivergenceIndex,
            offset: 2
        )
            : null);
        var fine = (Array.IndexOf(
            array: args,
            value: "--fine"
        ) >= 0);
        var framesArg = CommandLineArguments.Value(
            args: args,
            name: "--frames"
        );
        var frames = (((framesArg is not null) && int.TryParse(
            result: out var parsedFrames,
            s: framesArg
        ))
            ? parsedFrames
            : 600);
        var perturbArg = CommandLineArguments.Value(
            args: args,
            name: "--perturb-at"
        );
        var perturbAtFrame = (((perturbArg is not null) && int.TryParse(
            result: out var parsedPerturb,
            s: perturbArg
        ))
            ? parsedPerturb
            : (int?)null);

        exitCode = HashDivergenceProbe.Run(
            fine: fine,
            frames: frames,
            perturbAtFrame: perturbAtFrame,
            romAPath: romAPath,
            romBPath: romBPath
        );

        return true;
    }
    // The positional token `offset` positions after `index`, or null when it is absent or is itself a flag (starts "--").
    private static string? PositionalAfter(string[] args, int index, int offset) =>
        ((((index + offset) < args.Length) && !args[(index + offset)].StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: "--"
    ))
        ? args[(index + offset)]
        : null);
    // The value following a named flag (e.g. --frames 300), or null when the flag is absent or has no following token.
    // A model token is either a family name (which selects that family's target revision) or a revision's own name.
    private static bool TryParseModel(string value, out ConsoleModel model) {
        if (string.Equals(
            a: value,
            b: "dmg",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            model = ConsoleModel.DmgC;

            return true;
        }

        if (string.Equals(
            a: value,
            b: "cgb",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            model = ConsoleModel.CgbE;

            return true;
        }

        if (Enum.TryParse(
            ignoreCase: true,
            result: out model,
            value: value
        ) && Enum.IsDefined(value: model)) {
            return true;
        }

        model = ConsoleModel.DmgC;

        return false;
    }
    // The family's target revision for whichever hardware the cartridge's color flag asks for.
    private static ConsoleModel ModelFromHeader(byte[] rom) =>
        (((rom.Length > 0x0143) && (0 != (rom[0x0143] & 0x80)))
        ? ConsoleModel.CgbE
        : ConsoleModel.DmgC);
    // Warm the machine with the fast Run path, then instruction-step under the clock, attributing each instruction's
    // consumed cycles to halted time when it began halted (a wake instruction lands in the halted bucket — off by one
    // instruction, immaterial at this scale).
    private static void HaltShare(string romPath, int warmFrames, int measureFrames, ConsoleModel model) {
        using var machine = PostMachine.Build(
            model: model,
            rom: File.ReadAllBytes(path: romPath)
        );

        var cpu = machine.GetRequiredService<ICpu>();
        var clock = machine.GetRequiredService<MasterClock>();

        PostMachine.RunFrames(
            frames: warmFrames,
            instance: machine
        );

        var targetCycles = (((ulong)measureFrames) * ((ulong)PostMachine.TCyclesPerFrame));
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
        using var machine = PostMachine.Build(
            model: model,
            rom: File.ReadAllBytes(path: romPath)
        );

        var clock = machine.GetRequiredService<MasterClock>();
        var cpu = machine.GetRequiredService<ICpu>();
        var interrupts = machine.GetRequiredService<IInterruptController>();
        var ppu = machine.GetRequiredService<IPpu>();
        var targetCycles = (((ulong)frames) * ((ulong)PostMachine.TCyclesPerFrame));

        using var writer = new StreamWriter(path: outputPath);

        while (clock.CycleCount < targetCycles) {
            var before = clock.CycleCount;
            var pc = cpu.ProgramCounter;
            var ly = ppu.ReadRegister(address: MemoryMap.LcdY);
            var stat = ppu.ReadRegister(address: MemoryMap.LcdStatus);
            var requested = ((byte)interrupts.Requested);
            var halted = cpu.IsHalted;

            machine.Machine.StepInstruction();

            if (
                ((ly >= lyMin) && (ly <= lyMax)) ||
                (pc < 0x0100)
            ) {
                writer.WriteLine(value: $"{before} pc={pc:X4} ly={ly:X2} stat={stat:X2} if={requested:X2} a={cpu.A:X2} b={cpu.B:X2}{(halted
                    ? " halt"
                    : string.Empty)}");
            }
        }

        Console.WriteLine(value: $"  stat-trace {Path.GetFileName(path: romPath)} ({model}, {frames} frames, ly {lyMin:X2}-{lyMax:X2}) -> {outputPath}");
    }
    // Resolves --boot: the literal "puck" selects the forge's authored image for the model, anything else (including
    // its absence) leaves the machine on the seeded post-boot state.
    private static byte[]? AuthoredBootRom(string[] args, ConsoleModel model) {
        var boot = CommandLineArguments.Value(
            args: args,
            name: "--boot"
        );

        return (string.Equals(
            a: boot,
            b: "puck",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
            ? BootRomBuilder.Build(model: model)
            : null);
    }
    private static void Render(string romPath, string outputPath, int frames, ConsoleModel model, byte[]? bootRom) {
        using var machine = PostMachine.Build(
            bootRom: bootRom,
            model: model,
            rom: File.ReadAllBytes(path: romPath)
        );

        // Frame-at-a-time so the KEY1 speed switch is caught at the frame it happens — the observable that separates
        // "the game runs double-speed on Color hardware" from "the game paces identically on every costume".
        var key1 = machine.GetRequiredService<IKey1>();
        var speedSwitchFrame = -1;

        for (var frame = 0; (frame < frames); ++frame) {
            PostMachine.RunFrames(
                frames: 1,
                instance: machine
            );

            if (
                (speedSwitchFrame < 0) &&
                key1.IsDoubleSpeed
            ) {
                speedSwitchFrame = frame;
            }
        }

        var speedDetail = ((speedSwitchFrame >= 0)
            ? $"double-speed since frame {speedSwitchFrame}"
            : "normal speed throughout");
        var framebuffer = machine.GetRequiredService<IFramebuffer>();
        var pixels = framebuffer.Pixels;
        var rgba = FramebufferRgba.Pack(pixels: pixels);

        PngEncoder.Write(
            path: outputPath,
            rgba: rgba,
            width: framebuffer.Width,
            height: framebuffer.Height
        );

        var pixelHash = Fnv1aHash.Compute(values: MemoryMarshal.AsBytes(span: pixels));

        var bootDetail = ((bootRom is null)
            ? "seeded handoff"
            : "authored boot ROM");

        Console.WriteLine(value: $"  rendered {Path.GetFileName(path: romPath)} ({model}, {bootDetail}, {frames} frames, {speedDetail}) -> {outputPath} [fb-hash 0x{pixelHash:X16}]");
    }
    // Parses --dump-snapshot's knobs, boots the machine, runs the requested frames, and writes the snapshot image plus
    // its section-table sidecar. Returns 2 when --rom names a missing file, otherwise 0.
    private static int DumpSnapshot(string[] args) =>
        SnapshotDumpDiagnostic.Run<MachineSnapshot, MachineIdentity, Tick>(
            args: args,
            capture: static (rom, isSynthetic, frames) => {
                var model = (isSynthetic
                    ? ConsoleModel.DmgC
                    : ModelFromHeader(rom: rom));

                using var machine = PostMachine.Build(
                    model: model,
                    rom: rom
                );

                PostMachine.RunFrames(
                    frames: frames,
                    instance: machine
                );

                return (machine.Machine.Snapshot(), $"{model}, {frames} frames");
            },
            defaultArtifactsSubpath: "gb-post",
            defaultFrames: DefaultDumpSnapshotFrames,
            syntheticRom: static () => SyntheticRom.Create()
        );
}
