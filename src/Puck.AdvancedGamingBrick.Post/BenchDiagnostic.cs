using System.Text;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Runs the fleet benchmark over the native Advanced machine.</summary>
internal static class BenchDiagnostic {
    private const int DefaultFramesPerMachine = 200;

    private static readonly int[] DefaultFleetSizes = [1, 4, 16, 64];

    /// <summary>Runs the bench and writes the report to the console and <c>bench-report.txt</c> in the artifacts
    /// directory.</summary>
    /// <param name="args">The command-line arguments (<c>--bench-rom</c>, <c>--bench-frames</c>,
    /// <c>--bench-fleet</c>, <c>--bench-warmup-frames</c>, <c>--artifacts</c>).</param>
    /// <returns>0 on a clean run; 1 when a determinism guard failed.</returns>
    /// <param name="bios">The explicitly supplied BIOS image.</param>
    public static int Run(string[] args, ReadOnlyMemory<byte> bios) {
        var romPath = CommandLineArguments.Value(
            args: args,
            name: "--bench-rom"
        );
        var frameFloor = (int.TryParse(
            s: CommandLineArguments.Value(
                args: args,
                name: "--bench-frames"
            ),
            result: out var parsedFrames
        )
            ? parsedFrames
            : DefaultFramesPerMachine
        );
        var fleetSizes = BenchDiagnosticFormatting.ParseFleetSizes(
            defaultFleetSizes: DefaultFleetSizes,
            value: CommandLineArguments.Value(
                args: args,
                name: "--bench-fleet"
            )
        );
        var artifactsDirectory = (CommandLineArguments.Value(
            args: args,
            name: "--artifacts"
        ) ?? Path.Combine(
            path1: "artifacts",
            path2: "gba-post"
        ));
        byte[] rom;
        string romName;

        if (!string.IsNullOrEmpty(value: romPath)) {
            rom = File.ReadAllBytes(path: romPath);
            romName = Path.GetFileName(path: romPath);
        } else {
            // No corpus needed: the same zero-asset synthetic cartridge the throughput stage runs.
            rom = SyntheticRom.Create();
            romName = "synthetic";
        }

        var report = new StringBuilder();
        var warmupFrames = CommandLineArguments.Value(
            args: args,
            name: "--bench-warmup-frames"
        );
        AgbMachineSnapshot? initialState = null;

        if (warmupFrames is not null) {
            if (
                !int.TryParse(
                result: out var warmup,
                s: warmupFrames
            ) ||
                (warmup < 0)
            ) {
                Console.Error.WriteLine(value: "--bench-warmup-frames must be a nonnegative integer.");
                return 2;
            }

            using var subject = PostMachine.Build(
                bios: bios,
                rom: rom
            );

            subject.RunFrames(frames: warmup);
            initialState = subject.Machine.Snapshot();
        }

        BenchDiagnosticFormatting.Line(
            report: report,
            text: $"machine-fleet bench (AGB) — {romName}, frame floor {frameFloor}/machine, {Environment.ProcessorCount} logical processors"
        );
        BenchDiagnosticFormatting.Line(
            report: report,
            text: $"runtime {Environment.Version}; warm-up {(warmupFrames ?? "0")} frames with keys released before each measured fleet cell; audio output disabled"
        );

        return MachineBenchmark.Run<BenchmarkSubject, AgbMachineSnapshot>(
            build: () => new(subject: PostMachine.Build(bios: bios, rom: rom)),
            fleetSizes: fleetSizes,
            frameFloor: frameFloor,
            hardwareFps: PostMachine.HardwareFps,
            report: report,
            artifactsDirectory: artifactsDirectory,
            initialState: initialState,
            reportDivergenceDetails: true
        );
    }

    private readonly struct BenchmarkSubject(PostMachine subject) : IMachineBenchmarkSubject<AgbMachineSnapshot> {
        public void PrepareInput() { }
        public static bool SnapshotsEqual(AgbMachineSnapshot a, AgbMachineSnapshot b) => a.ContentEquals(other: b);
        public static int SnapshotSize(AgbMachineSnapshot snapshot) => snapshot.Size;
        public AgbMachineSnapshot Snapshot() => subject.Machine.Snapshot();
        public void Restore(AgbMachineSnapshot snapshot) => subject.Machine.Restore(snapshot: snapshot);
        public IDisposable Fork() => subject.Fork();
        public void Dispose() => subject.Dispose();
        public void RunFrames(int frames) => subject.RunFrames(frames: frames);
        public void RunFrame(int stream, int frame) {
            // KEYINPUT is active-low; the script walks all ten button bits.
            var pressed = ((ushort)(((frame * 37) + (stream * 11)) & 0x3FF));

            subject.Machine.SetKeyInput(keys: ((ushort)(0x3FF & ~pressed)));
            _ = subject.Machine.RunFrame();
        }
    }
}
