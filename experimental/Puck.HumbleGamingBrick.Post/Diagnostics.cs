using Puck.Capture;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The diagnostic surface of the GB/GBC POST: single-ROM inspectors dispatched from CLI flags before the battery runs,
/// mirroring the GBA POST's toolbox. These are investigative tools, not self-checking stages; <see cref="TryRun"/>
/// dispatches them so the battery stays the default.
/// </summary>
internal static class Diagnostics {
    /// <summary>The frame budget a render runs when none is given — ten seconds of emulated time, enough for a
    /// commercial ROM to clear its logo screens and start drawing.</summary>
    private const int DefaultRenderFrames = 600;

    /// <summary>Dispatches the diagnostic CLI flags — each runs a single investigative mode and returns; when none
    /// matches, the caller proceeds to the POST battery.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="exitCode">The exit code the handled mode produced (0 when it does not gate).</param>
    /// <returns><see langword="true"/> when a diagnostic flag was handled (return <paramref name="exitCode"/>, skip the
    /// battery); otherwise <see langword="false"/>.</returns>
    public static bool TryRun(string[] args, out int exitCode) {
        exitCode = 0;

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
                var romPath = args[index + 1];
                var warmFrames = ((((index + 2) < args.Length) && int.TryParse(s: args[index + 2], result: out var parsedWarm)) ? parsedWarm : 300);
                var measureFrames = ((((index + 3) < args.Length) && int.TryParse(s: args[index + 3], result: out var parsedMeasure)) ? parsedMeasure : 300);
                var model = ((((index + 4) < args.Length) && TryParseModel(value: args[index + 4], model: out var parsedModel))
                    ? parsedModel
                    : ModelFromHeader(rom: File.ReadAllBytes(path: romPath)));

                HaltShare(romPath: romPath, warmFrames: warmFrames, measureFrames: measureFrames, model: model);

                return true;
            }
        }

        // --render <rom> <out.png> [frames] [dmg|cgb|agb]: boot a ROM (no boot ROM, seeded post-boot state), run N frames,
        // and dump the framebuffer, to eyeball the PPU output. The model defaults to what the cartridge header asks
        // for (CGB flag at 0x0143), so a dual-mode cart renders in color unless "dmg" forces the monochrome costume.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (string.Equals(a: args[index], b: "--render", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                var romPath = args[index + 1];
                var frames = ((((index + 3) < args.Length) && int.TryParse(s: args[index + 3], result: out var parsedFrames))
                    ? parsedFrames
                    : DefaultRenderFrames);
                var model = ((((index + 4) < args.Length) && TryParseModel(value: args[index + 4], model: out var parsedModel))
                    ? parsedModel
                    : ModelFromHeader(rom: File.ReadAllBytes(path: romPath)));

                Render(romPath: romPath, outputPath: args[index + 2], frames: frames, model: model);

                return true;
            }
        }

        return false;
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
        var rgba = new byte[pixels.Length * 4];

        // The framebuffer packs 0x00RRGGBB; the encoder wants R,G,B,A bytes, so repack with an opaque alpha.
        for (var index = 0; (index < pixels.Length); ++index) {
            var offset = (index * 4);
            var pixel = pixels[index];

            rgba[offset] = (byte)(pixel >> 16);
            rgba[offset + 1] = (byte)(pixel >> 8);
            rgba[offset + 2] = (byte)pixel;
            rgba[offset + 3] = 0xFF;
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
}
