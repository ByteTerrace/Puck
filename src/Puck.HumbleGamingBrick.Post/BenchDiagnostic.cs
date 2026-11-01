using System.Diagnostics;
using System.Text;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>Runs the shared fleet benchmark plus mailbox, footprint, and optional mixed-mapper measurements
/// over the Humble machine.</summary>
internal static class BenchDiagnostic {
    /// <summary>The measured-frame floor per machine; low fleet sizes get more frames (see <see cref="MachineBenchmark.FramesFor"/>)
    /// so the small-N cells are not noise-dominated.</summary>
    private const int DefaultFramesPerMachine = 60;
    private const int FootprintFleetSize = 64;
    /// <summary>Emulated frames per mailbox check — a quarter second of machine time, the "did anything arrive"
    /// wake span.</summary>
    private const int MailboxFramesPerCheck = 15;
    private const int MailboxReps = 32;
    /// <summary>Frames a machine runs before its state is considered representative for snapshot/restore/fork/mailbox
    /// measurement.</summary>
    private const int WarmFrames = 120;

    private static readonly int[] DefaultFleetSizes = [1, 2, 4, 8, 16, 32, 64, 128, 256];

    /// <summary>A deterministic, edge-rich joypad script (the trio-lockstep pattern): odd multipliers walk all 256
    /// button patterns, offset per stream so independent machines genuinely diverge.</summary>
    private static JoypadButtons ButtonsFor(int stream, int frame) =>
        ((JoypadButtons)((byte)((frame * 37) + (stream * 11))));
    private static void MeasureFootprint(byte[] rom, ConsoleModel model, StringBuilder report) {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetBefore = Environment.WorkingSet;
        var fleet = new MachineInstance[FootprintFleetSize];

        for (var index = 0; (index < FootprintFleetSize); ++index) {
            fleet[index] = PostMachine.Build(
                model: model,
                rom: rom
            );

            // One frame so every machine's working memory is genuinely touched, not just reserved.
            PostMachine.RunFrames(
                instance: fleet[index],
                frames: 1
            );
        }

        var managedPerMachine = ((GC.GetTotalMemory(forceFullCollection: true) - managedBefore) / FootprintFleetSize);
        var workingSetPerMachine = ((Environment.WorkingSet - workingSetBefore) / FootprintFleetSize);

        foreach (var machine in fleet) {
            machine.Dispose();
        }

        BenchDiagnosticFormatting.Line(
            report: report,
            text: ""
        );
        BenchDiagnosticFormatting.Line(
            report: report,
            text: $"resident footprint ({FootprintFleetSize}-machine fleet): {managedPerMachine:N0} B managed / {workingSetPerMachine:N0} B working set per machine"
        );
    }
    private static void MeasureMailboxCycle(byte[] rom, ConsoleModel model, StringBuilder report) {
        using var subject = PostMachine.Build(
            model: model,
            rom: rom
        );

        PostMachine.RunFrames(
            frames: WarmFrames,
            instance: subject
        );

        var bus = subject.GetRequiredService<ISystemBus>();
        var dormant = subject.Machine.Snapshot();
        var stopwatch = Stopwatch.StartNew();
        byte mailbox = 0;

        // The long-term-interaction rhythm: wake a dormant machine, give it a beat of emulated time, read its
        // cartridge/work RAM for what the game left behind, and freeze it again. 0xC000 is the page the synthetic
        // ROM's loop writes; a real mailbox convention would pin its own address.
        for (var rep = 0; (rep < MailboxReps); ++rep) {
            subject.Machine.Restore(snapshot: dormant);
            PostMachine.RunFrames(
                frames: MailboxFramesPerCheck,
                instance: subject
            );
            mailbox = bus.ReadByte(address: 0xC000);
            dormant = subject.Machine.Snapshot();
        }

        stopwatch.Stop();

        var millisecondsPerCheck = (stopwatch.Elapsed.TotalMilliseconds / MailboxReps);

        BenchDiagnosticFormatting.Line(
            report: report,
            text: ""
        );
        BenchDiagnosticFormatting.Line(
            report: report,
            text: $"mailbox check (restore -> run {MailboxFramesPerCheck} frames -> read -> snapshot): {millisecondsPerCheck:F2} ms/check = {(1_000.0 / millisecondsPerCheck):F0} checks/s single-threaded (last read 0x{mailbox:X2})"
        );
    }
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
            models[index] = (((bytes.Length > 0x0143) && (0 != (bytes[0x0143] & 0x80)))
                ? ConsoleModel.CgbE
                : ConsoleModel.DmgC
            );
            names[index] = Path.GetFileName(path: romPaths[index]);
            mappers[index] = CartridgeHeader.Parse(rom: bytes).Mapper;
        }

        BenchDiagnosticFormatting.Line(
            report: report,
            text: ""
        );
        BenchDiagnosticFormatting.Line(
            report: report,
            text: $"mixed-mapper fleet — {romCount} distinct ROMs, machine[i] = rom[i % {romCount}] (round-robin):"
        );

        for (var index = 0; (index < romCount); ++index) {
            BenchDiagnosticFormatting.Line(
                report: report,
                text: $"  rom[{index}] {names[index]} — {mappers[index]} ({models[index]})"
            );
        }

        BenchDiagnosticFormatting.Line(
            report: report,
            text: ""
        );
        BenchDiagnosticFormatting.Line(
            report: report,
            text: "mixed-fleet scaling, machine-frames/s (bit-lock compares machine[i] serial vs parallel, every i):"
        );
        BenchDiagnosticFormatting.Line(
            report: report,
            text: $"{"n",5}  {"independent-1t",18}  {"independent-mt",18}"
        );

        MixedFleetCell? largestSerial = null;
        MixedFleetCell? largestParallel = null;
        var largestCount = 0;

        foreach (var count in fleetSizes) {
            if (count < romCount) {
                // Too small a fleet to give every mapper a machine — skip rather than silently under-representing the
                // mixed workload the row claims to measure.
                continue;
            }

            var frames = MachineBenchmark.FramesFor(
                count: count,
                frameFloor: frameFloor
            );

            GC.Collect();
            GC.WaitForPendingFinalizers();

            var serial = RunMixedFleet(
                count: count,
                frames: frames,
                models: models,
                parallel: false,
                romCount: romCount,
                roms: roms
            );
            var parallel = RunMixedFleet(
                count: count,
                frames: frames,
                models: models,
                parallel: true,
                romCount: romCount,
                roms: roms
            );
            var pairHeld = true;

            for (var index = 0; (index < count); ++index) {
                pairHeld &= serial.Snapshots[index].ContentEquals(other: parallel.Snapshots[index]);
            }

            determinismHeld &= pairHeld;

            BenchDiagnosticFormatting.Line(
                report: report,
                text: $"{count,5}  {MixedCell(cell: serial),18}  {MixedCell(cell: parallel),18}{(pairHeld
                ? ""
                : "  << DETERMINISM BROKEN")}"
            );

            if (count >= largestCount) {
                largestCount = count;
                largestSerial = serial;
                largestParallel = parallel;
            }
        }

        if (
            (largestSerial is not null) &&
            (largestParallel is not null)
        ) {
            BenchDiagnosticFormatting.Line(
                report: report,
                text: ""
            );
            BenchDiagnosticFormatting.Line(
                report: report,
                text: $"mixed-fleet per-ROM machine-frames/s (fleet size {largestCount}, per-machine average — serial / parallel):"
            );

            for (var index = 0; (index < romCount); ++index) {
                BenchDiagnosticFormatting.Line(
                    report: report,
                    text: $"  rom[{index}] {names[index],-40} {mappers[index],-12} {largestSerial.PerRomMachineFramesPerSecond[index],8:F0}  /  {largestParallel.PerRomMachineFramesPerSecond[index],8:F0}"
                );
            }
        }
    }
    private static string MixedCell(MixedFleetCell cell) =>
        $"{cell.AggregateMachineFramesPerSecond,8:F0} ({(cell.AggregateMachineFramesPerSecond / PostMachine.HardwareFps),5:F1} rt)";
    private static MixedFleetCell RunMixedFleet(byte[][] roms, ConsoleModel[] models, int romCount, int count, int frames, bool parallel) {
        var machines = new BenchmarkSubject[count];

        for (var index = 0; (index < count); ++index) {
            var romIndex = (index % romCount);

            machines[index] = new(instance: PostMachine.Build(
                model: models[romIndex],
                rom: roms[romIndex]
            ));
        }
        for (var index = 0; (index < count); ++index) {
            machines[index].PrepareInput();
        }
        // Per-machine elapsed ticks, tracked independently of the fleet's aggregate stopwatch so a per-ROM throughput
        // number can be recovered without a separate run per mapper.
        var perMachineTicks = new long[count];
        var elapsed = MachineFleetStepper.Run<BenchmarkSubject, MachineSnapshot,
            MachineFleetStepper.IndependentStreams, MachineFleetStepper.PerMachineTiming>(
            machines: machines, frames: frames, parallel: parallel, timing: new(ticks: perMachineTicks));

        var snapshots = Array.ConvertAll(
            array: machines,
            converter: static machine => machine.Snapshot()
        );
        var perRom = new double[romCount];
        var perRomMachineCount = new int[romCount];

        for (var index = 0; (index < count); ++index) {
            var romIndex = (index % romCount);
            var machineFramesPerSecond = (frames / (perMachineTicks[index] / ((double)Stopwatch.Frequency)));

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
            AggregateMachineFramesPerSecond: ((((double)count) * frames) / elapsed.TotalSeconds),
            PerRomMachineFramesPerSecond: perRom,
            Snapshots: snapshots
        );
    }

    /// <summary>Runs the bench and writes the report to the console and <c>bench-report.txt</c> in the artifacts
    /// directory.</summary>
    /// <param name="args">The command-line arguments (<c>--bench-rom</c>, <c>--bench-frames</c>,
    /// <c>--bench-fleet</c>, <c>--artifacts</c>).</param>
    /// <returns>0 on a clean run; 1 when a determinism guard failed.</returns>
    public static int Run(string[] args) {
        // Commas belong to cartridge filenames; only repeated --bench-rom options separate paths.
        var romPaths = CommandLineArguments.Values(
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
            path2: "gb-post"
        ));
        byte[] rom;
        ConsoleModel model;
        string romName;

        if (romPaths.Length > 0) {
            rom = File.ReadAllBytes(path: romPaths[0]);
            model = (((rom.Length > 0x0143) && (0 != (rom[0x0143] & 0x80)))
                ? ConsoleModel.CgbE
                : ConsoleModel.DmgC
            );
            romName = Path.GetFileName(path: romPaths[0]);
        } else {
            rom = SyntheticRom.Create();
            model = ConsoleModel.DmgC;
            romName = "synthetic";
        }

        var report = new StringBuilder();

        BenchDiagnosticFormatting.Line(
            report: report,
            text: $"machine-fleet bench — {romName} ({model}), frame floor {frameFloor}/machine, {Environment.ProcessorCount} logical processors"
        );

        return MachineBenchmark.Run<BenchmarkSubject, MachineSnapshot>(
            build: () => new(instance: PostMachine.Build(model: model, rom: rom)),
            fleetSizes: fleetSizes,
            frameFloor: frameFloor,
            hardwareFps: PostMachine.HardwareFps,
            report: report,
            artifactsDirectory: artifactsDirectory,
            extraMeasurements: extraReport => {
                MeasureMailboxCycle(model: model, report: extraReport, rom: rom);
                MeasureFootprint(model: model, report: extraReport, rom: rom);
                var held = true;

                if (romPaths.Length > 1) {
                    MeasureMixedMapperFleet(determinismHeld: ref held, fleetSizes: fleetSizes,
                        frameFloor: frameFloor, report: extraReport, romPaths: romPaths);
                }
                return held;
            }
        );
    }

    private struct BenchmarkSubject : IMachineBenchmarkSubject<MachineSnapshot> {
        private readonly MachineInstance m_instance;

        private IJoypad? m_joypad;

        public BenchmarkSubject(MachineInstance instance) {
            m_instance = instance;
            m_joypad = null;
        }

        public void PrepareInput() => m_joypad = m_instance.GetRequiredService<IJoypad>();
        public static bool SnapshotsEqual(MachineSnapshot a, MachineSnapshot b) => a.ContentEquals(other: b);
        public static int SnapshotSize(MachineSnapshot snapshot) => snapshot.Size;
        public MachineSnapshot Snapshot() => m_instance.Machine.Snapshot();
        public void Restore(MachineSnapshot snapshot) => m_instance.Machine.Restore(snapshot: snapshot);
        public IDisposable Fork() => m_instance.Fork();
        public void Dispose() => m_instance.Dispose();
        public void RunFrames(int frames) => PostMachine.RunFrames(frames: frames, instance: m_instance);
        public void RunFrame(int stream, int frame) {
            m_joypad!.SetButtons(pressed: ButtonsFor(frame: frame, stream: stream));
            m_instance.Machine.Run(tCycles: ((ulong)PostMachine.TCyclesPerFrame));
        }
    }
    /// <summary>A mixed fleet cell retains every machine's snapshot for cross-mode comparisons.</summary>
    private sealed record MixedFleetCell(double AggregateMachineFramesPerSecond, double[] PerRomMachineFramesPerSecond, MachineSnapshot[] Snapshots);
}
