using System.CommandLine;
using Puck.Cli.Host;
using Xunit;

namespace Puck.Cli.Gate.Tests;

public sealed class HostAdmissionLawTests {
    // The 16 GB, six-thread developer machine's thresholds without a commit reserve, so the laws about a start's own need
    // read the commit headroom against that need alone: pressure below 2 GB free, disk pressure below 10 GB. Its installed
    // memory reads 40 GB, whose account budget less its authoring margin (25 GB) holds every kind's full default peak, so
    // these laws read the kinds' own figures; the 16 GB machine's own peaks are
    // AWindowedWorldOnASixteenGigabyteMachineExpectsItsAccountBudgetLessItsAuthoringMargin's.
    private static readonly HostLoadThresholds Thresholds = (HostLoadThresholds.For(installedRamGb: 16, logicalProcessors: 6) with { CommitReservePercent = 0, InstalledRamGb = 40 });
    // The same thresholds with their default commit reserve (10% of the limit).
    private static readonly HostLoadThresholds Reserved = (HostLoadThresholds.For(installedRamGb: 16, logicalProcessors: 6) with { InstalledRamGb = 40 });
    // The 32 GB, 16-thread machine with its defaults: pressure below 4 GB free, a tenth of the commit limit in reserve.
    private static readonly HostLoadThresholds Desk = HostLoadThresholds.For(installedRamGb: 32, logicalProcessors: 16);

    // A commit reading is a percentage of a 40 GB commit limit; none is a platform that reports no limit. Free memory
    // defaults to plenty, so a law about commit is not also a law about physical memory.
    private static HostSample Sample(double cpu = 0, double ram = 64, double disk = 100, string? gpu = null, string? heavy = null, double? commit = null, double limit = 40, HostRunningStart[]? starts = null, HostLeaseHold[]? holds = null, double deviceTotal = double.NaN, double deviceUsed = double.NaN) => new(At: DateTimeOffset.UnixEpoch, CpuPercent: cpu, FreeDiskGb: disk, FreeRamGb: ram, GpuHolder: gpu, HeavyTestHolder: heavy, ReuseNodes: 42, CommitChargeGb: ((commit is { } percent) ? (percent * limit / 100.0) : double.NaN), CommitLimitGb: ((commit is null) ? double.NaN : limit), Starts: starts, Holds: holds, DeviceTotalGb: deviceTotal, DeviceUsedGb: deviceUsed);
    // Another lane's heavy hold: `puck lease heavy` PID 41, whose tree holds `privateGb` of commit (resident alike) and has
    // used `cpuSeconds` of CPU, with the given members beside the holder.
    private static HostLeaseHold HeavyHold(double privateGb, double cpuSeconds = 0, HostStartKind kind = HostStartKind.HeavySuite, params int[] members) =>
        new(CpuSeconds: cpuSeconds, Device: false, Holder: "other-lane run", Id: 41, Kind: kind, Lease: "heavy", Members: [41, .. members], PrivateGb: privateGb, ResidentGb: privateGb, StartTicks: 41);
    // A ready heavy suite holding 20 GB of commit and 1 GB resident, in a kind no law here observes: it adds no growth, and
    // headroom would be larger without it, so a start asked beside it is one a wait for it to exit could admit.
    private static readonly HostRunningStart Company = new(Id: 900, Kind: HostStartKind.HeavySuite, PrivateGb: 20, PrivateResidentGb: 1, Ready: true, StartTicks: 900);
    private static bool Admits(HostStartKind kind, HostSample reading, HostStartTracker tracker, GateClock clock, int boundSeconds = 25, HostLoadThresholds? on = null) {
        using var error = new StringWriter();

        return HostAdmission.Wait("start", (kind == HostStartKind.WindowedWorld), false, () => reading, clock, clock.Advance, error, CancellationToken.None, (on ?? Thresholds), TimeSpan.FromSeconds(seconds: boundSeconds), null, kind, tracker);
    }

    [Fact]
    public void HostOptionsDefaultToTheAdmissionThresholdsAndStillAllowOverrides() {
        var load = HostCommand.Create().Subcommands.Single(predicate: command => (command.Name == "load"));
        var defaults = HostLoadThresholds.ThisMachine;
        (string Name, double? Value)[] thresholds = [("--capacity-cpu", defaults.CapacityCpuPercent), ("--commit-reserve", defaults.CommitReservePercent), ("--device-full", defaults.DeviceFullPercent),("--pressure-ram", defaults.PressureRamGb), ("--pressure-disk", defaults.PressureDiskGb)];

        foreach (var (name, value) in thresholds) {
            var option = ((Option<double?>)load.Options.Single(predicate: option => (option.Name == name)));

            Assert.Equal(value, load.Parse([]).GetValue(option: option));
            Assert.Equal(1, load.Parse([name, "1"]).GetValue(option: option));
        }
        Assert.Contains("Defaults scale with the machine", CliHelp.DetailOf(command: load));
    }
    [Fact]
    public void AdmissionJudgesFreeMemoryAgainstTheMachinesOwnSize() {
        var clock = new GateClock();
        var calls = 0;
        using var small = new StringWriter();
        using var large = new StringWriter();

        // 3 GB free is not pressure on a 16 GB machine (below 2 GB) and is on a 32 GB one (below 4 GB), which waits until
        // 6 GB are free: pressure leaves the floor plus the step's own 2 GB peak.
        Assert.True(condition: HostAdmission.Wait("build", false, false, () => Sample(ram: 4), clock, _ => Assert.Fail(message: "4 GB free must admit a step on a 16 GB machine"), small, CancellationToken.None, Thresholds));
        Assert.True(condition: HostAdmission.Wait("build", false, false, () => Sample(ram: ((calls++ == 0) ? 3 : 6)), clock, clock.Advance, large, CancellationToken.None, Desk));
        Assert.Equal(actual: calls, expected: 2);
        Assert.Contains(actualString: large.ToString(), expectedSubstring: "freeRAM=3.0GB (admission needs at least 4.0GB (memory pressure))");
    }
    // Peaks measured on the machine this was found on (a 32 GB desk, 64 GB commit limit), written as the tracker writes
    // them (one line per observed process: start ticks, id, private GB, private working-set GB, working-set GB): a
    // windowed World reaches 20.4 GB of private bytes (commit) but holds about 14 GB privately resident.
    private static HostStartTracker MeasuredDesk(string root) {
        var peaks = Path.Combine(root, HostStartTracker.ObservationDirectory);

        Directory.CreateDirectory(peaks);
        foreach (var (name, text) in (IEnumerable<(string, string)>)[("WindowedWorld", "1 1 20.4 14 15.3\n"), ("HeadlessWorld", "1 1 1.5 1.2 1.4\n"), ("HeavySuite", "1 1 1.6 1.3 1.4\n")]) {
            File.WriteAllText(Path.Combine(peaks, name), text);
        }

        return new HostStartTracker(root);
    }
    // Feeds one finished process of a kind to a tracker over the shared root: seen, then gone after a plateau.
    private static void Observed(string root, GateClock clock, HostStartKind kind, int id, double privateGb, double workingSetGb, double privateResidentGb = double.NaN, HostStartProfile? marked = null) {
        var tracker = new HostStartTracker(root);

        _ = tracker.Judge(reading: Sample(starts: [new(Id: id, Kind: kind, PrivateGb: privateGb, PrivateResidentGb: privateResidentGb, StartTicks: id, WorkingSetGb: workingSetGb, Marked: marked)]), kind: HostStartKind.Step, thresholds: Desk, clock: clock, reserve: false);
        clock.Advance(HostStartTracker.Plateau);
        _ = tracker.Judge(reading: Sample(), kind: HostStartKind.Step, thresholds: Desk, clock: clock, reserve: false);
    }
    // A start is admitted by its own measured need, each quantity against its own reading: private bytes against the commit
    // headroom less the reserve, working set against free memory above the pressure floor. On an idle 32 GB desk (21 GB
    // free) a windowed World goes ahead although its 20.4 GB of private bytes plus the floor exceed free memory; with one
    // running (7 GB free left, 14 GB resident) a headless World and a heavy step go ahead and a second windowed World
    // does not; at 6 GB free the same holds; under real pressure nothing is admitted.
    [Fact]
    public void AStartIsAdmittedByWhatItNeedsNotByAFixedFreeMemoryFloor() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var running = new HostRunningStart(Id: 11, Kind: HostStartKind.WindowedWorld, PrivateGb: 20.4, Ready: true, StartTicks: 1, WorkingSetGb: 14);
        var idle = Sample(commit: 40, limit: 64, ram: 21);
        var beside = Sample(commit: 60, limit: 64, ram: 7, starts: [running]);
        var today = Sample(commit: 60, limit: 64, ram: 6, starts: [running]);

        HostStartTracker Tracker() => MeasuredDesk(directory.RootPath);

        Assert.Equal(expected: 4.0, actual: Desk.PressureRamGb);
        // 21 GB free less 14 GB resident leaves 7 GB, over the 4 GB floor; commit headroom 38.4 GB less 20.4 GB leaves 18 GB over the 6.4 GB reserve.
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: idle, tracker: Tracker(), clock: new GateClock(), on: Desk));
        foreach (var reading in new[] { beside, today }) {
            Assert.True(condition: Admits(kind: HostStartKind.HeadlessWorld, reading: reading, tracker: Tracker(), clock: new GateClock(), on: Desk));
            Assert.True(condition: Admits(kind: HostStartKind.HeavySuite, reading: reading, tracker: Tracker(), clock: new GateClock(), on: Desk));
            Assert.False(condition: Admits(kind: HostStartKind.WindowedWorld, reading: reading, tracker: Tracker(), clock: new GateClock(), on: Desk));
        }

        // The second windowed World fails on physical memory (7 GB free less 14 GB resident), and on commit too: 25.6 GB of
        // headroom less 20.4 GB leaves under the 6.4 GB reserve.
        var second = Tracker().Judge(reading: beside, kind: HostStartKind.WindowedWorld, thresholds: Desk, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (false, false, 14.0, 20.4), actual: (second.CommitCovered, second.PhysicalCovered, second.ResidentNeedGb, second.NeedGb));

        // With the commit charge low enough for two private peaks, the working set alone still keeps the second one out.
        var roomyCommit = Tracker().Judge(reading: Sample(commit: 20, limit: 64, ram: 7, starts: [running]), kind: HostStartKind.WindowedWorld, thresholds: Desk, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (true, false), actual: (roomyCommit.CommitCovered, roomyCommit.PhysicalCovered));

        // Under real pressure (free memory below the floor) nothing is admitted, a step included.
        var pressed = Sample(commit: 60, limit: 64, ram: 3.5, starts: [running]);

        foreach (var kind in Enum.GetValues<HostStartKind>()) {
            Assert.False(condition: Admits(kind: kind, reading: pressed, tracker: Tracker(), clock: new GateClock(), boundSeconds: 15, on: Desk), userMessage: kind.ToString());
        }
    }
    // Before a kind's working set is measured the fallback is three quarters of its commit peak, so an idle 32 GB desk
    // still admits one windowed World and not two.
    [Fact]
    public void AnUnmeasuredWindowedStartFallsBackToAFractionOfItsCommitPeakAsWorkingSet() {
        var tracker = new HostStartTracker();
        var idle = Tracker().Judge(reading: Sample(commit: 40, limit: 64, ram: 21), kind: HostStartKind.WindowedWorld, thresholds: Desk, clock: new GateClock(), reserve: false);
        var running = new HostRunningStart(Id: 11, Kind: HostStartKind.WindowedWorld, PrivateGb: 18, Ready: true, StartTicks: 1, WorkingSetGb: 12.6);
        var second = Tracker().Judge(reading: Sample(commit: 40, limit: 64, ram: 7.5, starts: [running]), kind: HostStartKind.WindowedWorld, thresholds: Desk, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (12.6, true), actual: (Math.Round(value: idle.ResidentNeedGb, digits: 6), idle.PhysicalCovered));
        Assert.Equal(expected: (12.6, false), actual: (Math.Round(value: second.ResidentNeedGb, digits: 6), second.PhysicalCovered));
        Assert.Equal(expected: HostLoadThresholds.WindowedWorldPeakGb * HostLoadThresholds.ResidentFraction, actual: tracker.ExpectedPeaks(kind: HostStartKind.WindowedWorld).ResidentGb);

        HostStartTracker Tracker() => new();
    }
    // The tracker measures each kind's peaks in three quantities from the same plateaued or finished processes (private
    // bytes, private working set, whole working set) and shares them under the lease root, one line per process.
    [Fact]
    public void EachKindsPeaksAreMeasuredByKindOfMemoryAndShared() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();
        var tracker = new HostStartTracker(directory.RootPath);
        var process = new HostRunningStart(Id: 8, Kind: HostStartKind.WindowedWorld, PrivateGb: 20, PrivateResidentGb: 10, StartTicks: 1, WorkingSetGb: 12);

        _ = tracker.Judge(reading: Sample(starts: [process]), kind: HostStartKind.Step, thresholds: Desk, clock: clock, reserve: false);
        clock.Advance(HostStartTracker.Plateau);
        _ = tracker.Judge(reading: Sample(), kind: HostStartKind.Step, thresholds: Desk, clock: clock, reserve: false);

        var next = new HostStartTracker(directory.RootPath);

        Assert.Equal(expected: new HostPeaks(PrivateGb: 20, ResidentGb: 10, WorkingSetGb: 12), actual: next.ExpectedPeaks(kind: HostStartKind.WindowedWorld));
        Assert.Equal(expected: "1 8 20 10 12\n", actual: File.ReadAllText(Path.Combine(directory.RootPath, HostStartTracker.ObservationDirectory, "WindowedWorld")));
        Assert.True(condition: double.IsNaN(next.ExpectedPeaks(kind: HostStartKind.HeadlessWorld).WorkingSetGb));
    }
    // A windowed World's working set is mostly memory the system can take back: a clean shader-cache mapping and shared
    // images. Free memory is judged by its private working set, so on the idle 32 GB desk (18.9 GB free, 29.5 GB of commit
    // headroom) a World whose 15.3 GB working set holds 12.8 GB privately is admitted, where judging the whole working set
    // refused it (18.9 - 15.3 leaves under the 4 GB floor). The commit check still reads private bytes, driver mirror and
    // all. A start whose private working set does not fit free memory above the floor is still refused, and the refusal
    // names both figures.
    [Fact]
    public void AStartIsJudgedByItsPrivateWorkingSetNotTheCleanPagesItShares() {
        using var split = new Puck.Testing.TemporaryDirectory();
        using var whole = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();
        var idle = Sample(commit: (100.0 * (64 - 29.5) / 64), limit: 64, ram: 18.9, starts: [Company]);
        var crowded = Sample(commit: (100.0 * (64 - 29.5) / 64), limit: 64, ram: 16, starts: [Company]);

        Observed(split.RootPath, clock, HostStartKind.WindowedWorld, id: 1, privateGb: 20.4, workingSetGb: 15.3, privateResidentGb: 12.8);
        Observed(whole.RootPath, clock, HostStartKind.WindowedWorld, id: 1, privateGb: 20.4, workingSetGb: 15.3);

        var admitted = new HostStartTracker(split.RootPath).Judge(reading: idle, kind: HostStartKind.WindowedWorld, thresholds: Desk, clock: clock, reserve: false);
        var unsplit = new HostStartTracker(whole.RootPath).Judge(reading: idle, kind: HostStartKind.WindowedWorld, thresholds: Desk, clock: clock, reserve: false);

        Assert.Equal(expected: (true, true, 12.8, 15.3, 20.4), actual: (admitted.CommitCovered, admitted.PhysicalCovered, admitted.ResidentOwnGb, admitted.WorkingSetOwnGb, admitted.OwnGb));
        Assert.Equal(expected: (true, false, 15.3), actual: (unsplit.CommitCovered, unsplit.PhysicalCovered, unsplit.ResidentOwnGb));
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: idle, tracker: new HostStartTracker(split.RootPath), clock: new GateClock(), on: Desk));

        using var error = new StringWriter();
        var waiting = new GateClock();

        Assert.False(condition: HostAdmission.Wait("puck_world_start", true, false, () => crowded, waiting, waiting.Advance, error, CancellationToken.None, Desk, TimeSpan.FromSeconds(seconds: 15), null, HostStartKind.WindowedWorld, new HostStartTracker(split.RootPath)));
        Assert.Contains(expectedSubstring: "freeRAM=16.0GB (admission needs at least 16.8GB (12.8GB expected private working set of a windowed World of its 15.3GB working set plus 0.0GB still to grow in the admitted starts plus the 4.0GB pressure floor))", actualString: error.ToString());
    }
    // A live start's private working set is what holds free memory: one booting beside the start holds back only the private
    // working set it has left to grow, and a reading that cannot split residency falls back to the whole working set.
    [Fact]
    public void ARunningStartsResidentGrowthIsReadFromItsPrivateWorkingSet() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();

        Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: 1, privateGb: 20.4, workingSetGb: 15.3, privateResidentGb: 12.8);

        HostStartDemand Beside(HostRunningStart booting) => new HostStartTracker(directory.RootPath).Judge(reading: Sample(ram: 30, starts: [booting]), kind: HostStartKind.HeadlessWorld, thresholds: Desk, clock: clock, reserve: false);
        var split = Beside(new(Id: 2, Kind: HostStartKind.WindowedWorld, PrivateGb: 10, PrivateResidentGb: 4.8, StartTicks: 2, WorkingSetGb: 6));
        var unsplit = Beside(new(Id: 3, Kind: HostStartKind.WindowedWorld, PrivateGb: 10, StartTicks: 3, WorkingSetGb: 6));

        Assert.Equal(expected: (8.0, 6.8), actual: (Math.Round(split.ResidentGrowthGb, 6), Math.Round(unsplit.ResidentGrowthGb, 6)));
        Assert.Equal(expected: (4.8, 6.0, 6.0), actual: (new HostRunningStart(Id: 2, Kind: HostStartKind.WindowedWorld, PrivateGb: 10, PrivateResidentGb: 4.8, WorkingSetGb: 6).ResidentGb, new HostRunningStart(Id: 3, Kind: HostStartKind.WindowedWorld, PrivateGb: 10, WorkingSetGb: 6).ResidentGb, new HostRunningStart(Id: 2, Kind: HostStartKind.WindowedWorld, PrivateGb: 10, PrivateResidentGb: 4.8, WorkingSetGb: 6).HeldGb));
    }
    // A heavy suite that hangs stops using CPU while its private bytes move by more than a material step (a collection, a
    // timer): the growth it was admitted for never plateaus by memory alone. After a plateau without CPU use admission
    // expects nothing more of it, so a start the commit headroom covers beside what it holds is admitted.
    [Fact]
    public void AHungStartWhosePrivateBytesWobbleStopsHoldingBackTheStartsBesideIt() {
        var clock = new GateClock();
        var tracker = new HostStartTracker();

        // 12 GB of commit headroom: a heavy suite's 10 GB peak fits beside a start holding 3.9 GB, its 6.1 GB of unspent
        // growth on top does not.
        HostStartDemand Read(int step) => tracker.Judge(reading: Sample(commit: 70, limit: 40, starts: [new(Id: 7044, Kind: HostStartKind.HeavySuite, PrivateGb: ((step % 2 == 0) ? 3.9 : 3.8), StartTicks: 7, PrivateResidentGb: 3.0, CpuSeconds: (53 + (0.01 * step)))]), kind: HostStartKind.HeavySuite, thresholds: Thresholds, clock: clock, reserve: false);

        Assert.False(condition: Read(0).CommitCovered);

        for (var step = 1; (step < 12); step++) {
            clock.Advance(TimeSpan.FromSeconds(seconds: 20));

            var demand = Read(step);

            if (clock.GetElapsedTime(0) >= HostStartTracker.Plateau) {
                Assert.Equal(expected: (true, 0.0), actual: (demand.CommitCovered, demand.GrowthGb));
            }
        }
    }
    // A start that uses CPU is never idle however flat its memory, and the idle mark is the machine's: a run that goes quiet
    // is marked once, a heavy slot opens, and the first CPU it uses again takes the mark away.
    [Fact]
    public void AnIdleMarkFollowsTheStartsCpuAndIsSharedUnderTheLeaseRoot() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();
        var tracker = new HostStartTracker(directory.RootPath);
        var ticks = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        var cpu = 100.0;

        HostRunningStart Start(DateTimeOffset? idleSince = null) => new(Id: Environment.ProcessId, Kind: HostStartKind.HeavySuite, PrivateGb: 3.9, StartTicks: ticks, CpuSeconds: cpu, IdleSince: idleSince);
        void Look(HostRunningStart start) => tracker.Judge(reading: Sample(starts: [start]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        IdleStart[] Marked() => [.. StartIdleness.Among(root: directory.RootPath, running: new Dictionary<int, long>()).Values];

        // Busy for three minutes: 5 CPU-seconds in every 20, flat memory.
        for (var step = 0; (step < 9); step++) {
            Look(Start());
            clock.Advance(TimeSpan.FromSeconds(seconds: 20));
            cpu += 5;
        }
        Assert.Empty(Marked());

        // Quiet for a plateau and a reading: marked, once, with the time it last used CPU.
        Look(Start());
        for (var step = 0; (step < 4); step++) {
            clock.Advance(TimeSpan.FromSeconds(seconds: 20));
            Look(Start());
        }

        var idle = Assert.Single(Marked());

        Assert.Equal(expected: (Environment.ProcessId, HostStartKind.HeavySuite), actual: (idle.ProcessId, idle.Kind));

        // Another observer reads the mark: the start is idle there at once, with no growth reserved.
        var other = new HostStartTracker(directory.RootPath).Judge(reading: Sample(commit: 70, limit: 40, starts: [Start(idleSince: idle.Since)]), kind: HostStartKind.HeavySuite, thresholds: Thresholds, clock: clock, reserve: false);

        Assert.Equal(expected: (true, 0.0), actual: (other.CommitCovered, other.GrowthGb));

        // CPU use resumes: the mark goes.
        cpu += 5;
        Look(Start(idleSince: idle.Since));
        Assert.Empty(Marked());
    }
    // The operating system answers a process's private working set, for this process and for another one the same user runs,
    // and it never exceeds the process's whole working set.
    [Fact]
    public void AProcessPrivateWorkingSetIsReadAndNeverExceedsItsWorkingSet() {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) { return; }

        var parent = ProcessInspection.ParentId(processId: Environment.ProcessId);

        foreach (var id in ((parent > 0) ? new[] { Environment.ProcessId, parent } : [Environment.ProcessId])) {
            using var process = System.Diagnostics.Process.GetProcessById(processId: id);
            var before = Puck.Hosting.HostMemory.PrivateResidentBytes(processId: id);

            process.Refresh();
            var workingSet = process.WorkingSet64;

            Assert.True(condition: before is > 0, userMessage: $"process {id}");
            // The process runs on between the two reads, so the working set is allowed a little slack.
            Assert.True(condition: (before <= (workingSet + (64L << 20))), userMessage: $"process {id}: private {before} of {workingSet}");
        }
    }
    // One unusual session (the owner's long play, 15.3 GB resident against about 11 for an ordinary windowed World) holds the
    // estimate up only briefly: five ordinary starts later it is back down, and a process that loaded the outlier before
    // reads the file's window, not its own memory.
    [Fact]
    public void AnOutlierRaisesTheExpectedPeakOnlyBrieflyAndOrdinaryStartsBringItBack() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();
        var longLived = new HostStartTracker(directory.RootPath);
        double Resident() => longLived.ExpectedPeaks(kind: HostStartKind.WindowedWorld).ResidentGb;

        Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: 1, privateGb: 20, workingSetGb: 15.3);
        Assert.Equal(expected: 15.3, actual: Resident());
        Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: 2, privateGb: 16, workingSetGb: 11);
        Assert.Equal(expected: 15.3, actual: Resident());
        for (var id = 3; id <= 6; ++id) {
            Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: id, privateGb: 16, workingSetGb: 11);
        }

        Assert.Equal(expected: 11.0, actual: Resident());
        Assert.Equal(expected: 16.0, actual: longLived.ExpectedPeaks(kind: HostStartKind.WindowedWorld).PrivateGb);
        Assert.Equal(expected: 11.0, actual: new HostStartTracker(directory.RootPath).ExpectedPeaks(kind: HostStartKind.WindowedWorld).ResidentGb);
    }
    // One pathological session (33 GB of commit) is the only observation of its kind. On an idle 32 GB desk with 26 GB of
    // commit headroom and 19.7 GB free, the estimate alone could refuse every later windowed start, and a refused kind runs
    // nothing that could teach the window a lower figure. An estimate the machine could not hold even with every admitted
    // start gone is judged as the lesser of itself and the unmeasured fallback, so the start goes ahead and its observation
    // brings the estimate down. Beside a running 20 GB suite the same machine could hold it once that exits, so the whole
    // estimate still judges, and so it does for a second start asked while the first holds its reservation.
    [Fact]
    public void AnInflatedEstimateNeverLocksALoneStartOutSoItsObservationsCanLowerIt() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();
        var idle = Sample(commit: (100.0 * (64 - 26) / 64), limit: 64, ram: 19.7);
        var beside = Sample(commit: (100.0 * (64 - 26) / 64), limit: 64, ram: 19.7, starts: [Company]);

        Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: 1, privateGb: 33, workingSetGb: 20, privateResidentGb: 15.1);
        Assert.Equal(expected: 33.0, actual: new HostStartTracker(directory.RootPath).ExpectedPeaks(kind: HostStartKind.WindowedWorld).PrivateGb);

        var lone = new HostStartTracker(directory.RootPath).Judge(reading: idle, kind: HostStartKind.WindowedWorld, thresholds: Desk, clock: clock, reserve: false);
        var crowded = new HostStartTracker(directory.RootPath).Judge(reading: beside, kind: HostStartKind.WindowedWorld, thresholds: Desk, clock: clock, reserve: false);

        Assert.Equal(expected: (true, true, HostLoadThresholds.WindowedWorldPeakGb, HostLoadThresholds.WindowedWorldPeakGb), actual: (lone.CommitCovered, lone.PhysicalCovered, lone.OwnGb, lone.LoneOwnGb));
        Assert.Equal(expected: (false, 33.0, HostLoadThresholds.WindowedWorldPeakGb), actual: (crowded.CommitCovered, crowded.OwnGb, crowded.LoneOwnGb));
        Assert.False(condition: Admits(kind: HostStartKind.WindowedWorld, reading: beside, tracker: new HostStartTracker(directory.RootPath), clock: new GateClock(), on: Desk));

        // The first lone start is admitted and holds its reservation, so a second asked at once is not alone.
        var asked = new HostStartTracker(directory.RootPath);

        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: idle, tracker: asked, clock: new GateClock(), on: Desk));
        Assert.False(condition: Admits(kind: HostStartKind.WindowedWorld, reading: idle, tracker: asked, clock: new GateClock(), on: Desk));

        // Each admitted lone start runs and exits at an ordinary peak: five of them push the outlier below the percentile, and
        // the kind is then admitted beside another start by its own learned figure.
        for (var id = 2; id <= 6; ++id) {
            Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: idle, tracker: new HostStartTracker(directory.RootPath), clock: new GateClock(), on: Desk), userMessage: $"start {id}");
            Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: id, privateGb: 16, workingSetGb: 11, privateResidentGb: 11);
        }

        Assert.Equal(expected: 16.0, actual: new HostStartTracker(directory.RootPath).ExpectedPeaks(kind: HostStartKind.WindowedWorld).PrivateGb);
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: beside, tracker: new HostStartTracker(directory.RootPath), clock: new GateClock(), on: Desk));
    }
    // A new normal is learned in both directions: three of the last ten starts at 14 GB raise the estimate to it, and ten
    // at 11 GB bring it back.
    [Fact]
    public void ASustainedRiseIsLearnedAndASustainedFallIsLearnedBack() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();
        var tracker = new HostStartTracker(directory.RootPath);
        var id = 0;

        for (var i = 0; i < 10; ++i) { Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: ++id, privateGb: 16, workingSetGb: 11); }
        for (var i = 0; i < 2; ++i) { Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: ++id, privateGb: 20, workingSetGb: 14); }

        Assert.Equal(expected: 11.0, actual: tracker.ExpectedPeaks(kind: HostStartKind.WindowedWorld).ResidentGb);
        Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: ++id, privateGb: 20, workingSetGb: 14);
        Assert.Equal(expected: (14.0, 20.0), actual: (tracker.ExpectedPeaks(kind: HostStartKind.WindowedWorld).ResidentGb, tracker.ExpectedPeaks(kind: HostStartKind.WindowedWorld).PrivateGb));
        for (var i = 0; i < 10; ++i) { Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: ++id, privateGb: 16, workingSetGb: 11); }

        Assert.Equal(expected: (11.0, 16.0), actual: (tracker.ExpectedPeaks(kind: HostStartKind.WindowedWorld).ResidentGb, tracker.ExpectedPeaks(kind: HostStartKind.WindowedWorld).PrivateGb));
    }
    // A kind the idle machine could not hold is refused by name at once, however long the bound: the least commit it is
    // admitted at alone (for an unmeasured windowed World, its expected 12.6 GB private working set, 0.7 of 18 GB) plus the
    // reserve exceed the commit limit. A limit that holds that least figure (19 GB) is not refused, though it cannot hold
    // the whole 18 GB guess with its reserve.
    [Fact]
    public void AStartThatCanNeverFitTheMachineIsRefusedByNameAtOnce() {
        var smallCommit = HostLoadThresholds.For(installedRamGb: 32, logicalProcessors: 16);
        var cases = new (HostLoadThresholds Thresholds, HostSample Reading, string Expected)[] {
            (smallCommit, Sample(commit: 10, limit: 13), "a windowed World can never be admitted on this machine, it needs at least 12.6GB of commit and the commit reserve is 1.3GB, over the machine's 13.0GB commit limit"),
        };

        Assert.True(condition: HostAdmission.Wait("start", false, false, () => Sample(commit: 10, limit: 19), new GateClock(), _ => Assert.Fail(message: "a lone unmeasured start that fits its least commit is admitted at once"), TextWriter.Null, CancellationToken.None, smallCommit, TimeSpan.FromMinutes(minutes: 30), null, HostStartKind.WindowedWorld, new HostStartTracker()));

        foreach (var (thresholds, reading, expected) in cases) {
            var clock = new GateClock();
            using var error = new StringWriter();

            Assert.False(condition: HostAdmission.Wait("start", false, false, () => reading, clock, _ => Assert.Fail(message: "an impossible start does not wait"), error, CancellationToken.None, thresholds, TimeSpan.FromMinutes(minutes: 30), null, HostStartKind.WindowedWorld, new HostStartTracker()));
            Assert.Contains(expectedSubstring: expected, actualString: error.ToString());
            Assert.Equal(expected: TimeSpan.Zero, actual: clock.GetElapsedTime(startingTimestamp: 0));
        }
    }
    // An unmeasured windowed World on a 16 GB machine is expected at its account budget less its authoring margin there
    // (12 - 2 = 10 GB, the top of the 8 to 10 GB a World read on such a machine), not at a large machine's 18 GB, so an idle
    // 16 GB machine with a 19 GB commit limit admits it.
    [Fact]
    public void AWindowedWorldOnASixteenGigabyteMachineExpectsItsAccountBudgetLessItsAuthoringMargin() {
        var machine = HostLoadThresholds.For(installedRamGb: 16, logicalProcessors: 6);
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.Equal(expected: 10.0, actual: HostLoadThresholds.DefaultPeakGb(installedRamGb: 16, kind: HostStartKind.WindowedWorld));
        Assert.Equal(expected: HostLoadThresholds.WindowedWorldPeakGb, actual: HostLoadThresholds.DefaultPeakGb(installedRamGb: 32, kind: HostStartKind.WindowedWorld));
        Assert.Equal(expected: HostLoadThresholds.ColdWindowedWorldPeakGb, actual: HostLoadThresholds.DefaultPeakGb(installedRamGb: 64, kind: new HostStartProfile(HostStartKind.WindowedWorld).Cold));
        Assert.Equal(expected: 10.0, actual: new HostStartTracker().ExpectedPeaks(kind: HostStartKind.WindowedWorld, thresholds: machine).PrivateGb);
        Assert.True(condition: HostAdmission.Wait("start", false, false, () => Sample(commit: 10, limit: 19, ram: 13), clock, _ => Assert.Fail(message: "an idle machine admits at once"), error, CancellationToken.None, machine, TimeSpan.FromMinutes(minutes: 30), null, HostStartKind.WindowedWorld, new HostStartTracker()));
    }
    // On an idle 16 GB machine 8.6 GB of memory is free and the pressure floor is 2 GB, so an unmeasured windowed World's
    // fallback working set (0.7 of its 10 GB, 7 GB) does not fit and nothing else is running to wait for. The guess is held
    // to the 6.6 GB the idle machine can give, so the World runs and teaches the window its real peak; the same guess beside
    // a running start still waits, and a profile this machine has measured is never clamped, so a measured need that does
    // not fit is refused.
    [Fact]
    public void AnUnmeasuredProfileIsHeldToWhatTheIdleMachineCanGiveSoItCanRunAndBeMeasured() {
        var machine = HostLoadThresholds.For(installedRamGb: 16, logicalProcessors: 6);
        var idle = Sample(commit: 10, limit: 19, ram: 8.6);
        var beside = Sample(commit: 10, limit: 19, ram: 8.6, starts: [new(Id: 900, Kind: HostStartKind.HeavySuite, PrivateGb: 2, PrivateResidentGb: 1, Ready: true, StartTicks: 900)]);
        using var directory = new Puck.Testing.TemporaryDirectory();

        var lone = new HostStartTracker().Judge(reading: idle, kind: HostStartKind.WindowedWorld, thresholds: machine, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (true, true, 10.0, 6.6, false), actual: (lone.CommitCovered, lone.PhysicalCovered, lone.OwnGb, Math.Round(value: lone.ResidentOwnGb, digits: 6), lone.Measured));
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: idle, tracker: new HostStartTracker(), clock: new GateClock(), on: machine));
        Assert.False(condition: Admits(kind: HostStartKind.WindowedWorld, reading: beside, tracker: new HostStartTracker(), clock: new GateClock(), on: machine));

        // Once measured, the profile is no longer clamped: a learned 10 GB resident does not fit 8.6 GB free, and is judged by
        // the lesser of it and the 7 GB fallback, which does not fit either.
        Observed(directory.RootPath, new GateClock(), HostStartKind.WindowedWorld, id: 1, privateGb: 11, workingSetGb: 12, privateResidentGb: 10);

        var measured = new HostStartTracker(directory.RootPath).Judge(reading: idle, kind: HostStartKind.WindowedWorld, thresholds: machine, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (false, 7.0, true), actual: (measured.PhysicalCovered, Math.Round(value: measured.ResidentOwnGb, digits: 6), measured.Measured));
    }
    // The RTX 2060 machine's reading (16 GB installed, a 22.7 GB commit limit and 9 GB committed with no Puck process
    // running): 13.7 GB of commit headroom holds the 10 GB fallback and the 2.3 GB reserve, so a first benchmark is
    // admitted. With 11 GB of headroom left by other programs it does not hold the whole guess; alone, the start is judged by
    // the least commit it can need (its 7 GB expected private working set) and admitted to be measured, and it reserves its
    // whole guess, so a second start asked at once waits. Beside another Puck start the whole guess still judges it, and a
    // commit limit that could not hold even the least figure refuses it by name.
    [Fact]
    public void ALoneUnmeasuredStartIsAdmittedAtItsLeastCommitSoTheMachineLearnsOnItsFirstBoot() {
        var machine = HostLoadThresholds.For(installedRamGb: 16, logicalProcessors: 12);
        var reserve = (22.7 * HostLoadThresholds.DefaultCommitReservePercent / 100.0);
        HostSample Reading(double headroomGb, HostRunningStart[]? starts = null) => Sample(commit: (100.0 * (22.7 - headroomGb) / 22.7), limit: 22.7, ram: 9, starts: starts);
        var vulkan = new HostStartProfile(HostStartKind.WindowedWorld, Device: "vulkan", Document: "debug");

        var roomy = new HostStartTracker().Judge(reading: Reading(13.7), kind: vulkan, thresholds: machine, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (true, 10.0, 12.3), actual: (roomy.CommitCovered, roomy.OwnGb, Math.Round(roomy.NeedGb + reserve, 1)));

        var tight = new HostStartTracker().Judge(reading: Reading(11), kind: vulkan, thresholds: machine, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (true, 7.0, 7.0), actual: (tight.CommitCovered, Math.Round(tight.OwnGb, 6), Math.Round(tight.LoneOwnGb, 6)));

        // Admitted alone, it reserves its whole 10 GB guess: the second start of this process is not alone and waits.
        var asked = new HostStartTracker();

        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Reading(11), tracker: asked, clock: new GateClock(), on: machine));
        Assert.False(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Reading(11), tracker: asked, clock: new GateClock(), on: machine));

        // Beside a ready headless World (another Puck process holding commit) the whole guess judges it.
        var beside = new HostStartTracker().Judge(reading: Reading(11, [new(Id: 900, Kind: HostStartKind.HeadlessWorld, PrivateGb: 1, PrivateResidentGb: 1, Ready: true, StartTicks: 900)]), kind: vulkan, thresholds: machine, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (false, 10.0), actual: (beside.CommitCovered, beside.OwnGb));

        // A 7.5 GB commit limit cannot hold the least figure and its 0.75 GB reserve: refused by name, at once.
        using var error = new StringWriter();
        var clock = new GateClock();

        Assert.False(condition: HostAdmission.Wait("benchmark", true, false, () => Sample(commit: 10, limit: 7.5, ram: 9), clock, _ => Assert.Fail(message: "an impossible start does not wait"), error, CancellationToken.None, machine, TimeSpan.FromMinutes(minutes: 120), null, vulkan, new HostStartTracker()));
        Assert.Contains(expectedSubstring: "a windowed World of debug on vulkan can never be admitted on this machine, it needs at least 7.0GB of commit", actualString: error.ToString());
    }
    // A windowed World is learned and judged per document: a small root world measured at 4 GB is admitted beside a running
    // debug World where the debug world's own 13.8 GB is not, and a document this machine has not measured is judged by what
    // any World of its kind, backend and store reached here (every document's observation is also one of those), falling to
    // the fallback only when nothing of the kind has been measured.
    [Fact]
    public void AWindowedWorldIsLearnedAndJudgedByTheDocumentItBoots() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();
        var debug = new HostStartProfile(HostStartKind.WindowedWorld, Device: "directx", Document: "debug");
        var gallery = debug with { Document = "gallery" };
        var lead = HostLoadThresholds.For(installedRamGb: 64, logicalProcessors: 24);

        Assert.Equal(expected: HostLoadThresholds.WindowedWorldPeakGb, actual: new HostStartTracker(directory.RootPath).ExpectedPeaks(kind: gallery, thresholds: lead).PrivateGb);

        Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: 1, privateGb: 13.8, workingSetGb: 11, privateResidentGb: 10, marked: debug);

        // Unmeasured, the gallery borrows the debug world's figure through its kind, backend and store.
        Assert.Equal(expected: (13.8, 13.8), actual: (new HostStartTracker(directory.RootPath).ExpectedPeaks(kind: gallery).PrivateGb, new HostStartTracker(directory.RootPath).ExpectedPeaks(kind: debug.AnyDocument).PrivateGb));

        new HostStartTracker(directory.RootPath).Finish(profile: gallery, process: (2, 2), peaks: new HostPeaks(PrivateGb: 4, ResidentGb: 3, WorkingSetGb: 3.5));

        var next = new HostStartTracker(directory.RootPath);

        Assert.Equal(expected: (13.8, 4.0), actual: (next.ExpectedPeaks(kind: debug).PrivateGb, next.ExpectedPeaks(kind: gallery).PrivateGb));
        Assert.Equal(expected: ["WindowedWorld.directx", "WindowedWorld.directx@debug", "WindowedWorld.directx@gallery"], actual: [.. next.MeasuredProfiles().Select(static profile => profile.Name)]);

        // Beside a running debug World, 16.4 GB of commit headroom (6.4 GB of it the reserve) and 20 GB free admit the gallery
        // and not a second debug World.
        HostRunningStart[] running = [new(Id: 1, Kind: HostStartKind.WindowedWorld, PrivateGb: 13.8, PrivateResidentGb: 10, Ready: true, StartTicks: 1, Marked: debug)];
        var reading = Sample(commit: (100.0 * (64 - 16.4) / 64), limit: 64, ram: 20, starts: running);

        Assert.True(condition: next.Judge(reading: reading, kind: gallery, thresholds: lead, clock: clock, reserve: false).Covered);
        Assert.False(condition: next.Judge(reading: reading, kind: debug, thresholds: lead, clock: clock, reserve: false).Covered);
    }
    // A World that is stopped records what it reached as one settled observation of its profile, though nothing sampled it
    // while it ran and it never lasted a plateau, and the profile's window then judges the next start of it.
    [Fact]
    public void AStoppingWorldRecordsItsPeaksWithoutHavingBeenSampled() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var windowed = new HostStartProfile(Kind: HostStartKind.WindowedWorld, Device: "directx");
        var tracker = new HostStartTracker(directory.RootPath);

        tracker.Finish(profile: windowed, process: (5, 1), peaks: new HostPeaks(PrivateGb: 6.5, ResidentGb: 4.25, WorkingSetGb: 5.0));

        var learned = new HostStartTracker(directory.RootPath).ExpectedPeaks(kind: windowed);

        Assert.Equal(expected: (6.5, 4.25, 5.0), actual: (learned.PrivateGb, learned.ResidentGb, learned.WorkingSetGb));
        Assert.Equal(expected: HostLoadThresholds.WindowedWorldPeakGb, actual: new HostStartTracker(directory.RootPath).ExpectedPeaks(kind: HostStartKind.WindowedWorld).PrivateGb);
    }
    // Starts that have not finished growing hold their remaining working-set growth out of free memory too: a headless
    // World that has not reported ready keeps the start waiting that would be admitted beside it once it has.
    [Fact]
    public void FreeMemoryLeavesRoomForTheGrowthOfStartsAlreadyRunning() {
        HostRunningStart[] booting = [new(Id: 9, Kind: HostStartKind.HeadlessWorld, PrivateGb: 0.5, StartTicks: 1, WorkingSetGb: 0.4)];
        HostRunningStart[] ready = [new(Id: 9, Kind: HostStartKind.HeadlessWorld, PrivateGb: 0.5, Ready: true, StartTicks: 1, WorkingSetGb: 0.4)];
        // A step's fallback working set is 1.4 GB (0.7 of its 2 GB); the unready headless World still has 4.2 - 0.4 GB to grow.
        var crowded = new HostStartTracker().Judge(reading: Sample(ram: 9, starts: booting), kind: HostStartKind.Step, thresholds: Desk, clock: new GateClock(), reserve: false);
        var settled = new HostStartTracker().Judge(reading: Sample(ram: 9, starts: ready), kind: HostStartKind.Step, thresholds: Desk, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (5.2, false), actual: (Math.Round(value: crowded.ResidentNeedGb, digits: 6), crowded.PhysicalCovered));
        Assert.Equal(expected: (1.4, true), actual: (Math.Round(value: settled.ResidentNeedGb, digits: 6), settled.PhysicalCovered));
    }
    [Fact]
    public void AnUnreadableFreeMemoryOrAMissingFloorIsNotJudged() {
        Assert.True(condition: Desk.PhysicalCovers(reading: Sample(ram: double.NaN), needGb: 1000));
        Assert.True(condition: (Desk with { PressureRamGb = null }).PhysicalCovers(reading: Sample(ram: 1), needGb: 1000));
        Assert.True(condition: Desk.PhysicalCovers(reading: Sample(ram: 22), needGb: 18));
        Assert.False(condition: Desk.PhysicalCovers(reading: Sample(ram: 21.9), needGb: 18));
    }
    [Fact]
    public void IdleAdmissionHasNoWaitOrRefusalEvenWithReuseNodes() {
        using var error = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("build", false, false, () => Sample(), new GateClock(), _ => Assert.Fail(message: "idle must not wait"), error, CancellationToken.None, Thresholds));
        Assert.Equal("", error.ToString());
    }
    [InlineData("ram")]
    [InlineData("disk")]
    [InlineData("gpu")]
    [Theory]
    public void CapacityWaitsVisiblyThenReturnsRegardlessOfLineThrottling(string pressure) {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();
        var busy = pressure switch {
            "ram" => Sample(ram: 1),
            "disk" => Sample(disk: 1),
            _ => Sample(gpu: "Puck.World 42"),
        };

        Assert.True(condition: HostAdmission.Wait("step", true, false, () => ((++calls <= 2) ? busy : Sample()), clock, clock.Advance, error, CancellationToken.None, Thresholds));
        Assert.Equal(actual: calls, expected: 3);
        Assert.Equal(TimeSpan.FromSeconds(seconds: 20), clock.GetElapsedTime(startingTimestamp: 0));
        Assert.Contains("waiting for host capacity before step", error.ToString());
        Assert.Contains("capacity returned for step", error.ToString());
        Assert.Equal(2, error.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').Length);
    }
    [Fact]
    public void AWaitNamesWhatHoldsItBackAndEachNewGpuHolder() {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();
        HostSample[] readings = [Sample(gpu: "testhost 77"), Sample(gpu: "testhost 77"), Sample(gpu: "Puck.World 9"), Sample(ram: 1), Sample()];

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", true, false, () => readings[calls++], clock, clock.Advance, error, CancellationToken.None, Thresholds));
        var lines = error.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');

        Assert.Equal(expected: 3, actual: lines.Length);
        Assert.EndsWith(actualString: lines[0].TrimEnd(), expectedEndString: "the GPU is held by testhost 77.");
        Assert.EndsWith(actualString: lines[1].TrimEnd(), expectedEndString: "the GPU is held by Puck.World 9.");
        Assert.Contains(actualString: lines[2], expectedSubstring: "capacity returned for Puck.World.Tests");
        Assert.Contains(actualString: HostAdmissionReason(ram: 1), expectedSubstring: "no memory headroom: freeRAM=1.0GB");
    }
    [Fact]
    public void OnlyADeviceStepWaitsForTheGpuHolder() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("baselines browser-parity", false, false, () => Sample(gpu: "Puck.World.Tests 7"), clock, _ => Assert.Fail(message: "a CPU step must not wait on the GPU"), error, CancellationToken.None, Thresholds));
        Assert.Equal(expected: "", actual: error.ToString());

        var calls = 0;

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", true, false, () => ((calls++ == 0) ? Sample(gpu: "Puck.World 9") : Sample()), clock, clock.Advance, error, CancellationToken.None, Thresholds));
        Assert.Contains(actualString: error.ToString(), expectedSubstring: "the GPU is held by Puck.World 9");

        // A CPU step still waits for memory headroom, and names it rather than a GPU holder.
        using var busy = new StringWriter();

        calls = 0;
        Assert.True(condition: HostAdmission.Wait("affected", false, false, () => ((calls++ == 0) ? Sample(ram: 1, gpu: "Puck.World 9") : Sample(gpu: "Puck.World 9")), clock, clock.Advance, busy, CancellationToken.None, Thresholds));
        Assert.Contains(actualString: busy.ToString(), expectedSubstring: "no memory headroom: freeRAM=1.0GB");
        Assert.DoesNotContain(expectedSubstring: "GPU", actualString: busy.ToString());
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void CpuLoadIsAdvisoryAndNeverHoldsOrRefusesAStep(bool device) {
        var clock = new GateClock();
        using var error = new StringWriter();

        // A CPU that never falls under the threshold, with memory, disk and the GPU free.
        Assert.True(condition: HostAdmission.Wait("build", device, false, () => Sample(cpu: 99), clock, _ => Assert.Fail(message: "CPU load must not hold a step back"), error, CancellationToken.None, Thresholds));
        var lines = error.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');

        Assert.Single(collection: lines);
        Assert.Contains(actualString: lines[0], expectedSubstring: "build runs under cpu=99%");
        Assert.DoesNotContain(expectedSubstring: "waiting", actualString: lines[0]);
    }
    [Fact]
    public void HeavyStartsShareTheMachineByHeadroomAndWaitOnCpuOnlyBesideAnotherHeavyHold() {
        var clock = new GateClock();
        using var error = new StringWriter();

        // Another process's heavy test run no longer holds a heavy start back: memory headroom judges it.
        Assert.True(condition: HostAdmission.Wait("lease heavy b", false, true, () => Sample(heavy: "Puck.World.Tests 11"), clock, _ => Assert.Fail(message: "a heavy start with headroom must not wait on another heavy run"), error, CancellationToken.None, Thresholds, tracker: new HostStartTracker()));
        // A lone heavy start runs under any CPU load, which only slows it.
        Assert.True(condition: HostAdmission.Wait("lease heavy b", false, true, () => Sample(cpu: 95), clock, _ => Assert.Fail(message: "CPU load must not hold a lone heavy start back"), error, CancellationToken.None, Thresholds, tracker: new HostStartTracker()));

        // Beside another lane's running heavy hold, a further heavy start waits while the CPU is over the capacity threshold
        // (50% on six threads), naming the holder, for 15 minutes; a light step never waits on it.
        var hold = HeavyHold(privateGb: 10);

        using var crowded = new StringWriter();

        HostSample Reading() => Sample(cpu: ((clock.GetElapsedTime(startingTimestamp: 0).TotalMinutes < 15) ? 95 : 20), holds: [hold]);
        var started = clock.GetTimestamp();

        Assert.True(condition: HostAdmission.Wait("lease heavy b", false, true, Reading, clock, clock.Advance, crowded, CancellationToken.None, Thresholds, tracker: new HostStartTracker()));
        Assert.Equal(actual: crowded.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').Select(selector: static line => line.TrimEnd()).ToArray(), expected: [
            "gate: waiting for host capacity before lease heavy b (at most 120 minutes): no CPU headroom: cpu=95% with 1 other heavy holder(s) running (other-lane run PID 41); a further heavy start needs cpu under 50%.",
            "gate: still waiting before lease heavy b after 10 minutes: no CPU headroom: cpu=95% with 1 other heavy holder(s) running (other-lane run PID 41); a further heavy start needs cpu under 50%.",
            "gate: capacity returned for lease heavy b.",
        ]);
        Assert.Equal(expected: TimeSpan.FromMinutes(minutes: 15), actual: clock.GetElapsedTime(startingTimestamp: started));
        Assert.True(condition: HostAdmission.Wait("baselines state", false, false, () => Sample(cpu: 95, holds: [hold]), clock, _ => Assert.Fail(message: "a light step must not wait on CPU"), new StringWriter(), CancellationToken.None, Thresholds, tracker: new HostStartTracker()));
    }
    [Fact]
    public void AHeavyStartIsRefusedOnlyAtItsTwoHourBound() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("lease heavy b", false, true, () => Sample(cpu: 95, holds: [HeavyHold(privateGb: 10)]), clock, clock.Advance, error, CancellationToken.None, Thresholds, tracker: new HostStartTracker()));
        Assert.Equal(expected: TimeSpan.FromHours(hours: 2), actual: clock.GetElapsedTime(startingTimestamp: 0));
        Assert.Equal(expected: CliLease.DefaultWait, actual: TimeSpan.FromHours(hours: 2));
    }
    // Another process's hold counts what its process tree still has to grow toward the hold's expected peak from the grant,
    // before any start in it appears, so a second heavy start is admitted beside a first only while the commit headroom
    // covers both peaks; a tracked start inside the hold is counted once, by the greater of the two; and the hold tapers to
    // what it holds once its tree stops growing for a plateau.
    [Fact]
    public void AnotherProcesssHoldReservesItsTreesGrowthFromTheGrantAndCountsAStartInsideItOnce() {
        var clock = new GateClock();
        var tracker = new HostStartTracker();
        // 30 GB of commit headroom on a 40 GB limit (25% charged): a heavy suite expects 10 GB.
        HostSample Reading(double treeGb, params HostRunningStart[] starts) => Sample(commit: 25, holds: [HeavyHold(privateGb: treeGb, cpuSeconds: clock.GetElapsedTime(startingTimestamp: 0).TotalSeconds, members: [.. starts.Select(selector: static start => start.Id)])], starts: starts);

        // The hold was just granted and its tree holds 0.5 GB: 9.5 GB still to grow, beside the start's own 10 GB.
        var granted = tracker.Judge(clock: clock, kind: HostStartKind.HeavySuite, reading: Reading(treeGb: 0.5), reserve: false, thresholds: Thresholds);

        Assert.Equal(expected: 9.5, actual: granted.GrowthGb, precision: 6);
        Assert.True(condition: granted.CommitCovered);
        // A third heavy start beside two such holds would need 10 + 9.5 + 9.5 = 29 GB of the 30: it fits, a fourth does not.
        var two = Sample(commit: 25, holds: [HeavyHold(privateGb: 0.5), (HeavyHold(privateGb: 0.5) with { Id = 42, StartTicks = 42, Members = [42] })]);

        Assert.True(condition: tracker.Judge(clock: clock, kind: HostStartKind.HeavySuite, reading: two, reserve: false, thresholds: Thresholds).CommitCovered);
        Assert.False(condition: tracker.Judge(clock: clock, kind: HostStartKind.HeavySuite, reading: (two with { Holds = [.. two.Holds!, (HeavyHold(privateGb: 0.5) with { Id = 43, StartTicks = 43, Members = [43] })] }), reserve: false, thresholds: Thresholds).CommitCovered);

        // A replica appears inside the first hold: the hold's 9.5 GB and the replica's own 8.5 GB still to grow are one
        // growth, the greater, never 18 GB.
        var replica = new HostRunningStart(Id: 77, Kind: HostStartKind.ReproduceReplica, PrivateGb: 0.5, PrivateResidentGb: 0.5, StartTicks: 77, CpuSeconds: 0);
        var inside = tracker.Judge(clock: clock, kind: HostStartKind.HeavySuite, reading: Reading(1.0, replica), reserve: false, thresholds: Thresholds);

        Assert.Equal(expected: 9.0, actual: inside.GrowthGb, precision: 6);
        // The tree stops growing for a plateau while it keeps using CPU: the hold counts only what it holds.
        clock.Advance(HostStartTracker.Plateau);
        Assert.Equal(expected: 0, actual: tracker.Judge(clock: clock, kind: HostStartKind.HeavySuite, reading: Reading(1.0, replica with { CpuSeconds = 60 }), reserve: false, thresholds: Thresholds).GrowthGb, precision: 6);
    }

    private static string HostAdmissionReason(double ram) {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();

        // Too little memory first, then headroom.
        _ = HostAdmission.Wait("x", true, false, () => ((calls++ == 0) ? Sample(ram: ram) : Sample()), clock, clock.Advance, error, CancellationToken.None, Thresholds);
        return error.ToString();
    }

    // A commit reading that leaves under the reserve (10% of the limit: 4 GB of 40) holds every start back while 64 GB of RAM
    // and 100 GB of disk are free, and says so; a reading with room admits at once. The stage a caller shows is the phrase
    // "waiting for memory headroom: commit headroom 2.0GB (95% of limit charged)". A fixed share of the limit gates nothing:
    // an ordinary step is admitted at 80% charged because its 2 GB need and the reserve fit in the 8 GB of headroom.
    [Fact]
    public void ACommitHeadroomUnderItsReserveHoldsAStepBackAndIsNamedAndAboveItAdmits() {
        var clock = new GateClock();
        var calls = 0;
        var stages = new List<string>();
        using var error = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("puck_world_start", false, false, () => ((calls++ < 2) ? Sample(commit: 95) : Sample(commit: 60)), clock, clock.Advance, error, CancellationToken.None, Reserved, stage: stages.Add));
        Assert.Equal(expected: 3, actual: calls);
        Assert.Equal(expected: ["waiting for memory headroom: commit headroom 2.0GB (95% of limit charged)", "waiting for memory headroom: commit headroom 2.0GB (95% of limit charged)"], actual: stages);
        Assert.Contains(actualString: error.ToString(), expectedSubstring: "no memory headroom: commit headroom 2.0GB (95% of limit charged) (admission needs at least 4.0GB (the commit reserve, 10% of the limit)).");
        Assert.DoesNotContain(expectedSubstring: "freeRAM", actualString: error.ToString());

        using var idle = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("puck_world_start", false, false, () => Sample(commit: 80), clock, _ => Assert.Fail(message: "80% commit must admit a 2 GB step"), idle, CancellationToken.None, Reserved));
        Assert.Equal(expected: "", actual: idle.ToString());
        // A platform that reports no commit limit is never held back by it.
        Assert.True(condition: HostAdmission.Wait("x", false, false, () => Sample(), clock, _ => Assert.Fail(message: "no commit reading must admit"), idle, CancellationToken.None, Reserved));
        // 90% leaves exactly the reserve and nothing for the step's own 2 GB: the start's need, not the charge, refuses it.
        var bounded = new GateClock();

        Assert.False(condition: HostAdmission.Wait("x", false, false, () => Sample(commit: 90), bounded, bounded.Advance, idle, CancellationToken.None, Reserved, bound: TimeSpan.FromSeconds(seconds: 25)));
        Assert.Equal(expected: TimeSpan.FromSeconds(seconds: 25), actual: bounded.GetElapsedTime(startingTimestamp: 0));
        Assert.Contains(actualString: idle.ToString(), expectedSubstring: "(at most 25 seconds)");
        Assert.Contains(actualString: idle.ToString(), expectedSubstring: "commit headroom 4.0GB (admission needs at least 6.0GB (2.0GB expected peak of a step plus 0.0GB still to grow in 0 admitted start(s) plus the 4.0GB commit reserve))");
    }
    // The commit condition and the memory and disk conditions hold a step back together, each named with its own need.
    [Fact]
    public void EachShortfallIsNamedWithItsOwnNeedWhenSeveralHoldAStepBack() {
        var clock = new GateClock();
        var calls = 0;
        using var error = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("x", false, false, () => ((calls++ == 0) ? Sample(commit: 95, ram: 1, disk: 2) : Sample()), clock, clock.Advance, error, CancellationToken.None, Reserved));
        Assert.Contains(actualString: error.ToString(), expectedSubstring: "no memory headroom: commit headroom 2.0GB (95% of limit charged) (admission needs at least 4.0GB (the commit reserve, 10% of the limit)); freeRAM=1.0GB (admission needs at least 2.0GB (memory pressure)); freeDisk=2.0GB (admission needs at least 10.0GB).");
    }
    [InlineData(90.0, false)]
    [InlineData(90.5, true)]
    [InlineData(double.NaN, false)]
    [Theory]
    public void CommitHeadroomIsWhatTheReserveLeavesOfTheLimitAndAnUnreadableChargeIsNotJudged(double percent, bool held) {
        var reading = (double.IsNaN(d: percent) ? Sample() : Sample(commit: percent));

        Assert.Equal(expected: held, actual: !Reserved.CommitHasHeadroom(reading: reading));
        Assert.Equal(expected: double.IsNaN(d: percent), actual: double.IsNaN(d: reading.CommitPercent));
        Assert.True(condition: (Reserved with { CommitReservePercent = null }).CommitHasHeadroom(reading: Sample(commit: 99)));
        Assert.True(condition: (Reserved with { CommitReservePercent = null }).CommitCovers(reading: Sample(commit: 99), needGb: 1000));
    }
    // The need and the reserve are both in the commit check: 16 GB of headroom covers a 10 GB need with 4 GB in reserve, not 12.1.
    [Fact]
    public void CommitCoversNeedsTheStartsOwnNeedPlusTheReserve() {
        var reading = Sample(commit: 60);

        Assert.Equal(expected: 4.0, actual: Reserved.CommitReserveGb(reading: reading));
        Assert.True(condition: Reserved.CommitCovers(reading: reading, needGb: 12));
        Assert.False(condition: Reserved.CommitCovers(reading: reading, needGb: 12.1));
        Assert.True(condition: Thresholds.CommitCovers(reading: reading, needGb: 16));
    }
    [Fact]
    public void ACommitHeadroomUnderItsReserveAlsoEndsCapacityInTheMonitorAndNamesItInTheReading() {
        var monitor = new HostLoadMonitor(cpuSamples: 1, thresholds: Reserved);

        Assert.Equal(expected: ["GPU idle cpu=0% freeRAM=64.0GB freeDisk=100.0GB commit=95% reuseNodes=42", "LOADED cpu=0% freeRAM=64.0GB freeDisk=100.0GB commit=95% reuseNodes=42"], actual: monitor.Observe(sample: Sample(commit: 95)));
        Assert.False(condition: monitor.HasCapacity);
        Assert.Equal(expected: ["CAPACITY cpu=0% freeRAM=64.0GB freeDisk=100.0GB commit=60% reuseNodes=42"], actual: monitor.Observe(sample: Sample(commit: 60)));
        Assert.True(condition: monitor.HasCapacity);
    }
    // A verb below the gate admits each World start through WorldLaunch, and the installed admission is HostAdmission: the
    // start waits at a 98% commit charge and goes ahead at 60%, each replica admitted on its own reading, and a bound that
    // passes first refuses the start by exception.
    [Fact]
    public void AVerbsWorldStartWaitsWithoutHeadroomForItsPeakAndRunsWithItThroughTheInstalledAdmission() {
        using var checkout = new GitScratchCheckout();
        var clock = new GateClock();
        var readings = 0;
        var asked = new List<(string Step, bool Device, bool Heavy, HostStartKind Kind)>();
        using var error = new StringWriter();
        HostAdmit admit = (checkout, step, device, heavySuite, kind, bound, reserve, progress, stage, cancellationToken) => {
            asked.Add(item: (step, device, heavySuite, kind.Kind));

            return Task.FromResult(result: HostAdmission.Wait(step, device, heavySuite, () => ((readings++ < 2) ? Sample(commit: 98, limit: 64) : Sample(commit: 60, limit: 64)), clock, clock.Advance, error, cancellationToken, Thresholds, bound, stage, kind, reserve: reserve));
        };

        WorldAdmission.Install(admit: admit);
        try {
            var worker = new WorldLaunchRequest(Artifact: "Puck.World.dll", Step: "test worker") { Arguments = ["--test-batch", "batch.json"], Checkout = checkout.Root, HeavySuite = true, Shape = WorldShape.TestBatch };

            // The first replica waits two samples at 90% and starts on the third.
            WorldLaunch.Admit(cancellationToken: TestContext.Current.CancellationToken, request: worker).Dispose();
            Assert.Equal(expected: 4, actual: readings);
            Assert.Equal(expected: TimeSpan.FromSeconds(seconds: 20), actual: clock.GetElapsedTime(startingTimestamp: 0));
            // Alone and unmeasured, the replica waits only for its lone need, its expected private working set.
            Assert.Contains(actualString: error.ToString(), expectedSubstring: "no memory headroom: commit headroom 1.3GB (admission needs at least 6.3GB (6.3GB lone need (9.0GB expected) of a test-batch replica");
            Assert.Contains(actualString: error.ToString(), expectedSubstring: "capacity returned for test worker");

            // The next replica is admitted on its own reading: 60% starts at once, as does a World booted in process.
            WorldLaunch.Admit(cancellationToken: TestContext.Current.CancellationToken, request: worker).Dispose();
            WorldAdmission.WaitInProcess(cancellationToken: TestContext.Current.CancellationToken, step: "bench");
            Assert.Equal(expected: 7, actual: readings);
            Assert.Equal(expected: TimeSpan.FromSeconds(seconds: 20), actual: clock.GetElapsedTime(startingTimestamp: 0));
            Assert.Equal(expected: [("test worker", false, true, HostStartKind.ReproduceReplica), ("test worker", false, true, HostStartKind.ReproduceReplica), ("test worker", false, true, HostStartKind.ReproduceReplica), ("test worker", false, true, HostStartKind.ReproduceReplica), ("bench", false, false, HostStartKind.HeadlessWorld)], actual: asked);

            // A machine that never recovers refuses the start at the admission's bound, and the verb's start never happens.
            readings = int.MinValue;
            Assert.Throws<HostAdmissionRefusedException>(testCode: () => WorldAdmission.WaitInProcess(cancellationToken: TestContext.Current.CancellationToken, step: "canary"));
        } finally {
            WorldAdmission.Install(admit: null);
        }

        // Without an installed admission a start is admitted at once.
        WorldAdmission.WaitInProcess(cancellationToken: TestContext.Current.CancellationToken, step: "canary");
    }
    [Theory]
    [InlineData(WorldShape.TestBatch, "heavy")]
    [InlineData(WorldShape.Windowed, "gpu")]
    public async Task WorldLaunchWaitsForHeadroomWithoutAnExclusiveHold(WorldShape shape, string resource) {
        using var checkout = new GitScratchCheckout();
        var token = TestContext.Current.CancellationToken;
        var root = await CliLease.RootAsync(checkout.Root, token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headroom = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        HostAdmit admit = async (place, _, _, _, _, bound, reserve, _, _, cancel) => {
            Assert.Equal(checkout.Root, place);
            if (reserve) {
                Assert.Equal(TimeSpan.Zero, bound);
                Assert.True(CliLease.Inspect(root, resource, DateTimeOffset.UtcNow).HolderAlive);
                return true;
            }
            Assert.False(CliLease.Inspect(root, resource, DateTimeOffset.UtcNow).HolderAlive);
            waiting.SetResult();
            return await headroom.Task.WaitAsync(cancel);
        };
        WorldAdmission.Install(admit);
        try {
            var request = new WorldLaunchRequest("launch law", "Puck.World.dll") { Checkout = checkout.Root, HeavySuite = shape == WorldShape.TestBatch, Shape = shape };
            var pending = WorldLaunch.AdmitAsync(request, token);
            await waiting.Task.WaitAsync(token);
            using (await CliLease.AcquireAsync(root, resource, "other lane", TimeSpan.Zero, TimeProvider.System, checkout.Root, token)) {
                Assert.Equal("other lane", CliLease.Inspect(root, resource, DateTimeOffset.UtcNow).Holder);
            }
            headroom.SetResult(true);
            using var launch = await pending;
            Assert.True(CliLease.Inspect(root, resource, DateTimeOffset.UtcNow).HolderAlive);
        } finally {
            headroom.TrySetResult(false);
            WorldAdmission.Install(null);
        }
        Assert.False(CliLease.Inspect(root, resource, DateTimeOffset.UtcNow).HolderAlive);
    }

    // A start that finds this process already holding the lease (another lane's World started through the same adapter, or its
    // own lane's earlier World) waits for its turn in the queue and for headroom, however short the machine is, and is refused
    // only when no wait could admit it. Only the reservation made under the lease's own guard reads once.
    [Theory]
    [InlineData(WorldShape.TestBatch, "heavy")]
    [InlineData(WorldShape.Windowed, "gpu")]
    public async Task AStartWaitsItsTurnAndItsHeadroomWhileThisProcessHoldsTheLeaseAndOnlyOneThatCanNeverFitIsRefused(WorldShape shape, string resource) {
        using var checkout = new GitScratchCheckout();
        var token = TestContext.Current.CancellationToken;
        var root = await CliLease.RootAsync(checkout.Root, token);
        var clock = new GateClock();
        var wait = TimeSpan.FromHours(1);
        var tight = false;
        var impossible = false;
        var shortReadings = new SemaphoreSlim(0);
        var asked = new System.Collections.Concurrent.ConcurrentQueue<(bool Reserve, TimeSpan? Bound)>();
        using var error = new StringWriter();
        HostSample Reading() => (impossible ? Sample(commit: 10, limit: 1) : (tight ? Sample(commit: 98, limit: 128) : Sample(commit: 60, limit: 128)));
        HostAdmit admit = (_, step, device, heavySuite, kind, bound, reserve, _, stage, cancel) => {
            asked.Enqueue(item: (reserve, bound));

            // The third delay is the last: the machine has room again.
            var delays = 0;

            return Task.FromResult(result: HostAdmission.Wait(step, device, heavySuite, Reading, clock, span => {
                clock.Advance(span);
                if (tight && (++delays == 3)) {
                    tight = false;
                    _ = shortReadings.Release();
                }
            }, error, cancel, Thresholds, bound, stage, kind, reserve: reserve));
        };

        WorldAdmission.Install(admit: admit);
        try {
            var lane = new WorldLaunchRequest("launch law", "Puck.World.dll") { AdmissionWait = wait, Checkout = checkout.Root, HeavySuite = (shape == WorldShape.TestBatch), Holder = "a", Lane = "a", Shape = shape };

            using var first = await WorldLaunch.AdmitAsync(lane, token);

            Assert.True(CliLease.Inspect(root, resource, DateTimeOffset.UtcNow).HolderAlive);

            // Another lane's start, short of headroom: it waits the shortage out, then runs beside the lane that holds the
            // lease in a slot of its own once headroom covers it.
            tight = true;

            var queued = WorldLaunch.AdmitAsync((lane with { Holder = "b", Lane = "b" }), token);

            await shortReadings.WaitAsync(token);
            _ = await queued.WaitAsync(token);
            Assert.Equal(["a", "b"], CliLease.Inspect(root, resource, DateTimeOffset.UtcNow).Holders.Select(static held => held.Holder));
            first.Dispose();

            using var second = await queued;

            Assert.Equal("b", CliLease.Inspect(root, resource, DateTimeOffset.UtcNow).Holder);

            // A second start of the lane that holds waits for headroom too, then shares the lease.
            tight = true;

            var shared = WorldLaunch.AdmitAsync((lane with { Holder = "b", Lane = "b" }), token);

            await shortReadings.WaitAsync(token);

            using var third = await shared;

            // Waiting spends the start's own bound; only the reservation under the lease's guard reads once.
            Assert.All(asked.Where(static call => !call.Reserve), static call => Assert.NotEqual(TimeSpan.Zero, call.Bound));
            Assert.Equal(1, asked.Count(call => (call.Reserve && (call.Bound == wait))));
            Assert.Contains(asked, static call => (call.Reserve && (call.Bound == TimeSpan.Zero)));

            // A start that can never fit is refused by name at once, with the lease still held.
            impossible = true;

            var refused = await Assert.ThrowsAsync<HostAdmissionRefusedException>(() => WorldLaunch.AdmitAsync((lane with { Holder = "c", Lane = "c" }), token));

            Assert.Contains("can never be admitted on this machine", refused.Reason);
            Assert.Equal("b", CliLease.Inspect(root, resource, DateTimeOffset.UtcNow).Holder);
        } finally {
            WorldAdmission.Install(null);
        }
        Assert.False(CliLease.Inspect(root, resource, DateTimeOffset.UtcNow).HolderAlive);
    }

    // The launch's own view of a World (whether it opens a device, so takes the GPU lease) and the host's reading of the
    // command line it builds agree for every shape, so a running World is classified as the launch admitted it.
    [InlineData(WorldShape.Headless)]
    [InlineData(WorldShape.Windowed)]
    [InlineData(WorldShape.Document)]
    [InlineData(WorldShape.TestBatch)]
    [Theory]
    public void TheLaunchAndTheHostReadAWorldsDeviceAlike(WorldShape shape) {
        var request = new WorldLaunchRequest(Artifact: "Puck.World.dll", Step: "law") { Arguments = ((shape == WorldShape.TestBatch) ? ["--test-batch", "batch.json"] : []), Shape = shape, World = "worlds/debug" };

        Assert.Equal(expected: request.OpensDevice, actual: WorldDevices.OpensDevice(arguments: request.CommandLine(), start: 1));
        Assert.Equal(expected: request.Kind, actual: WorldDevices.StartKind(arguments: request.CommandLine(), start: 1));
        Assert.Contains(collection: request.CommandLine(), expected: "none");
    }
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--headless" }, false)]
    [InlineData(new[] { "--headless", "true" }, false)]
    [InlineData(new[] { "--headless", "false" }, true)]
    [InlineData(new[] { "--devices", "none", "--test-batch", "batch.json" }, false)]
    [Theory]
    public void AWorldOpensADeviceUnlessItBootsHeadlessOrRunsATestBatch(string[] arguments, bool opens) =>
        Assert.Equal(expected: opens, actual: WorldDevices.OpensDevice(arguments: arguments));
    // A windowed World is expected to peak at 18 GB. At 60% of a 40 GB limit the charge is 24 GB, under the 75% share, but
    // the 16 GB of headroom cannot hold it beside another Puck process: the start waits, names the peak, and goes ahead once
    // 20 GB are free. Alone, the same reading admits it at the least commit it can need (its 12.6 GB expected private
    // working set), since it has not been measured here.
    [Fact]
    public void AWindowedStartWaitsWhenTheHeadroomIsBelowItsPeakEvenAtSixtyPercentCommit() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("puck_world_start", true, false, () => Sample(commit: 60, starts: [Company]), clock, clock.Advance, error, CancellationToken.None, Thresholds, TimeSpan.FromSeconds(seconds: 25), null, HostStartKind.WindowedWorld, new HostStartTracker()));
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(commit: 60), tracker: new HostStartTracker(), clock: new GateClock()));
        Assert.Contains(actualString: error.ToString(), expectedSubstring: "no memory headroom: commit headroom 16.0GB (admission needs at least 18.0GB (18.0GB expected peak of a windowed World plus 0.0GB still to grow in 0 admitted start(s) plus the 0.0GB commit reserve))");
        Assert.DoesNotContain(expectedSubstring: "% of limit", actualString: error.ToString());
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(commit: 50), tracker: new HostStartTracker(), clock: new GateClock()));
        // The same 60% reading admits a headless World, whose peak is 6 GB.
        Assert.True(condition: Admits(kind: HostStartKind.HeadlessWorld, reading: Sample(commit: 60), tracker: new HostStartTracker(), clock: new GateClock()));
    }
    // A running admitted start counts for what it has left to grow: a World that reached its peak counts for nothing, and
    // one still at 6 GB holds 12 GB more of the headroom.
    [Fact]
    public void ARunningStartCountsOnlyForWhatItHasLeftToGrow() {
        HostRunningStart[] atPeak = [new(Id: 7, Kind: HostStartKind.WindowedWorld, PrivateGb: 18)];
        HostRunningStart[] growing = [new(Id: 7, Kind: HostStartKind.WindowedWorld, PrivateGb: 6)];

        // Commit 50% of 40 GB leaves 20 GB: enough for a windowed World beside one that stopped growing, not beside one still at 6 GB.
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(commit: 50, starts: atPeak), tracker: new HostStartTracker(), clock: new GateClock()));
        Assert.False(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(commit: 50, starts: growing), tracker: new HostStartTracker(), clock: new GateClock()));

        var demand = new HostStartTracker().Judge(reading: Sample(commit: 50, starts: growing), kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (30.0, 12.0, 1, false), actual: (demand.NeedGb, demand.GrowthGb, demand.Growing, demand.Covered));
        // A process that exited stops counting at once: the next reading no longer lists it.
        var tracker = new HostStartTracker();

        Assert.False(condition: tracker.Judge(reading: Sample(commit: 50, starts: growing), kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: new GateClock(), reserve: false).Covered);
        Assert.True(condition: tracker.Judge(reading: Sample(commit: 50), kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: new GateClock(), reserve: false).Covered);
    }
    // Reserved growth covers only the starts that have not reported ready: an idle headless World at 0.6 GB that reported
    // steady state counts its usage alone (which the charge already holds), so a windowed start that needs 18 GB of the
    // 22 GB headroom goes ahead beside it; the same World before it reported ready holds its 5.4 GB still to grow and
    // keeps the start out.
    [Fact]
    public void AStartThatReportedReadyReservesNoGrowth() {
        HostRunningStart[] booting = [new(Id: 9, Kind: HostStartKind.HeadlessWorld, PrivateGb: 0.6)];
        HostRunningStart[] ready = [new(Id: 9, Kind: HostStartKind.HeadlessWorld, PrivateGb: 0.6, Ready: true)];
        // Commit 45% of 40 GB leaves 22 GB.
        var unready = new HostStartTracker().Judge(reading: Sample(commit: 45, starts: booting), kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: new GateClock(), reserve: false);
        var settled = new HostStartTracker().Judge(reading: Sample(commit: 45, starts: ready), kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (5.4, 1, false), actual: (Math.Round(value: unready.GrowthGb, digits: 6), unready.Growing, unready.Covered));
        Assert.Equal(expected: (0.0, 0, true, 18.0), actual: (settled.GrowthGb, settled.Growing, settled.Covered, settled.NeedGb));
    }
    // A World reports steady state in its own words: a headless World once it answers on its console or publishes its
    // attachment, a World that opens a device once its renderer is ready or its bakes settle; boot narration is neither.
    [InlineData("[world.status: tick 3]", true, false, true)]
    [InlineData("[world.control: operator attachment C:/t/puck-control-1.json]", false, false, true)]
    [InlineData("[world.host] physical input devices are off (--devices none); this boot shape opens none either way.", false, false, false)]
    [InlineData("[world.status: tick 3]", true, true, false)]
    [InlineData("[engine: ready at tick 41]", false, true, true)]
    [InlineData("[bakes: settled at tick 90]", false, true, true)]
    [InlineData("[bakes: not settled after 600 seconds, so world.wait released: debug]", false, true, false)]
    [Theory]
    public void AWorldReportsReadyInItsOwnWords(string line, bool stdout, bool device, bool ready) =>
        Assert.Equal(expected: ready, actual: WorldReadiness.Signals(line: line, opensDevice: device, stdout: stdout));
    // Two starts asked in a row cannot take the same headroom: the first admitted start holds its peak until its process
    // shows up, so the second waits, then goes ahead when the process appears (counted at its live size) or the hold passes.
    [Fact]
    public void TwoStartsInARowCannotBothBeAdmittedIntoTheSameHeadroom() {
        var clock = new GateClock();
        var tracker = new HostStartTracker();
        var reading = Sample(commit: 40);

        // 24 GB of headroom holds one windowed World and not two.
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: reading, tracker: tracker, clock: clock));
        Assert.False(condition: Admits(kind: HostStartKind.WindowedWorld, reading: reading, tracker: tracker, clock: clock));
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: reading, tracker: tracker, clock: clock, boundSeconds: 100));
        Assert.True(condition: (clock.GetElapsedTime(startingTimestamp: 0) >= HostStartTracker.ReservationHold));

        // The first start's process appears at 3 GB: its reservation ends and it counts for the 15 GB it has left, not both.
        var answered = new HostStartTracker();
        var asked = Sample(commit: 20, limit: 64);

        Assert.True(condition: answered.Judge(reading: asked, kind: HostStartKind.WindowedWorld, thresholds: Thresholds, clock: clock, reserve: true).Covered);
        Assert.Equal(expected: HostLoadThresholds.WindowedWorldPeakGb, actual: answered.Judge(reading: asked, kind: HostStartKind.HeadlessWorld, thresholds: Thresholds, clock: clock, reserve: false).GrowthGb);

        var appeared = answered.Judge(reading: Sample(commit: 20, limit: 64, starts: [new(Id: 9, Kind: HostStartKind.WindowedWorld, PrivateGb: 3)]), kind: HostStartKind.HeadlessWorld, thresholds: Thresholds, clock: clock, reserve: false);

        Assert.Equal(expected: (15.0, 1), actual: (appeared.GrowthGb, appeared.Growing));
        // A start that nothing else holds back is the only one that reserves: one held by the GPU leaves no reservation.
        var held = new HostStartTracker();
        using var heldError = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("start", true, false, () => Sample(commit: 20, limit: 64, gpu: "Puck.World 9"), clock, clock.Advance, heldError, CancellationToken.None, Thresholds, TimeSpan.FromSeconds(seconds: 15), null, HostStartKind.WindowedWorld, held));
        Assert.Equal(expected: 0.0, actual: held.Judge(reading: asked, kind: HostStartKind.HeadlessWorld, thresholds: Thresholds, clock: clock, reserve: false).GrowthGb);
    }
    // A finished start teaches its real peak; a still-growing start has not supplied a completed measurement.
    [Fact]
    public void AFinishedStartsMeasuredPeakReplacesTheFallbackWithoutClippingRealPeaks() {
        var tracker = new HostStartTracker();
        var clock = new GateClock();

        Assert.Equal(expected: HostLoadThresholds.HeadlessWorldPeakGb, actual: tracker.ExpectedPeaks(kind: HostStartKind.HeadlessWorld).PrivateGb);
        _ = tracker.Judge(reading: Sample(starts: [new(Id: 4, Kind: HostStartKind.HeadlessWorld, PrivateGb: 9)]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        _ = tracker.Judge(reading: Sample(starts: [new(Id: 4, Kind: HostStartKind.HeadlessWorld, PrivateGb: 4)]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        Assert.Equal(expected: HostLoadThresholds.HeadlessWorldPeakGb, actual: tracker.ExpectedPeaks(kind: HostStartKind.HeadlessWorld).PrivateGb);
        clock.Advance(HostStartTracker.Plateau);
        _ = tracker.Judge(reading: Sample(), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        Assert.Equal(expected: 9.0, actual: tracker.ExpectedPeaks(kind: HostStartKind.HeadlessWorld).PrivateGb);
        Assert.Equal(expected: HostLoadThresholds.WindowedWorldPeakGb, actual: tracker.ExpectedPeaks(kind: HostStartKind.WindowedWorld).PrivateGb);
        _ = tracker.Judge(reading: Sample(starts: [new(Id: 5, Kind: HostStartKind.HeadlessWorld, PrivateGb: 30)]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        _ = tracker.Judge(reading: Sample(), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        Assert.Equal(expected: 30.0, actual: tracker.ExpectedPeaks(kind: HostStartKind.HeadlessWorld).PrivateGb);
    }
    // A process is its id and start ticks: a headless World that reuses a finished heavy suite's id is a new start, so the
    // suite's 15 GB peak is measured for heavy suites when it exits and never carried into the headless kind.
    [Fact]
    public void AReusedProcessIdIsANewStartAndCarriesNoPeakFromTheOneBefore() {
        var tracker = new HostStartTracker();
        var clock = new GateClock();

        _ = tracker.Judge(reading: Sample(starts: [new(Id: 7, Kind: HostStartKind.HeavySuite, PrivateGb: 15, StartTicks: 1)]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        clock.Advance(HostStartTracker.Plateau);
        _ = tracker.Judge(reading: Sample(starts: [new(Id: 7, Kind: HostStartKind.HeadlessWorld, PrivateGb: 0.5, StartTicks: 2)]), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        _ = tracker.Judge(reading: Sample(), kind: HostStartKind.Step, thresholds: Thresholds, clock: clock, reserve: false);
        Assert.Equal(expected: (15.0, HostLoadThresholds.HeadlessWorldPeakGb), actual: (tracker.ExpectedPeaks(kind: HostStartKind.HeavySuite).PrivateGb, tracker.ExpectedPeaks(kind: HostStartKind.HeadlessWorld).PrivateGb));
    }

    [Fact]
    public void APlateauReleasesGrowthAndRenewedGrowthRestoresIt() {
        var clock = new GateClock();
        var tracker = new HostStartTracker();
        var sample = Sample(commit: 50, limit: 45.7, starts: [new(7, HostStartKind.ReproduceReplica, 3, StartTicks: 1)]);
        HostStartDemand Read() => tracker.Judge(sample, HostStartKind.WindowedWorld, Thresholds, clock, false);
        var first = Read();
        Assert.False(first.Covered); // Old demand: 18 + (9 - 3) = 24, against 22.85 GB.
        clock.Advance(HostStartTracker.Plateau / 2);
        Assert.Equal(3, Read().GrowthGb);
        clock.Advance(HostStartTracker.Plateau / 2);
        var plateau = Read();
        Assert.True(plateau.Covered);
        Assert.Equal(0, plateau.GrowthGb);
        // A process that releases and then allocates again must get its growth reserve back.
        sample = sample with { Starts = [new(7, HostStartKind.ReproduceReplica, 1, StartTicks: 1)] };
        Read();
        sample = sample with { Starts = [new(7, HostStartKind.ReproduceReplica, 2, StartTicks: 1)] };
        Assert.Equal(1, Read().GrowthGb);
    }

    [Fact]
    public void MeasuredPeaksSurviveANewCliAndAFewerLowerObservationsCannotEraseAHigherOne() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();
        var id = 7;
        void Measure(double peak) {
            var tracker = new HostStartTracker(directory.RootPath);
            tracker.Judge(Sample(starts: [new(++id, HostStartKind.WindowedWorld, peak, StartTicks: id)]), HostStartKind.Step, Thresholds, clock, false);
            clock.Advance(HostStartTracker.Plateau);
            tracker.Judge(Sample(), HostStartKind.Step, Thresholds, clock, false);
        }
        Measure(13);
        var next = new HostStartTracker(directory.RootPath);
        var demand = next.Judge(Sample(commit: 50), HostStartKind.WindowedWorld, Thresholds, clock, false);
        Assert.Equal(13, demand.OwnGb);
        Assert.True(demand.Covered);
        Measure(11);
        next.Judge(Sample(), HostStartKind.Step, Thresholds, clock, false);
        Assert.Equal(13, next.ExpectedPeaks(HostStartKind.WindowedWorld).PrivateGb);
        var live = new HostStartTracker(directory.RootPath).Judge(
            Sample(starts: [new(9, HostStartKind.WindowedWorld, 18)]), HostStartKind.WindowedWorld, Thresholds, clock, false);
        Assert.Equal(18, live.OwnGb);
    }

    [Fact]
    public void ADeadlinePrintsItsLastBlockerEvenWhenTheHeartbeatHasNotElapsed() {
        var clock = new GateClock();
        using var error = new StringWriter();
        var reads = 0;
        Assert.False(HostAdmission.Wait("start", true, false, () => Sample(gpu: ++reads == 1 ? "World 1" : "World 2"),
            clock, clock.Advance, error, CancellationToken.None, Thresholds, TimeSpan.FromSeconds(1)));
        Assert.Contains("refused start after 1 seconds: the GPU is held by World 2", error.ToString());
    }
    // An observed peak is never clipped to a fraction of the host limit; an unknown commit reading is not judged.
    [Fact]
    public void ARealPeakIsNeverClippedAndAnUnreadableCommitIsNotJudged() {
        var tracker = new HostStartTracker();
        var clock = new GateClock();
        tracker.Judge(Sample(starts: [new(42, HostStartKind.WindowedWorld, 30)]), HostStartKind.Step, Thresholds, clock, false);
        clock.Advance(HostStartTracker.Plateau);
        var measured = tracker.Judge(Sample(commit: 20, limit: 32, starts: [Company]), HostStartKind.WindowedWorld, Thresholds, clock, false);
        Assert.Equal(30, measured.OwnGb);
        Assert.False(measured.Covered);
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(commit: (400.0 / 24.0), limit: 24), tracker: new HostStartTracker(), clock: new GateClock()));
        Assert.True(condition: Admits(kind: HostStartKind.WindowedWorld, reading: Sample(starts: [new(Id: 1, Kind: HostStartKind.WindowedWorld, PrivateGb: 1)]), tracker: new HostStartTracker(), clock: new GateClock()));
    }
    [InlineData("Puck.World.exe", "Puck.World.exe --devices none --world worlds/debug", HostStartKind.WindowedWorld)]
    [InlineData("dotnet", "dotnet D:\\store\\Puck.World.dll --devices none --headless true", HostStartKind.HeadlessWorld)]
    [InlineData("dotnet", "dotnet D:\\store\\Puck.World.dll --devices none --test-batch b.json", HostStartKind.ReproduceReplica)]
    [InlineData("Puck.World.Tests", "", HostStartKind.HeavySuite)]
    [InlineData("dotnet", "dotnet exec D:\\x\\Puck.World.Presentation.Tests.dll", HostStartKind.HeavySuite)]
    [InlineData("Puck.World", "", HostStartKind.WindowedWorld)]
    [Theory]
    public void TheHostTracksWorldsAndHeavySuitesAsAdmittedStarts(string name, string commandLine, HostStartKind kind) =>
        Assert.Equal(expected: kind, actual: HostProcesses.RunningStart(name: name, commandLine: commandLine));
    [InlineData("pwsh", "pwsh -c Puck.World.dll")]
    [InlineData("dotnet", "dotnet build src/Puck.World/Puck.World.csproj -c Release")]
    [InlineData("MSBuild", "MSBuild.dll /nodemode:1")]
    [InlineData("dotnet", "dotnet D:\\x\\Puck.Cli.dll lease heavy")]
    [Theory]
    public void ShellsBuildsAndOtherProcessesAreNotTrackedStarts(string name, string commandLine) =>
        Assert.Null(@object: HostProcesses.RunningStart(name: name, commandLine: commandLine));
    // A windowed World is judged by its own profile: its mesh store (a cold boot bakes every mesh and peaks far higher) and
    // its backend are measured in windows of their own, so the cold bakes never set what a warm session must find free.
    [Fact]
    public void AColdStoreAndEachBackendAreMeasuredInWindowsOfTheirOwn() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();
        var windowed = new HostStartProfile(HostStartKind.WindowedWorld);

        Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: 1, privateGb: 12, workingSetGb: 9, privateResidentGb: 8, marked: windowed);
        Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: 2, privateGb: 22, workingSetGb: 17, privateResidentGb: 15, marked: windowed.Cold);
        Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: 3, privateGb: 14, workingSetGb: 10, privateResidentGb: 9, marked: (windowed with { Device = "vulkan" }));
        // An unmarked World is judged by its kind alone.
        Observed(directory.RootPath, clock, HostStartKind.WindowedWorld, id: 4, privateGb: 12, workingSetGb: 9, privateResidentGb: 8);

        var next = new HostStartTracker(directory.RootPath);

        Assert.Equal(expected: 12.0, actual: next.ExpectedPeaks(kind: windowed).PrivateGb);
        Assert.Equal(expected: 22.0, actual: next.ExpectedPeaks(kind: windowed.Cold).PrivateGb);
        Assert.Equal(expected: 14.0, actual: next.ExpectedPeaks(kind: (windowed with { Device = "vulkan" })).PrivateGb);
        Assert.Equal(expected: ["WindowedWorld", "WindowedWorld.cold", "WindowedWorld.vulkan"], actual: Directory.EnumerateFiles(Path.Combine(directory.RootPath, HostStartTracker.ObservationDirectory)).Where(static file => !file.EndsWith(".guard")).Select(static file => Path.GetFileName(file)).Order().ToArray());
        // Neither dimension means anything to a heavy suite, so a mark that names them measures the same window.
        Assert.Equal(expected: next.ExpectedPeaks(kind: HostStartKind.HeavySuite), actual: next.ExpectedPeaks(kind: new HostStartProfile(HostStartKind.HeavySuite, "vulkan", StoreWarmth.Cold)));
    }
    // With a warm window of 12 GB and a cold one of 22 GB measured, 14 GB of free memory admits a warm start and not a cold
    // one: the cold bakes no longer set what every windowed start must find free.
    [Fact]
    public void AWarmStartIsAdmittedWhereAColdStartIsNot() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var peaks = Path.Combine(directory.RootPath, HostStartTracker.ObservationDirectory);

        Directory.CreateDirectory(peaks);
        File.WriteAllText(Path.Combine(peaks, "WindowedWorld"), "1 1 12 8 9\n");
        File.WriteAllText(Path.Combine(peaks, "WindowedWorld.cold"), "1 1 22 15 16\n");

        var reading = Sample(commit: 30, limit: 64, ram: 14);
        var warm = new HostStartTracker(directory.RootPath).Judge(reading: reading, kind: HostStartKind.WindowedWorld, thresholds: Desk, clock: new GateClock(), reserve: false);
        var cold = new HostStartTracker(directory.RootPath).Judge(reading: reading, kind: new HostStartProfile(HostStartKind.WindowedWorld).Cold, thresholds: Desk, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (true, true, 8.0, "WindowedWorld"), actual: (warm.CommitCovered, warm.PhysicalCovered, warm.ResidentOwnGb, warm.Profile!.Value.Name));
        // The cold 15 GB does not fit the 10 GB free memory leaves above the floor even with nothing else running, so it is
        // judged by the lesser of it and the desk's cold fallback (0.7 of 20 GB), which does not fit either.
        Assert.Equal(expected: (true, false, 14.0, "WindowedWorld.cold"), actual: (cold.CommitCovered, cold.PhysicalCovered, Math.Round(cold.ResidentOwnGb, 6), cold.Profile!.Value.Name));
    }
    // Before a profile has an observation, a cold windowed World is expected to peak at the cold bake's figure, and a running
    // start still growing is expected to reach the peak of the profile its launch marked.
    [Fact]
    public void AnUnmeasuredColdStartAndAMarkedRunningStartExpectTheColdPeak() {
        var tracker = new HostStartTracker();
        var cold = new HostStartProfile(HostStartKind.WindowedWorld).Cold;
        var growing = new HostRunningStart(Id: 11, Kind: HostStartKind.WindowedWorld, PrivateGb: 2, StartTicks: 1);
        var large = HostLoadThresholds.For(installedRamGb: 64, logicalProcessors: 16);

        Assert.Equal(expected: HostLoadThresholds.ColdWindowedWorldPeakGb, actual: tracker.ExpectedPeaks(kind: cold, thresholds: large).PrivateGb);
        Assert.Equal(expected: HostLoadThresholds.WindowedWorldPeakGb, actual: tracker.ExpectedPeaks(kind: HostStartKind.WindowedWorld, thresholds: large).PrivateGb);

        var unmarked = new HostStartTracker().Judge(reading: Sample(starts: [growing]), kind: HostStartKind.Step, thresholds: large, clock: new GateClock(), reserve: false);
        var marked = new HostStartTracker().Judge(reading: Sample(starts: [growing with { Marked = cold }]), kind: HostStartKind.Step, thresholds: large, clock: new GateClock(), reserve: false);

        Assert.Equal(expected: (HostLoadThresholds.WindowedWorldPeakGb - 2, HostLoadThresholds.ColdWindowedWorldPeakGb - 2), actual: (unmarked.GrowthGb, marked.GrowthGb));
    }
    // A device start shares the GPU with a working holder while the dedicated video memory in use is under 95% of the
    // device's (11.4 GB of a 12 GB device). It waits for the holder once the device is full, or when the device cannot be
    // measured, and a lone start never waits.
    [InlineData(12.0, 4.0, true)]
    [InlineData(12.0, 9.9, true)]
    [InlineData(12.0, 11.3, true)]
    [InlineData(12.0, 11.4, false)]
    [InlineData(12.0, 14.0, false)]
    [InlineData(double.NaN, double.NaN, false)]
    [Theory]
    public void ADeviceStartSharesTheGpuWhileItsMemoryHasHeadroom(double total, double used, bool admitted) {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.Equal(expected: admitted, actual: HostAdmission.Wait("start", true, false, () => Sample(gpu: "Puck.World 9", deviceTotal: total, deviceUsed: used), clock, clock.Advance, error, CancellationToken.None, Thresholds, TimeSpan.FromSeconds(seconds: 25), null, HostStartKind.Step, new HostStartTracker()));
        if (!admitted) { Assert.Contains(expectedSubstring: "the GPU is held by Puck.World 9", actualString: error.ToString()); }
        if (!admitted && double.IsFinite(total)) { Assert.Contains(expectedSubstring: $"with {used:0.0}GB of {total:0.0}GB device memory in use", actualString: error.ToString()); }
        // Nothing else working on the device: a full GPU never holds the lone start back.
        Assert.True(HostAdmission.Wait("start", true, false, () => Sample(deviceTotal: 12, deviceUsed: 11.9), clock, clock.Advance, error, CancellationToken.None, Thresholds, TimeSpan.FromSeconds(seconds: 25), null, HostStartKind.Step, new HostStartTracker()));
        // A step that opens no device never waits on the GPU.
        Assert.True(HostAdmission.Wait("build", false, false, () => Sample(gpu: "Puck.World 9", deviceTotal: 12, deviceUsed: 11.9), clock, clock.Advance, error, CancellationToken.None, Thresholds, TimeSpan.FromSeconds(seconds: 25), null, HostStartKind.Step, new HostStartTracker()));
    }
    // The counter's instances are one per process and adapter: the instances that share an adapter's luid add up, and the
    // busiest adapter's total is the memory in use.
    [Fact]
    public void TheDeviceMemoryInUseIsTheBusiestAdaptersInstancesAddedUp() {
        const long Gb = 1073741824;

        Assert.Equal(expected: 4.0, actual: HostGpuMeter.Used([
            ("pid_100_luid_0x00000000_0x0000AAAA_phys_0", (3 * Gb)),
            ("pid_200_luid_0x00000000_0x0000AAAA_phys_0", (1 * Gb)),
            ("pid_100_luid_0x00000000_0x0000BBBB_phys_0", (2 * Gb)),
            ("pid_300_luid_0x00000000_0x0000BBBB_phys_0", (1 * Gb)),
        ]));
        Assert.Equal(expected: 0.0, actual: HostGpuMeter.Used([]));
    }
    // A hold whose tree uses no CPU for a plateau is idle, and the tracker marks it for the whole machine (the probe then
    // leaves its device out), as it marks an idle start; a hold that is working is not marked.
    [Fact]
    public void AGpuHolderThatSitsIdleIsMarkedIdleForTheMachine() {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var clock = new GateClock();
        var tracker = new HostStartTracker(directory.RootPath);
        var mark = Path.Combine(directory.RootPath, "start-idle", "41-41");

        HostLeaseHold Hold(double cpuSeconds) => (HeavyHold(privateGb: 6, cpuSeconds: cpuSeconds, kind: HostStartKind.WindowedWorld) with { Device = true, Lease = "gpu" });

        _ = tracker.Judge(reading: Sample(holds: [Hold(cpuSeconds: 10)]), kind: HostStartKind.Step, thresholds: Desk, clock: clock, reserve: false);
        clock.Advance(HostStartTracker.Plateau);
        _ = tracker.Judge(reading: Sample(holds: [Hold(cpuSeconds: 40)]), kind: HostStartKind.Step, thresholds: Desk, clock: clock, reserve: false);
        Assert.False(File.Exists(mark));

        clock.Advance(HostStartTracker.Plateau);
        _ = tracker.Judge(reading: Sample(holds: [Hold(cpuSeconds: 40)]), kind: HostStartKind.Step, thresholds: Desk, clock: clock, reserve: false);
        Assert.True(File.Exists(mark));
    }
    [InlineData(WorldShape.Windowed)]
    [InlineData(WorldShape.Document)]
    [Theory]
    public void ALaunchPredictsItsProfileFromItsBackendAndItsMeshStore(WorldShape shape) {
        using var directory = new Puck.Testing.TemporaryDirectory();
        var request = new WorldLaunchRequest(Artifact: "Puck.World.dll", Step: "law") { Arguments = ["--cache-dir", directory.RootPath], Backend = Puck.World.WorldBackendPreference.Vulkan, Shape = shape };

        // An empty store bakes every mesh; one that holds a mesh loads them.
        Assert.Equal(new HostStartProfile(HostStartKind.WindowedWorld, "vulkan", StoreWarmth.Cold), request.Profile);
        Directory.CreateDirectory(directory.PathOf("meshes"));
        File.WriteAllText(directory.PathOf("meshes/a.mesh"), "mesh");
        Assert.Equal(new HostStartProfile(HostStartKind.WindowedWorld, "vulkan"), request.Profile);
        Assert.Equal(new HostStartProfile(HostStartKind.WindowedWorld), (request with { Backend = null }).Profile);
        Assert.Equal(new HostStartProfile(HostStartKind.HeadlessWorld), (request with { Shape = WorldShape.Headless }).Profile);

        // The document is the world path's last name up to its first dot, wherever the world lies; a staged copy is measured
        // as the world it stands for, and a headless World measures no document.
        foreach (var world in (string[])["worlds/debug", "C:/install/versions/1/worlds/debug/debug.world.json", "worlds/debug/debug.puck", "worlds/debug/"]) {
            Assert.Equal(new HostStartProfile(HostStartKind.WindowedWorld, "vulkan", Document: "debug"), (request with { World = world }).Profile.Class);
        }
        Assert.Equal("gallery", (request with { World = "worlds/debug/Gallery.puck" }).Profile.Class.Document);
        Assert.Null((request with { World = "worlds/debug/.benchmark-pair-0f.puck" }).Profile.Document);
        Assert.Equal("debug", (request with { World = "worlds/debug/.benchmark-pair-0f.puck", AdmissionWorld = "worlds/debug" }).Profile.Document);
        Assert.Null((request with { World = "worlds/debug", Shape = WorldShape.Headless }).Profile.Document);
    }
    [Fact]
    public void PersistentPressureStopsAtTheBoundAndCancellationStartsNothing() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("build", false, false, () => Sample(ram: 0), clock, clock.Advance, error, CancellationToken.None, Thresholds));
        Assert.Equal(HostAdmission.Timeout, clock.GetElapsedTime(startingTimestamp: 0));
        Assert.DoesNotContain("capacity returned", error.ToString());
        Assert.Throws<OperationCanceledException>(testCode: () => HostAdmission.Wait("build", false, false, () => throw new Exception(message: "must not sample"), clock, clock.Advance, error, new CancellationToken(canceled: true), Thresholds));
    }
}
