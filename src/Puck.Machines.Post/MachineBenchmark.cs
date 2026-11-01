using System.Diagnostics;
using System.Text;
using Puck.Abstractions.Counting;

namespace Puck.Machines.Post;

/// <summary>Measures machine fleets and lifecycle operations with same-stream and serial/parallel determinism guards.</summary>
public static class MachineBenchmark {
    private const int BurstFrames = 600;
    private const int LatencyReps = 64;
    private const int WarmFrames = 120;

    /// <summary>Balances the emulated span of small fleets against the caller's per-machine floor.</summary>
    /// <param name="count">The fleet size.</param>
    /// <param name="frameFloor">The minimum frame count per machine.</param>
    /// <returns>The frame count for each machine.</returns>
    public static int FramesFor(int count, int frameFloor) => Math.Max(val1: frameFloor, val2: (960 / count));
    /// <summary>Runs the homogeneous fleet benchmark and writes its accumulated report.</summary>
    /// <typeparam name="TMachine">The owning machine adapter.</typeparam>
    /// <typeparam name="TSnapshot">The immutable machine state.</typeparam>
    /// <param name="build">Creates one isolated machine.</param>
    /// <param name="fleetSizes">The fleet sizes in report order.</param>
    /// <param name="frameFloor">The minimum frame count per machine.</param>
    /// <param name="hardwareFps">The hardware frame rate used to report realtime multiples.</param>
    /// <param name="report">The report, including the caller's machine-specific banner.</param>
    /// <param name="artifactsDirectory">The directory receiving bench-report.txt.</param>
    /// <param name="initialState">An optional state restored before each measured fleet cell.</param>
    /// <param name="reportDivergenceDetails">Whether to print each failed anchor comparison.</param>
    /// <param name="extraMeasurements">Optional additional report sections; returns whether their determinism guards held.</param>
    /// <returns>Zero when every determinism guard held, otherwise one.</returns>
    public static int Run<TMachine, TSnapshot>(Func<TMachine> build, int[] fleetSizes, int frameFloor,
        double hardwareFps, StringBuilder report, string artifactsDirectory, TSnapshot? initialState = null,
        bool reportDivergenceDetails = false, Func<StringBuilder, bool>? extraMeasurements = null)
        where TMachine : struct, IMachineBenchmarkSubject<TSnapshot>
        where TSnapshot : class {
        RunFleet<TMachine, TSnapshot>(build: build, count: 2, frames: 30, choir: false, parallel: false);
        RunFleet<TMachine, TSnapshot>(build: build, count: 2, frames: 30, choir: false, parallel: true);

        BenchDiagnosticFormatting.Line(report: report, text: "");
        BenchDiagnosticFormatting.Line(report: report, text: "fleet scaling, machine-frames/s (rt = machines sustainable at realtime):");
        BenchDiagnosticFormatting.Line(report: report, text: $"{"n",5}  {"independent-1t",18}  {"independent-mt",18}  {"choir-1t",18}  {"choir-mt",18}");
        var determinismHeld = true;

        foreach (var count in fleetSizes) {
            var frames = FramesFor(count: count, frameFloor: frameFloor);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var independentSingle = RunFleet<TMachine, TSnapshot>(build: build, choir: false, count: count, frames: frames, initialState: initialState, parallel: false);
            var independentParallel = RunFleet<TMachine, TSnapshot>(build: build, choir: false, count: count, frames: frames, initialState: initialState, parallel: true);
            var choirSingle = RunFleet<TMachine, TSnapshot>(build: build, choir: true, count: count, frames: frames, initialState: initialState, parallel: false);
            var choirParallel = RunFleet<TMachine, TSnapshot>(build: build, choir: true, count: count, frames: frames, initialState: initialState, parallel: true);

            // Every cell gives machine zero stream zero, independent of execution shape.
            var pairsMatched = (independentSingle.PairMatched && independentParallel.PairMatched && choirSingle.PairMatched && choirParallel.PairMatched);
            var serialVsParallel = TMachine.SnapshotsEqual(a: independentSingle.Anchor, b: independentParallel.Anchor);
            var independentVsChoir = TMachine.SnapshotsEqual(a: independentSingle.Anchor, b: choirSingle.Anchor);
            var choirSerialVsParallel = TMachine.SnapshotsEqual(a: independentSingle.Anchor, b: choirParallel.Anchor);
            var rowHeld = (pairsMatched && serialVsParallel && independentVsChoir && choirSerialVsParallel);

            determinismHeld &= rowHeld;

            BenchDiagnosticFormatting.Line(report: report,
                text: $"{count,5}  {Cell(cell: independentSingle, hardwareFps: hardwareFps),18}  {Cell(cell: independentParallel, hardwareFps: hardwareFps),18}  {Cell(cell: choirSingle, hardwareFps: hardwareFps),18}  {Cell(cell: choirParallel, hardwareFps: hardwareFps),18}{(rowHeld ? "" : "  << DETERMINISM BROKEN")}");

            if (reportDivergenceDetails && !rowHeld) {
                if (!pairsMatched) {
                    BenchDiagnosticFormatting.Line(report: report, text: $"    !! same-stream pair mismatch (machine 0 vs last machine) at fleet size {count}");
                }
                if (!serialVsParallel) {
                    BenchDiagnosticFormatting.Line(report: report, text: $"    !! serial vs parallel divergence at fleet size {count} (independent stream)");
                }
                if (!independentVsChoir) {
                    BenchDiagnosticFormatting.Line(report: report, text: $"    !! independent vs choir divergence at fleet size {count} (serial)");
                }
                if (!choirSerialVsParallel) {
                    BenchDiagnosticFormatting.Line(report: report, text: $"    !! serial vs parallel divergence at fleet size {count} (choir stream)");
                }
            }
        }

        var burstSingle = RunFleet<TMachine, TSnapshot>(build: build, choir: false, count: 1, frames: BurstFrames, initialState: initialState, parallel: false);
        var burstFleet = RunFleet<TMachine, TSnapshot>(build: build, count: Environment.ProcessorCount, frames: (BurstFrames / 2), choir: false, parallel: true, initialState: initialState);
        var singleMultiple = (burstSingle.MachineFramesPerSecond / hardwareFps);
        var fleetPerMachineMultiple = ((burstFleet.MachineFramesPerSecond / Environment.ProcessorCount) / hardwareFps);

        BenchDiagnosticFormatting.Line(report: report, text: "");
        BenchDiagnosticFormatting.Line(report: report, text: "burst catch-up (simulate-on-demand dormancy):");
        BenchDiagnosticFormatting.Line(report: report, text: $"  one machine: {burstSingle.MachineFramesPerSecond:F0} machine-frames/s = {singleMultiple:F1}x realtime; one dormant hour replays in {(3_600.0 / singleMultiple):F1} s");
        BenchDiagnosticFormatting.Line(report: report, text: $"  {Environment.ProcessorCount} machines in parallel: {burstFleet.MachineFramesPerSecond:F0} machine-frames/s aggregate = {fleetPerMachineMultiple:F1}x realtime each");
        if (reportDivergenceDetails && !burstFleet.PairMatched) {
            BenchDiagnosticFormatting.Line(report: report, text: "    !! burst-fleet same-stream pair mismatch");
        }
        determinismHeld &= burstFleet.PairMatched;

        MeasureLatencies<TMachine, TSnapshot>(build: build, report: report);
        if (extraMeasurements is not null) {
            determinismHeld &= extraMeasurements(arg: report);
        }

        BenchDiagnosticFormatting.Line(report: report, text: "");
        BenchDiagnosticFormatting.Line(report: report, text: (determinismHeld
            ? "determinism guards: all held (same-stream pairs + serial-vs-parallel anchors byte-identical)"
            : "determinism guards: FAILED — a same-stream pair or a serial-vs-parallel anchor diverged"));
        Directory.CreateDirectory(path: artifactsDirectory);
        var reportPath = Path.Combine(path1: artifactsDirectory, path2: "bench-report.txt");

        File.WriteAllText(path: reportPath, contents: report.ToString());
        Console.WriteLine(value: $"  bench report -> {reportPath}");
        return (determinismHeld ? 0 : 1);
    }

    private static string Cell<TSnapshot>(FleetCell<TSnapshot> cell, double hardwareFps) =>
        $"{cell.MachineFramesPerSecond,8:F0} ({(cell.MachineFramesPerSecond / hardwareFps),5:F1} rt)";
    private static FleetCell<TSnapshot> RunFleet<TMachine, TSnapshot>(Func<TMachine> build, int count, int frames,
        bool choir, bool parallel, TSnapshot? initialState = null)
        where TMachine : struct, IMachineBenchmarkSubject<TSnapshot>
        where TSnapshot : class {
        var machines = new TMachine[count];
        var built = 0;

        try {
            while (built < count) {
                var index = built;

                machines[index] = build();
                ++built;
                machines[index].PrepareInput();
                if (initialState is not null) {
                    machines[index].Restore(snapshot: initialState);
                }
            }
            var elapsed = (choir
                ? MachineFleetStepper.Run<TMachine, TSnapshot, MachineFleetStepper.SharedStreams, MachineFleetStepper.Untimed>(
                    frames: frames, machines: machines, parallel: parallel, timing: default)
                : MachineFleetStepper.Run<TMachine, TSnapshot, MachineFleetStepper.PairedStreams, MachineFleetStepper.Untimed>(
                    frames: frames, machines: machines, parallel: parallel, timing: default));
            var anchor = machines[0].Snapshot();
            var pairMatched = ((count < 2) || TMachine.SnapshotsEqual(a: anchor, b: machines[(count - 1)].Snapshot()));

            return new(MachineFramesPerSecond: ((((double)count) * frames) / elapsed.TotalSeconds), Anchor: anchor, PairMatched: pairMatched);
        } finally {
            for (var index = 0; (index < built); ++index) {
                machines[index].Dispose();
            }
        }
    }
    private static void MeasureLatencies<TMachine, TSnapshot>(Func<TMachine> build, StringBuilder report)
        where TMachine : struct, IMachineBenchmarkSubject<TSnapshot>
        where TSnapshot : class {
        BenchDiagnosticFormatting.Line(report: report, text: "");
        BenchDiagnosticFormatting.Line(report: report, text: $"per-operation latency (mean over {LatencyReps} reps) + managed allocation:");
        var createTicks = 0L;
        var createBytes = (AllocationWindow.Total(window: () => {
            for (var rep = 0; (rep < LatencyReps); ++rep) {
                var start = Stopwatch.GetTimestamp();
                var machine = build();

                createTicks += (Stopwatch.GetTimestamp() - start);
                machine.Dispose();
            }
        }) / LatencyReps);

        BenchDiagnosticFormatting.Line(report: report, text: $"  Create   {BenchDiagnosticFormatting.TicksToMicroseconds(ticks: (createTicks / LatencyReps)),10:F1} us  {createBytes,10:N0} B");

        using var subject = build();

        subject.RunFrames(frames: WarmFrames);
        var snapshotTicks = 0L;
        var snapshot = subject.Snapshot();
        var snapshotBytes = (AllocationWindow.Total(window: () => {
            for (var rep = 0; (rep < LatencyReps); ++rep) {
                var start = Stopwatch.GetTimestamp();

                snapshot = subject.Snapshot();
                snapshotTicks += (Stopwatch.GetTimestamp() - start);
            }
        }) / LatencyReps);

        BenchDiagnosticFormatting.Line(report: report, text: $"  Snapshot {BenchDiagnosticFormatting.TicksToMicroseconds(ticks: (snapshotTicks / LatencyReps)),10:F1} us  {snapshotBytes,10:N0} B  (snapshot size {TMachine.SnapshotSize(snapshot: snapshot):N0} B)");

        var restoreTicks = 0L;
        var restoreBytes = (AllocationWindow.Total(window: () => {
            for (var rep = 0; (rep < LatencyReps); ++rep) {
                var start = Stopwatch.GetTimestamp();

                subject.Restore(snapshot: snapshot);
                restoreTicks += (Stopwatch.GetTimestamp() - start);
            }
        }) / LatencyReps);

        BenchDiagnosticFormatting.Line(report: report, text: $"  Restore  {BenchDiagnosticFormatting.TicksToMicroseconds(ticks: (restoreTicks / LatencyReps)),10:F1} us  {restoreBytes,10:N0} B");

        var forkTicks = 0L;
        var forkBytes = (AllocationWindow.Total(window: () => {
            for (var rep = 0; (rep < LatencyReps); ++rep) {
                var start = Stopwatch.GetTimestamp();
                var fork = subject.Fork();

                forkTicks += (Stopwatch.GetTimestamp() - start);
                fork.Dispose();
            }
        }) / LatencyReps);

        BenchDiagnosticFormatting.Line(report: report, text: $"  Fork     {BenchDiagnosticFormatting.TicksToMicroseconds(ticks: (forkTicks / LatencyReps)),10:F1} us  {forkBytes,10:N0} B");
    }

    private sealed record FleetCell<TSnapshot>(double MachineFramesPerSecond, TSnapshot Anchor, bool PairMatched);
}
