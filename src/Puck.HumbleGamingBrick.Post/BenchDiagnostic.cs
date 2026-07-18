using System.Diagnostics;
using System.Text;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The machine-fleet bench (<c>--bench</c>) — the measurement instrument the fleet-performance plan rests on
/// (docs/machine-fleet-briefing.md §5 step 1). One run reports: the fleet scaling curve in BOTH shapes (independent
/// per-machine input streams and one shared choir stream), single- and multi-threaded; the burst catch-up rate (the
/// simulate-on-demand dormancy budget); <c>Create</c>/<c>Snapshot</c>/<c>Restore</c>/<c>Fork</c> latency and
/// allocation (the spawn / ghost-echo / promote-demote budgets); the mailbox-check cycle (restore → run → read
/// cartridge RAM → snapshot); and the per-machine footprint. It is a measurement, not a gate — but every fleet cell
/// ends with a same-stream snapshot compare and the multi-threaded cell must end bit-identical to the
/// single-threaded one, so a bench run that breaks determinism exits 1 instead of reporting quietly.
/// </summary>
internal static class BenchDiagnostic {
    /// <summary>The measured-frame floor per machine; low fleet sizes get more frames (see <see cref="FramesFor"/>)
    /// so the small-N cells are not noise-dominated.</summary>
    private const int DefaultFramesPerMachine = 60;
    /// <summary>Emulated frames per mailbox check — a quarter second of machine time, the "did anything arrive"
    /// wake span.</summary>
    private const int MailboxFramesPerCheck = 15;
    private const int LatencyReps = 64;
    private const int MailboxReps = 32;
    /// <summary>Frames a machine runs before its state is considered representative for snapshot/restore/fork/mailbox
    /// measurement.</summary>
    private const int WarmFrames = 120;
    private const int BurstFrames = 600;
    private const int FootprintFleetSize = 64;

    private static readonly int[] DefaultFleetSizes = [1, 2, 4, 8, 16, 32, 64, 128, 256];

    /// <summary>Runs the bench and writes the report to the console and <c>bench-report.txt</c> in the artifacts
    /// directory.</summary>
    /// <param name="args">The command-line arguments (<c>--bench-rom</c>, <c>--bench-frames</c>,
    /// <c>--bench-fleet</c>, <c>--artifacts</c>).</param>
    /// <returns>0 on a clean run; 1 when a determinism guard failed.</returns>
    public static int Run(string[] args) {
        // --bench-rom accepts more than one path via a repeated flag (--bench-rom a --bench-rom b — see ArgValues for
        // why not also a comma list): 0 or 1 path runs the original homogeneous-fleet bench unchanged; 2+ additionally
        // runs the mixed-mapper fleet section (A2/D1's megamorphic payoff case — see MeasureMixedMapperFleet).
        var romPaths = ArgValues(args: args, name: "--bench-rom");
        var frameFloor = (int.TryParse(s: ArgValue(args: args, name: "--bench-frames"), result: out var parsedFrames) ? parsedFrames : DefaultFramesPerMachine);
        var fleetSizes = ParseFleetSizes(value: ArgValue(args: args, name: "--bench-fleet"));
        var artifactsDirectory = (ArgValue(args: args, name: "--artifacts") ?? Path.Combine(path1: "artifacts", path2: "gb-post"));
        byte[] rom;
        ConsoleModel model;
        string romName;

        if (romPaths.Length > 0) {
            rom = File.ReadAllBytes(path: romPaths[0]);
            model = (((rom.Length > 0x0143) && (0 != (rom[0x0143] & 0x80))) ? ConsoleModel.Cgb : ConsoleModel.Dmg);
            romName = Path.GetFileName(path: romPaths[0]);
        } else {
            rom = SyntheticRom.Create();
            model = ConsoleModel.Dmg;
            romName = "synthetic";
        }

        var report = new StringBuilder();
        var determinismHeld = true;

        Line(report: report, text: $"machine-fleet bench — {romName} ({model}), frame floor {frameFloor}/machine, {Environment.ProcessorCount} logical processors");

        // Discarded warm-up fleets so JIT tiering settles before anything is measured.
        RunFleet(rom: rom, model: model, count: 2, frames: 30, choir: false, parallel: false);
        RunFleet(rom: rom, model: model, count: 2, frames: 30, choir: false, parallel: true);

        Line(report: report, text: "");
        Line(report: report, text: "fleet scaling, machine-frames/s (rt = machines sustainable at realtime):");
        Line(report: report, text: $"{"n",5}  {"independent-1t",18}  {"independent-mt",18}  {"choir-1t",18}  {"choir-mt",18}");

        foreach (var count in fleetSizes) {
            var frames = FramesFor(count: count, frameFloor: frameFloor);

            // A clean heap per row so one cell's garbage is not another cell's pause.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var independentSingle = RunFleet(rom: rom, model: model, count: count, frames: frames, choir: false, parallel: false);
            var independentParallel = RunFleet(rom: rom, model: model, count: count, frames: frames, choir: false, parallel: true);
            var choirSingle = RunFleet(rom: rom, model: model, count: count, frames: frames, choir: true, parallel: false);
            var choirParallel = RunFleet(rom: rom, model: model, count: count, frames: frames, choir: true, parallel: true);

            // Every cell consumed stream 0 on machine 0, so all four anchors must be byte-identical — this is the
            // serial-vs-parallel (and shape-vs-shape) bit-lock guard.
            determinismHeld &= (independentSingle.PairMatched && independentParallel.PairMatched && choirSingle.PairMatched && choirParallel.PairMatched);
            determinismHeld &= independentSingle.Anchor.ContentEquals(other: independentParallel.Anchor);
            determinismHeld &= independentSingle.Anchor.ContentEquals(other: choirSingle.Anchor);
            determinismHeld &= independentSingle.Anchor.ContentEquals(other: choirParallel.Anchor);

            Line(
                report: report,
                text: $"{count,5}  {Cell(cell: independentSingle),18}  {Cell(cell: independentParallel),18}  {Cell(cell: choirSingle),18}  {Cell(cell: choirParallel),18}{(determinismHeld ? "" : "  << DETERMINISM BROKEN")}"
            );
        }

        // Burst catch-up: the dormancy model's budget — a frozen machine fast-forwarding its elapsed span. One
        // machine uncapped, and one machine per logical processor all catching up at once.
        var burstSingle = RunFleet(rom: rom, model: model, count: 1, frames: BurstFrames, choir: false, parallel: false);
        var burstFleet = RunFleet(rom: rom, model: model, count: Environment.ProcessorCount, frames: (BurstFrames / 2), choir: false, parallel: true);
        var singleMultiple = (burstSingle.MachineFramesPerSecond / PostMachine.HardwareFps);
        var fleetPerMachineMultiple = ((burstFleet.MachineFramesPerSecond / Environment.ProcessorCount) / PostMachine.HardwareFps);

        Line(report: report, text: "");
        Line(report: report, text: "burst catch-up (simulate-on-demand dormancy):");
        Line(report: report, text: $"  one machine: {burstSingle.MachineFramesPerSecond:F0} machine-frames/s = {singleMultiple:F1}x realtime; one dormant hour replays in {(3_600.0 / singleMultiple):F1} s");
        Line(report: report, text: $"  {Environment.ProcessorCount} machines in parallel: {burstFleet.MachineFramesPerSecond:F0} machine-frames/s aggregate = {fleetPerMachineMultiple:F1}x realtime each");

        determinismHeld &= burstFleet.PairMatched;

        MeasureLatencies(rom: rom, model: model, report: report);
        MeasureMailboxCycle(rom: rom, model: model, report: report);
        MeasureFootprint(rom: rom, model: model, report: report);

        if (romPaths.Length > 1) {
            MeasureMixedMapperFleet(romPaths: romPaths, frameFloor: frameFloor, fleetSizes: fleetSizes, report: report, determinismHeld: ref determinismHeld);
        }

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
    private sealed record FleetCell(double MachineFramesPerSecond, MachineSnapshot Anchor, bool PairMatched);

    private static FleetCell RunFleet(byte[] rom, ConsoleModel model, int count, int frames, bool choir, bool parallel) {
        var machines = new MachineInstance[count];

        for (var index = 0; (index < count); ++index) {
            machines[index] = PostMachine.Build(model: model, rom: rom);
        }

        var joypads = Array.ConvertAll(array: machines, converter: static machine => machine.GetRequiredService<IJoypad>());
        var stopwatch = Stopwatch.StartNew();

        if (parallel) {
            // Task-per-machine, no per-frame barrier: input is a pure function of (stream, frame) and machines share
            // nothing, so each one can run its whole span straight through.
            Parallel.For(fromInclusive: 0, toExclusive: count, body: index => {
                var joypad = joypads[index];
                var machine = machines[index].Machine;
                var stream = StreamFor(index: index, count: count, choir: choir);

                for (var frame = 0; (frame < frames); ++frame) {
                    joypad.SetButtons(pressed: ButtonsFor(stream: stream, frame: frame));
                    machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
                }
            });
        } else {
            // Frame-at-a-time round-robin — the shape of today's serial stepping on the render thread.
            for (var frame = 0; (frame < frames); ++frame) {
                for (var index = 0; (index < count); ++index) {
                    joypads[index].SetButtons(pressed: ButtonsFor(stream: StreamFor(index: index, count: count, choir: choir), frame: frame));
                    machines[index].Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
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
    private static void MeasureLatencies(byte[] rom, ConsoleModel model, StringBuilder report) {
        Line(report: report, text: "");
        Line(report: report, text: $"per-operation latency (mean over {LatencyReps} reps) + managed allocation:");

        // Create: a full DI container per machine — the spawn-on-stumble / pooling-lever number.
        var createTicks = 0L;
        var createBytes = GC.GetAllocatedBytesForCurrentThread();

        for (var rep = 0; (rep < LatencyReps); ++rep) {
            var start = Stopwatch.GetTimestamp();
            var machine = PostMachine.Build(model: model, rom: rom);

            createTicks += (Stopwatch.GetTimestamp() - start);
            machine.Dispose();
        }

        createBytes = ((GC.GetAllocatedBytesForCurrentThread() - createBytes) / LatencyReps);
        Line(report: report, text: $"  Create   {TicksToMicroseconds(ticks: (createTicks / LatencyReps)),10:F1} us  {createBytes,10:N0} B");

        using var subject = PostMachine.Build(model: model, rom: rom);

        PostMachine.RunFrames(instance: subject, frames: WarmFrames);

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
    private static void MeasureMailboxCycle(byte[] rom, ConsoleModel model, StringBuilder report) {
        using var subject = PostMachine.Build(model: model, rom: rom);

        PostMachine.RunFrames(instance: subject, frames: WarmFrames);

        var bus = subject.GetRequiredService<ISystemBus>();
        var dormant = subject.Machine.Snapshot();
        var stopwatch = Stopwatch.StartNew();
        byte mailbox = 0;

        // The long-term-interaction rhythm: wake a dormant machine, give it a beat of emulated time, read its
        // cartridge/work RAM for what the game left behind, and freeze it again. 0xC000 is the page the synthetic
        // ROM's loop writes; a real mailbox convention would pin its own address.
        for (var rep = 0; (rep < MailboxReps); ++rep) {
            subject.Machine.Restore(snapshot: dormant);
            PostMachine.RunFrames(instance: subject, frames: MailboxFramesPerCheck);
            mailbox = bus.ReadByte(address: 0xC000);
            dormant = subject.Machine.Snapshot();
        }

        stopwatch.Stop();

        var millisecondsPerCheck = (stopwatch.Elapsed.TotalMilliseconds / MailboxReps);

        Line(report: report, text: "");
        Line(report: report, text: $"mailbox check (restore -> run {MailboxFramesPerCheck} frames -> read -> snapshot): {millisecondsPerCheck:F2} ms/check = {(1_000.0 / millisecondsPerCheck):F0} checks/s single-threaded (last read 0x{mailbox:X2})");
    }
    private static void MeasureFootprint(byte[] rom, ConsoleModel model, StringBuilder report) {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetBefore = Environment.WorkingSet;
        var fleet = new MachineInstance[FootprintFleetSize];

        for (var index = 0; (index < FootprintFleetSize); ++index) {
            fleet[index] = PostMachine.Build(model: model, rom: rom);

            // One frame so every machine's working memory is genuinely touched, not just reserved.
            PostMachine.RunFrames(instance: fleet[index], frames: 1);
        }

        var managedPerMachine = ((GC.GetTotalMemory(forceFullCollection: true) - managedBefore) / FootprintFleetSize);
        var workingSetPerMachine = ((Environment.WorkingSet - workingSetBefore) / FootprintFleetSize);

        foreach (var machine in fleet) {
            machine.Dispose();
        }

        Line(report: report, text: "");
        Line(report: report, text: $"resident footprint ({FootprintFleetSize}-machine fleet): {managedPerMachine:N0} B managed / {workingSetPerMachine:N0} B working set per machine");
    }

    /// <summary>One measured mixed-mapper fleet cell (one execution shape: serial or parallel). Unlike the homogeneous
    /// <see cref="FleetCell"/>, every machine's own final snapshot is kept (<see cref="Snapshots"/>) — a mixed fleet has
    /// no same-stream pair to compare (each machine index is a different cartridge/mapper by construction), so the
    /// determinism guard instead compares machine[i]'s serial snapshot against machine[i]'s parallel snapshot for every
    /// i (the "per-machine, same index, across modes" bit-lock the megamorphic A/B needs).</summary>
    private sealed record MixedFleetCell(double AggregateMachineFramesPerSecond, double[] PerRomMachineFramesPerSecond, MachineSnapshot[] Snapshots);

    /// <summary>The mixed-mapper fleet bench (D1's honest payoff case): machine i boots <c>rom[i % roms.Length]</c>, so
    /// a single process runs several distinct cartridge mappers side by side — the bank-resolved bus window cache's
    /// call sites are megamorphic across the fleet here in a way a single-ROM bench can never exercise, because dynamic
    /// PGO's guarded devirtualization profiles per call site, not per machine.</summary>
    private static void MeasureMixedMapperFleet(string[] romPaths, int frameFloor, int[] fleetSizes, StringBuilder report, ref bool determinismHeld) {
        var romCount = romPaths.Length;
        var roms = new byte[romCount][];
        var models = new ConsoleModel[romCount];
        var names = new string[romCount];
        var mappers = new MapperKind[romCount];

        for (var index = 0; (index < romCount); ++index) {
            var bytes = File.ReadAllBytes(path: romPaths[index]);

            roms[index] = bytes;
            models[index] = (((bytes.Length > 0x0143) && (0 != (bytes[0x0143] & 0x80))) ? ConsoleModel.Cgb : ConsoleModel.Dmg);
            names[index] = Path.GetFileName(path: romPaths[index]);
            mappers[index] = CartridgeHeader.Parse(rom: bytes).Mapper;
        }

        Line(report: report, text: "");
        Line(report: report, text: $"mixed-mapper fleet — {romCount} distinct ROMs, machine[i] = rom[i % {romCount}] (round-robin):");

        for (var index = 0; (index < romCount); ++index) {
            Line(report: report, text: $"  rom[{index}] {names[index]} — {mappers[index]} ({models[index]})");
        }

        Line(report: report, text: "");
        Line(report: report, text: "mixed-fleet scaling, machine-frames/s (bit-lock compares machine[i] serial vs parallel, every i):");
        Line(report: report, text: $"{"n",5}  {"independent-1t",18}  {"independent-mt",18}");

        MixedFleetCell? largestSerial = null;
        MixedFleetCell? largestParallel = null;
        var largestCount = 0;

        foreach (var count in fleetSizes) {
            if (count < romCount) {
                // Too small a fleet to give every mapper a machine — skip rather than silently under-representing the
                // mixed workload the row claims to measure.
                continue;
            }

            var frames = FramesFor(count: count, frameFloor: frameFloor);

            GC.Collect();
            GC.WaitForPendingFinalizers();

            var serial = RunMixedFleet(roms: roms, models: models, romCount: romCount, count: count, frames: frames, parallel: false);
            var parallel = RunMixedFleet(roms: roms, models: models, romCount: romCount, count: count, frames: frames, parallel: true);
            var pairHeld = true;

            for (var index = 0; (index < count); ++index) {
                pairHeld &= serial.Snapshots[index].ContentEquals(other: parallel.Snapshots[index]);
            }

            determinismHeld &= pairHeld;

            Line(
                report: report,
                text: $"{count,5}  {MixedCell(cell: serial),18}  {MixedCell(cell: parallel),18}{(pairHeld ? "" : "  << DETERMINISM BROKEN")}"
            );

            if (count >= largestCount) {
                largestCount = count;
                largestSerial = serial;
                largestParallel = parallel;
            }
        }

        if ((largestSerial is not null) && (largestParallel is not null)) {
            Line(report: report, text: "");
            Line(report: report, text: $"mixed-fleet per-ROM machine-frames/s (fleet size {largestCount}, per-machine average — serial / parallel):");

            for (var index = 0; (index < romCount); ++index) {
                Line(
                    report: report,
                    text: $"  rom[{index}] {names[index],-40} {mappers[index],-12} {largestSerial.PerRomMachineFramesPerSecond[index],8:F0}  /  {largestParallel.PerRomMachineFramesPerSecond[index],8:F0}"
                );
            }
        }
    }
    private static MixedFleetCell RunMixedFleet(byte[][] roms, ConsoleModel[] models, int romCount, int count, int frames, bool parallel) {
        var machines = new MachineInstance[count];

        for (var index = 0; (index < count); ++index) {
            var romIndex = (index % romCount);

            machines[index] = PostMachine.Build(model: models[romIndex], rom: roms[romIndex]);
        }

        var joypads = Array.ConvertAll(array: machines, converter: static machine => machine.GetRequiredService<IJoypad>());
        // Per-machine elapsed ticks, tracked independently of the fleet's aggregate stopwatch so a per-ROM throughput
        // number can be recovered without a separate run per mapper.
        var perMachineTicks = new long[count];
        var stopwatch = Stopwatch.StartNew();

        if (parallel) {
            Parallel.For(fromInclusive: 0, toExclusive: count, body: index => {
                var joypad = joypads[index];
                var machine = machines[index].Machine;
                var start = Stopwatch.GetTimestamp();

                for (var frame = 0; (frame < frames); ++frame) {
                    joypad.SetButtons(pressed: ButtonsFor(stream: index, frame: frame));
                    machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
                }

                perMachineTicks[index] = (Stopwatch.GetTimestamp() - start);
            });
        } else {
            for (var frame = 0; (frame < frames); ++frame) {
                for (var index = 0; (index < count); ++index) {
                    var start = Stopwatch.GetTimestamp();

                    joypads[index].SetButtons(pressed: ButtonsFor(stream: index, frame: frame));
                    machines[index].Machine.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
                    perMachineTicks[index] += (Stopwatch.GetTimestamp() - start);
                }
            }
        }

        stopwatch.Stop();

        var snapshots = Array.ConvertAll(array: machines, converter: static machine => machine.Machine.Snapshot());
        var perRom = new double[romCount];
        var perRomMachineCount = new int[romCount];

        for (var index = 0; (index < count); ++index) {
            var romIndex = (index % romCount);
            var machineFramesPerSecond = (frames / (perMachineTicks[index] / (double)Stopwatch.Frequency));

            perRom[romIndex] += machineFramesPerSecond;
            perRomMachineCount[romIndex] += 1;
        }

        for (var romIndex = 0; (romIndex < romCount); ++romIndex) {
            if (perRomMachineCount[romIndex] > 0) {
                perRom[romIndex] /= perRomMachineCount[romIndex];
            }
        }

        foreach (var machine in machines) {
            machine.Dispose();
        }

        return new MixedFleetCell(
            AggregateMachineFramesPerSecond: (((double)count * frames) / stopwatch.Elapsed.TotalSeconds),
            PerRomMachineFramesPerSecond: perRom,
            Snapshots: snapshots
        );
    }

    /// <summary>The input stream a machine consumes: the choir shares stream 0; independent machines get their own
    /// stream, except the LAST machine, which always mirrors stream 0 so every cell carries a same-stream pair for
    /// the determinism guard.</summary>
    private static int StreamFor(int index, int count, bool choir) =>
        ((choir || (index == (count - 1))) ? 0 : index);

    /// <summary>A deterministic, edge-rich joypad script (the trio-lockstep pattern): odd multipliers walk all 256
    /// button patterns, offset per stream so independent machines genuinely diverge.</summary>
    private static JoypadButtons ButtonsFor(int stream, int frame) =>
        (JoypadButtons)(byte)((frame * 37) + (stream * 11));

    /// <summary>Small fleets get more frames so their cells are not stopwatch noise; the emulated span per cell
    /// stays roughly level until the floor takes over.</summary>
    private static int FramesFor(int count, int frameFloor) =>
        Math.Max(val1: frameFloor, val2: (960 / count));
    private static string Cell(FleetCell cell) =>
        $"{cell.MachineFramesPerSecond,8:F0} ({(cell.MachineFramesPerSecond / PostMachine.HardwareFps),5:F1} rt)";
    private static string MixedCell(MixedFleetCell cell) =>
        $"{cell.AggregateMachineFramesPerSecond,8:F0} ({(cell.AggregateMachineFramesPerSecond / PostMachine.HardwareFps),5:F1} rt)";
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
    /// <summary>Collects every occurrence of a repeatable flag verbatim (one value per occurrence) — the repeated-flag
    /// half of <c>--bench-fleet</c>'s "multiple values" convention. <c>--bench-rom</c> deliberately does NOT also
    /// comma-split: GamingBrick ROM filenames routinely carry commas in their own right (region tags like
    /// <c>"(USA, Europe)"</c>), so a comma-list would misparse a real path on this repo's own dev machine — repetition
    /// (<c>--bench-rom a.gbc --bench-rom b.gbc</c>) is the only unambiguous multi-value form for this flag.</summary>
    private static string[] ArgValues(string[] args, string name) {
        var values = new List<string>();

        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                values.Add(item: args[(index + 1)]);
            }
        }

        return [.. values];
    }
}
