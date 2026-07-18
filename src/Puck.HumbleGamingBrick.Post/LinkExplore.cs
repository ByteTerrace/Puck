using Puck.Capture;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The interactive link explorer — the investigative tool that authors the frozen scripts the cross-generation
/// link-game gate replays. It boots one or two commercial ROMs under text <see cref="LinkInputScript"/>s, advances them
/// (a lone machine directly, a pair through <see cref="LinkReplay"/>/<see cref="SerialLinkSession"/>), and dumps each
/// side's framebuffer every N frames with a hash — so an operator can watch a scripted menu walk reach the link
/// handshake and read back how much serial traffic crossed. It is a diagnostic, not a self-checking stage (mirroring
/// <c>--render</c>/<c>--stat-trace</c>). Gate-ready keyframes live in <see cref="LinkGameReplayStage"/>; this tool remains
/// the interactive authoring and inspection surface.
/// </summary>
internal static class LinkExplore {
    /// <summary>Dispatches <c>--link-explore</c>. Usage:
    /// <c>--link-explore &lt;romA&gt; &lt;scriptA&gt; [&lt;romB&gt; &lt;scriptB&gt;] --frames N --dump-every M --out DIR
    /// [--modelA cgb] [--modelB agb]</c>. With only romA/scriptA a lone machine is driven; with a second pair the two
    /// are linked. Returns false (battery runs) when the flag is absent.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="exitCode">The exit code (0).</param>
    /// <returns><see langword="true"/> when the flag was handled.</returns>
    public static bool TryRun(string[] args, out int exitCode) {
        exitCode = 0;

        var index = Array.IndexOf(array: args, value: "--link-explore");

        if (index < 0) {
            return false;
        }

        var positionals = Positionals(args: args, afterIndex: index);
        var frames = IntArg(args: args, name: "--frames", fallback: 900);
        var dumpEvery = IntArg(args: args, name: "--dump-every", fallback: 60);
        var outDir = (StringArg(args: args, name: "--out") ?? ".");
        var modelA = ModelArg(args: args, name: "--modelA", fallback: ConsoleModel.Cgb);
        var modelB = ModelArg(args: args, name: "--modelB", fallback: ConsoleModel.Agb);

        Directory.CreateDirectory(path: outDir);

        if (positionals.Count >= 4) {
            RunLinked(
                romAPath: positionals[0], scriptAPath: positionals[1], modelA: modelA,
                romBPath: positionals[2], scriptBPath: positionals[3], modelB: modelB,
                frames: frames, dumpEvery: dumpEvery, outDir: outDir
            );
        } else if (positionals.Count >= 2) {
            RunLone(romPath: positionals[0], scriptPath: positionals[1], model: modelA, frames: frames, dumpEvery: dumpEvery, outDir: outDir);
        } else {
            Console.WriteLine(value: "  --link-explore needs at least <rom> <script>");
        }

        return true;
    }

    private static void RunLone(string romPath, string scriptPath, ConsoleModel model, int frames, int dumpEvery, string outDir) {
        using var machine = PostMachine.Build(model: model, rom: File.ReadAllBytes(path: romPath));

        var script = LinkInputScript.Load(path: scriptPath);
        var joypad = machine.GetRequiredService<IJoypad>();
        var tag = Path.GetFileNameWithoutExtension(path: romPath);

        Console.WriteLine(value: $"== link-explore (lone {model}) {tag}, {frames} frames, dump every {dumpEvery} ==");

        for (var frame = 0; (frame < frames); ++frame) {
            joypad.SetButtons(pressed: script.ButtonsAt(frame: frame));
            machine.Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);

            if ((((frame + 1) % dumpEvery) == 0) || ((frame + 1) == frames)) {
                Dump(machine: machine, outDir: outDir, tag: tag, frame: (frame + 1));
            }
        }
    }
    private static void RunLinked(
        string romAPath, string scriptAPath, ConsoleModel modelA,
        string romBPath, string scriptBPath, ConsoleModel modelB,
        int frames, int dumpEvery, string outDir
    ) {
        using var machineA = PostMachine.Build(model: modelA, rom: File.ReadAllBytes(path: romAPath));
        using var machineB = PostMachine.Build(model: modelB, rom: File.ReadAllBytes(path: romBPath));

        var scriptA = LinkInputScript.Load(path: scriptAPath);
        var scriptB = LinkInputScript.Load(path: scriptBPath);
        var tagA = $"A-{Path.GetFileNameWithoutExtension(path: romAPath)}-{modelA}";
        var tagB = $"B-{Path.GetFileNameWithoutExtension(path: romBPath)}-{modelB}";

        Console.WriteLine(value: $"== link-explore (linked {modelA}<->{modelB}), {frames} frames, dump every {dumpEvery} ==");

        var result = LinkReplay.Run(
            first: machineA, firstScript: scriptA, second: machineB, secondScript: scriptB, frames: frames,
            onFrame: frame => {
                if ((((frame + 1) % dumpEvery) == 0) || ((frame + 1) == frames)) {
                    Dump(machine: machineA, outDir: outDir, tag: tagA, frame: (frame + 1));
                    Dump(machine: machineB, outDir: outDir, tag: tagB, frame: (frame + 1));
                }
            }
        );

        Console.WriteLine(value: $"  A: masterSends={result.First.MasterSends} completions={result.First.Completions} trafficHash=0x{result.First.TrafficHash:X16}");
        Console.WriteLine(value: $"  B: masterSends={result.Second.MasterSends} completions={result.Second.Completions} trafficHash=0x{result.Second.TrafficHash:X16}");
    }
    private static void Dump(MachineInstance machine, string outDir, string tag, int frame) {
        var framebuffer = machine.GetRequiredService<IFramebuffer>();
        var pixels = framebuffer.Pixels;
        var rgba = new byte[(pixels.Length * 4)];

        for (var pixel = 0; (pixel < pixels.Length); ++pixel) {
            var offset = (pixel * 4);
            var value = pixels[pixel];

            rgba[offset] = (byte)(value >> 16);
            rgba[(offset + 1)] = (byte)(value >> 8);
            rgba[(offset + 2)] = (byte)value;
            rgba[(offset + 3)] = 0xFF;
        }

        var path = Path.Combine(path1: outDir, path2: $"{tag}_{frame:D5}.png");

        PngEncoder.Write(path: path, rgba: rgba, width: framebuffer.Width, height: framebuffer.Height);
        Console.WriteLine(value: $"    [{frame:D5}] {tag} -> {Path.GetFileName(path: path)} (fb 0x{HashPixels(pixels: pixels):X16})");
    }
    private static ulong HashPixels(ReadOnlySpan<uint> pixels) {
        var hash = 14_695_981_039_346_656_037ul;

        foreach (var pixel in pixels) {
            hash = ((hash ^ pixel) * 1_099_511_628_211ul);
        }

        return hash;
    }
    private static List<string> Positionals(string[] args, int afterIndex) {
        var positionals = new List<string>();

        for (var index = (afterIndex + 1); (index < args.Length); ++index) {
            if (args[index].StartsWith(value: "--", comparisonType: StringComparison.Ordinal)) {
                break;
            }

            positionals.Add(item: args[index]);
        }

        return positionals;
    }
    private static string? StringArg(string[] args, string name) {
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return args[(index + 1)];
            }
        }

        return null;
    }
    private static int IntArg(string[] args, string name, int fallback) {
        var value = StringArg(args: args, name: name);

        return (((value is not null) && int.TryParse(s: value, result: out var parsed)) ? parsed : fallback);
    }
    private static ConsoleModel ModelArg(string[] args, string name, ConsoleModel fallback) {
        var value = StringArg(args: args, name: name);

        return value?.ToLowerInvariant() switch {
            "dmg" => ConsoleModel.Dmg,
            "cgb" => ConsoleModel.Cgb,
            "agb" => ConsoleModel.Agb,
            _ => fallback,
        };
    }
}
