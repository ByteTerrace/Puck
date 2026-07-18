using System.Diagnostics;
using System.Text;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// The machine-fleet bench (<c>--bench</c>) — the Advanced-core counterpart to
/// <c>Puck.HumbleGamingBrick.Post.BenchDiagnostic</c>, mirrored row-for-row so the two cores are measured the same way.
/// One run reports: the fleet scaling curve in BOTH shapes (independent per-machine input streams and one shared choir
/// stream), single- and multi-threaded; the burst catch-up rate (the simulate-on-demand dormancy budget); and
/// <c>Create</c>/<c>Snapshot</c>/<c>Restore</c>/<c>Fork</c> latency and allocation. It is a measurement, not a gate —
/// but every fleet cell ends with a same-stream snapshot compare and the multi-threaded cell must end bit-identical to
/// the single-threaded one, so a bench run that breaks determinism exits 1 instead of reporting quietly.
/// </summary>
internal static class BenchDiagnostic {
    /// <summary>The measured-frame floor per machine; low fleet sizes get more frames (see <see cref="FramesFor"/>)
    /// so the small-N cells are not noise-dominated. 200 is the repo's aggregate-throughput measurement floor
    /// (docs/reviews §4) — well above the ±4&#160;ms native-channel noise the fleet plan distrusts.</summary>
    private const int DefaultFramesPerMachine = 200;
    private const int LatencyReps = 64;
    /// <summary>Frames a machine runs before its state is considered representative for snapshot/restore/fork
    /// measurement.</summary>
    private const int WarmFrames = 120;
    private const int BurstFrames = 600;

    private static readonly int[] DefaultFleetSizes = [1, 4, 16, 64];

    /// <summary>Runs the bench and writes the report to the console and <c>bench-report.txt</c> in the artifacts
    /// directory.</summary>
    /// <param name="args">The command-line arguments (<c>--bench-rom</c>, <c>--bench-frames</c>,
    /// <c>--bench-fleet</c>, <c>--artifacts</c>).</param>
    /// <returns>0 on a clean run; 1 when a determinism guard failed.</returns>
    public static int Run(string[] args) {
        var romPath = ArgValue(args: args, name: "--bench-rom");
        var frameFloor = (int.TryParse(s: ArgValue(args: args, name: "--bench-frames"), result: out var parsedFrames) ? parsedFrames : DefaultFramesPerMachine);
        var fleetSizes = ParseFleetSizes(value: ArgValue(args: args, name: "--bench-fleet"));
        var artifactsDirectory = (ArgValue(args: args, name: "--artifacts") ?? Path.Combine(path1: "artifacts", path2: "gba-post"));
        var bios = Diagnostics.BiosImage;
        byte[] rom;
        string romName;

        if (!string.IsNullOrEmpty(value: romPath)) {
            rom = File.ReadAllBytes(path: romPath);
            romName = Path.GetFileName(path: romPath);
        } else {
            // No corpus needed: the same zero-asset synthetic cartridge ThroughputStage runs.
            rom = SyntheticRom.Create();
            romName = "synthetic";
        }

        var report = new StringBuilder();
        var determinismHeld = true;

        Line(report: report, text: $"machine-fleet bench (AGB) — {romName}, frame floor {frameFloor}/machine, {Environment.ProcessorCount} logical processors");

        // Discarded warm-up fleets so JIT tiering settles before anything is measured.
        RunFleet(bios: bios, rom: rom, count: 2, frames: 30, choir: false, parallel: false);
        RunFleet(bios: bios, rom: rom, count: 2, frames: 30, choir: false, parallel: true);

        Line(report: report, text: "");
        Line(report: report, text: "fleet scaling, machine-frames/s (rt = machines sustainable at realtime):");
        Line(report: report, text: $"{"n",5}  {"independent-1t",18}  {"independent-mt",18}  {"choir-1t",18}  {"choir-mt",18}");

        foreach (var count in fleetSizes) {
            var frames = FramesFor(count: count, frameFloor: frameFloor);

            // A clean heap per row so one cell's garbage is not another cell's pause.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var independentSingle = RunFleet(bios: bios, rom: rom, count: count, frames: frames, choir: false, parallel: false);
            var independentParallel = RunFleet(bios: bios, rom: rom, count: count, frames: frames, choir: false, parallel: true);
            var choirSingle = RunFleet(bios: bios, rom: rom, count: count, frames: frames, choir: true, parallel: false);
            var choirParallel = RunFleet(bios: bios, rom: rom, count: count, frames: frames, choir: true, parallel: true);

            // Every cell consumed stream 0 on machine 0, so all four anchors must be byte-identical — this is the
            // serial-vs-parallel (and shape-vs-shape) bit-lock guard.
            var pairsMatched = (independentSingle.PairMatched && independentParallel.PairMatched && choirSingle.PairMatched && choirParallel.PairMatched);
            var serialVsParallel = independentSingle.Anchor.ContentEquals(other: independentParallel.Anchor);
            var independentVsChoir = independentSingle.Anchor.ContentEquals(other: choirSingle.Anchor);
            var choirSerialVsParallel = independentSingle.Anchor.ContentEquals(other: choirParallel.Anchor);
            var cellHeld = (pairsMatched && serialVsParallel && independentVsChoir && choirSerialVsParallel);

            determinismHeld &= cellHeld;

            Line(
                report: report,
                text: $"{count,5}  {Cell(cell: independentSingle),18}  {Cell(cell: independentParallel),18}  {Cell(cell: choirSingle),18}  {Cell(cell: choirParallel),18}{(cellHeld ? "" : "  << DETERMINISM BROKEN")}"
            );

            if (!cellHeld) {
                if (!pairsMatched) {
                    Line(report: report, text: $"    !! same-stream pair mismatch (machine 0 vs last machine) at fleet size {count}");
                }
                if (!serialVsParallel) {
                    Line(report: report, text: $"    !! serial vs parallel divergence at fleet size {count} (independent stream)");
                }
                if (!independentVsChoir) {
                    Line(report: report, text: $"    !! independent vs choir divergence at fleet size {count} (serial)");
                }
                if (!choirSerialVsParallel) {
                    Line(report: report, text: $"    !! serial vs parallel divergence at fleet size {count} (choir stream)");
                }
            }
        }

        // Burst catch-up: the dormancy model's budget — a frozen machine fast-forwarding its elapsed span. One
        // machine uncapped, and one machine per logical processor all catching up at once.
        var burstSingle = RunFleet(bios: bios, rom: rom, count: 1, frames: BurstFrames, choir: false, parallel: false);
        var burstFleet = RunFleet(bios: bios, rom: rom, count: Environment.ProcessorCount, frames: (BurstFrames / 2), choir: false, parallel: true);
        var singleMultiple = (burstSingle.MachineFramesPerSecond / PostMachine.HardwareFps);
        var fleetPerMachineMultiple = ((burstFleet.MachineFramesPerSecond / Environment.ProcessorCount) / PostMachine.HardwareFps);

        Line(report: report, text: "");
        Line(report: report, text: "burst catch-up (simulate-on-demand dormancy):");
        Line(report: report, text: $"  one machine: {burstSingle.MachineFramesPerSecond:F0} machine-frames/s = {singleMultiple:F1}x realtime; one dormant hour replays in {(3_600.0 / singleMultiple):F1} s");
        Line(report: report, text: $"  {Environment.ProcessorCount} machines in parallel: {burstFleet.MachineFramesPerSecond:F0} machine-frames/s aggregate = {fleetPerMachineMultiple:F1}x realtime each");

        if (!burstFleet.PairMatched) {
            Line(report: report, text: "    !! burst-fleet same-stream pair mismatch");
        }

        determinismHeld &= burstFleet.PairMatched;

        MeasureLatencies(bios: bios, rom: rom, report: report);

        Line(report: report, text: "");
        Line(report: report, text: (determinismHeld ? "determinism guards: all held (same-stream pairs + serial-vs-parallel anchors byte-identical)" : "determinism guards: FAILED — a same-stream pair or a serial-vs-parallel anchor diverged"));

        Directory.CreateDirectory(path: artifactsDirectory);

        var reportPath = Path.Combine(path1: artifactsDirectory, path2: "bench-report.txt");

        File.WriteAllText(path: reportPath, contents: report.ToString());
        Console.WriteLine(value: $"  bench report -> {reportPath}");

        return (determinismHeld ? 0 : 1);
    }

    /// <summary>One measured fleet cell. <c>Anchor</c> is machine 0's final snapshot; <c>PairMatched</c> is the
    /// same-stream honesty check (machine 0 vs the last machine, which always consumes stream 0).</summary>
    private sealed record FleetCell(double MachineFramesPerSecond, AgbMachineSnapshot Anchor, bool PairMatched);

    private static FleetCell RunFleet(ReadOnlyMemory<byte> bios, byte[] rom, int count, int frames, bool choir, bool parallel) {
        var machines = new PostMachine[count];

        for (var index = 0; (index < count); ++index) {
            machines[index] = PostMachine.Build(bios: bios, rom: rom);
        }

        var stopwatch = Stopwatch.StartNew();

        if (parallel) {
            // Task-per-machine, no per-frame barrier: input is a pure function of (stream, frame) and machines share
            // nothing, so each one can run its whole span straight through.
            Parallel.For(fromInclusive: 0, toExclusive: count, body: index => {
                var machine = machines[index].Machine;
                var stream = StreamFor(index: index, count: count, choir: choir);

                for (var frame = 0; (frame < frames); ++frame) {
                    machine.SetKeyInput(keys: KeyInputFor(stream: stream, frame: frame));
                    _ = machine.RunFrame();
                }
            });
        } else {
            // Frame-at-a-time round-robin — the shape of today's serial stepping on the render thread.
            for (var frame = 0; (frame < frames); ++frame) {
                for (var index = 0; (index < count); ++index) {
                    var machine = machines[index].Machine;

                    machine.SetKeyInput(keys: KeyInputFor(stream: StreamFor(index: index, count: count, choir: choir), frame: frame));
                    _ = machine.RunFrame();
                }
            }
        }

        stopwatch.Stop();

        var anchor = machines[0].Machine.Snapshot();
        var pairMatched = ((count < 2) || anchor.ContentEquals(other: machines[(count - 1)].Machine.Snapshot()));

        foreach (var machine in machines) {
            machine.Dispose();
        }

        return new FleetCell(
            MachineFramesPerSecond: (((double)count * frames) / stopwatch.Elapsed.TotalSeconds),
            Anchor: anchor,
            PairMatched: pairMatched
        );
    }
    private static void MeasureLatencies(ReadOnlyMemory<byte> bios, byte[] rom, StringBuilder report) {
        Line(report: report, text: "");
        Line(report: report, text: $"per-operation latency (mean over {LatencyReps} reps) + managed allocation:");

        // Create: a full DI container per machine — the spawn-on-stumble / pooling-lever number.
        var createTicks = 0L;
        var createBytes = GC.GetAllocatedBytesForCurrentThread();

        for (var rep = 0; (rep < LatencyReps); ++rep) {
            var start = Stopwatch.GetTimestamp();
            var machine = PostMachine.Build(bios: bios, rom: rom);

            createTicks += (Stopwatch.GetTimestamp() - start);
            machine.Dispose();
        }

        createBytes = ((GC.GetAllocatedBytesForCurrentThread() - createBytes) / LatencyReps);
        Line(report: report, text: $"  Create   {TicksToMicroseconds(ticks: (createTicks / LatencyReps)),10:F1} us  {createBytes,10:N0} B");

        using var subject = PostMachine.Build(bios: bios, rom: rom);

        subject.RunFrames(frames: WarmFrames);

        // Snapshot: the ghost-echo / bottled-moment / freeze-to-dormant cost.
        var snapshotTicks = 0L;
        var snapshotBytes = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = subject.Machine.Snapshot();

        for (var rep = 0; (rep < LatencyReps); ++rep) {
            var start = Stopwatch.GetTimestamp();

            snapshot = subject.Machine.Snapshot();
            snapshotTicks += (Stopwatch.GetTimestamp() - start);
        }

        snapshotBytes = ((GC.GetAllocatedBytesForCurrentThread() - snapshotBytes) / LatencyReps);
        Line(report: report, text: $"  Snapshot {TicksToMicroseconds(ticks: (snapshotTicks / LatencyReps)),10:F1} us  {snapshotBytes,10:N0} B  (snapshot size {snapshot.Size:N0} B)");

        // Restore: the wake-from-dormant / promote-demote-arrival cost.
        var restoreTicks = 0L;
        var restoreBytes = GC.GetAllocatedBytesForCurrentThread();

        for (var rep = 0; (rep < LatencyReps); ++rep) {
            var start = Stopwatch.GetTimestamp();

            subject.Machine.Restore(snapshot: snapshot);
            restoreTicks += (Stopwatch.GetTimestamp() - start);
        }

        restoreBytes = ((GC.GetAllocatedBytesForCurrentThread() - restoreBytes) / LatencyReps);
        Line(report: report, text: $"  Restore  {TicksToMicroseconds(ticks: (restoreTicks / LatencyReps)),10:F1} us  {restoreBytes,10:N0} B");

        // Fork: Create + Snapshot + Restore in one call — the counterfactual/ghost-spawn cost.
        var forkTicks = 0L;
        var forkBytes = GC.GetAllocatedBytesForCurrentThread();

        for (var rep = 0; (rep < LatencyReps); ++rep) {
            var start = Stopwatch.GetTimestamp();
            var fork = subject.Fork();

            forkTicks += (Stopwatch.GetTimestamp() - start);
            fork.Dispose();
        }

        forkBytes = ((GC.GetAllocatedBytesForCurrentThread() - forkBytes) / LatencyReps);
        Line(report: report, text: $"  Fork     {TicksToMicroseconds(ticks: (forkTicks / LatencyReps)),10:F1} us  {forkBytes,10:N0} B");
    }

    /// <summary>The input stream a machine consumes: the choir shares stream 0; independent machines get their own
    /// stream, except the LAST machine, which always mirrors stream 0 so every cell carries a same-stream pair for
    /// the determinism guard.</summary>
    private static int StreamFor(int index, int count, bool choir) =>
        ((choir || (index == (count - 1))) ? 0 : index);

    /// <summary>A deterministic, edge-rich KEYINPUT script (the trio-lockstep pattern): odd multipliers walk all
    /// 10 button bits, offset per stream so independent machines genuinely diverge. KEYINPUT is active-low, so the
    /// walked pattern is inverted before it is written.</summary>
    private static ushort KeyInputFor(int stream, int frame) {
        var pressed = (ushort)(((frame * 37) + (stream * 11)) & 0x3FF);

        return (ushort)(0x3FF & ~pressed);
    }

    /// <summary>Small fleets get more frames so their cells are not stopwatch noise; the emulated span per cell
    /// stays roughly level until the floor takes over.</summary>
    private static int FramesFor(int count, int frameFloor) =>
        Math.Max(val1: frameFloor, val2: (960 / count));
    private static string Cell(FleetCell cell) =>
        $"{cell.MachineFramesPerSecond,8:F0} ({(cell.MachineFramesPerSecond / PostMachine.HardwareFps),5:F1} rt)";
    private static double TicksToMicroseconds(long ticks) =>
        ((ticks * 1_000_000.0) / Stopwatch.Frequency);
    private static void Line(StringBuilder report, string text) {
        report.AppendLine(value: text);
        Console.WriteLine(value: text);
    }
    private static int[] ParseFleetSizes(string? value) {
        if (string.IsNullOrEmpty(value: value)) {
            return DefaultFleetSizes;
        }

        return Array.ConvertAll(array: value.Split(separator: ','), converter: static size => int.Parse(s: size));
    }
    private static string? ArgValue(string[] args, string name) {
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return args[(index + 1)];
            }
        }

        return null;
    }
}
