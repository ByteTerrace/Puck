using System.CommandLine;
using Puck.Cli.Host;
using Xunit;

namespace Puck.Cli.Gate.Tests;

public sealed class HostAdmissionLawTests {
    // The 16 GB, six-thread developer machine's thresholds without a commit reserve, so the laws about a start's own need
    // read the commit headroom against that need alone: pressure below 2 GB free, disk pressure below 10 GB. Its installed
    // memory reads 24 GB, whose account budget (18 GB) holds every kind's full default peak, so these laws read the kinds'
    // own figures; the 16 GB machine's own peaks are AWindowedWorldOnASixteenGigabyteMachineExpectsItsAccountBudget's.
    private static readonly HostLoadThresholds Thresholds = (HostLoadThresholds.For(installedRamGb: 16, logicalProcessors: 6) with { CommitReservePercent = 0, InstalledRamGb = 24 });
    // The same thresholds with their default commit reserve (10% of the limit).
    private static readonly HostLoadThresholds Reserved = (HostLoadThresholds.For(installedRamGb: 16, logicalProcessors: 6) with { InstalledRamGb = 24 });
    // The 32 GB, 16-thread machine with its defaults: pressure below 4 GB free, a tenth of the commit limit in reserve.
    private static readonly HostLoadThresholds Desk = HostLoadThresholds.For(installedRamGb: 32, logicalProcessors: 16);

    // A commit reading is a percentage of a 40 GB commit limit; none is a platform that reports no limit. Free memory
    // defaults to plenty, so a law about commit is not also a law about physical memory.
    private static HostSample Sample(double cpu = 0, double ram = 64, double disk = 100, string? gpu = null, string? heavy = null, double? commit = null, double limit = 40, HostRunningStart[]? starts = null) => new(At: DateTimeOffset.UnixEpoch, CpuPercent: cpu, FreeDiskGb: disk, FreeRamGb: ram, GpuHolder: gpu, HeavyTestHolder: heavy, ReuseNodes: 42, CommitChargeGb: ((commit is { } percent) ? (percent * limit / 100.0) : double.NaN), CommitLimitGb: ((commit is null) ? double.NaN : limit), Starts: starts);
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
        (string Name, double? Value)[] thresholds = [("--capacity-cpu", defaults.CapacityCpuPercent), ("--commit-reserve", defaults.CommitReservePercent),("--pressure-ram", defaults.PressureRamGb), ("--pressure-disk", defaults.PressureDiskGb)];

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
    private static void Observed(string root, GateClock clock, HostStartKind kind, int id, double privateGb, double workingSetGb, double privateResidentGb = double.NaN) {
        var tracker = new HostStartTracker(root);

        _ = tracker.Judge(reading: Sample(starts: [new(Id: id, Kind: kind, PrivateGb: privateGb, PrivateResidentGb: privateResidentGb, StartTicks: id, WorkingSetGb: workingSetGb)]), kind: HostStartKind.Step, thresholds: Desk, clock: clock, reserve: false);
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
    // The operating system answers a process's private working set, for this process and for another one the same user runs,
    // and it never exceeds the process's whole working set.
    [Fact]
    public void AProcessPrivateWorkingSetIsReadAndNeverExceedsItsWorkingSet() {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) { return; }

        var parent = ProcessInspection.ParentId(processId: Environment.ProcessId);

        foreach (var id in ((parent > 0) ? new[] { Environment.ProcessId, parent } : [Environment.ProcessId])) {
            using var process = System.Diagnostics.Process.GetProcessById(processId: id);
            var before = ProcessInspection.PrivateResidentBytes(processId: id);

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
    // A kind the idle machine could not hold is refused by name at once, however long the bound: its commit plus the reserve
    // exceed the commit limit.
    [Fact]
    public void AStartThatCanNeverFitTheMachineIsRefusedByNameAtOnce() {
        var smallCommit = HostLoadThresholds.For(installedRamGb: 32, logicalProcessors: 16);
        var cases = new (HostLoadThresholds Thresholds, HostSample Reading, string Expected)[] {
            (smallCommit, Sample(commit: 10, limit: 19), "a windowed World can never be admitted on this machine, it expects 18.0GB of commit and the commit reserve is 1.9GB, over the machine's 19.0GB commit limit"),
        };

        foreach (var (thresholds, reading, expected) in cases) {
            var clock = new GateClock();
            using var error = new StringWriter();

            Assert.False(condition: HostAdmission.Wait("start", false, false, () => reading, clock, _ => Assert.Fail(message: "an impossible start does not wait"), error, CancellationToken.None, thresholds, TimeSpan.FromMinutes(minutes: 30), null, HostStartKind.WindowedWorld, new HostStartTracker()));
            Assert.Contains(expectedSubstring: expected, actualString: error.ToString());
            Assert.Equal(expected: TimeSpan.Zero, actual: clock.GetElapsedTime(startingTimestamp: 0));
        }
    }
    // An unmeasured windowed World on a 16 GB machine is expected at its account budget there (12 GB, three quarters of the
    // machine), not at a large machine's 18 GB, so an idle 16 GB machine with a 19 GB commit limit admits it.
    [Fact]
    public void AWindowedWorldOnASixteenGigabyteMachineExpectsItsAccountBudget() {
        var machine = HostLoadThresholds.For(installedRamGb: 16, logicalProcessors: 6);
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.Equal(expected: 12.0, actual: HostLoadThresholds.DefaultPeakGb(installedRamGb: 16, kind: HostStartKind.WindowedWorld));
        Assert.Equal(expected: HostLoadThresholds.WindowedWorldPeakGb, actual: HostLoadThresholds.DefaultPeakGb(installedRamGb: 64, kind: HostStartKind.WindowedWorld));
        Assert.Equal(expected: 12.0, actual: new HostStartTracker().ExpectedPeaks(kind: HostStartKind.WindowedWorld, thresholds: machine).PrivateGb);
        Assert.True(condition: HostAdmission.Wait("start", false, false, () => Sample(commit: 10, limit: 19, ram: 13), clock, _ => Assert.Fail(message: "an idle machine admits at once"), error, CancellationToken.None, machine, TimeSpan.FromMinutes(minutes: 30), null, HostStartKind.WindowedWorld, new HostStartTracker()));
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
    public void AHeavySuiteWaitsOnAnotherHeavyRunPastTheOrdinaryBoundNamingEachHolderWithHeartbeats() {
        var clock = new GateClock();
        using var error = new StringWriter();

        // Another gate's Puck.World.Tests holds the machine for 35 minutes, a third one's for 15 more, then none.
        HostSample Reading() => clock.GetElapsedTime(startingTimestamp: 0).TotalMinutes switch {
            < 35 => Sample(heavy: "Puck.World.Tests 11"),
            < 50 => Sample(heavy: "Puck.World.Tests 12"),
            _ => Sample(),
        };

        Assert.True(condition: HostAdmission.Wait("Puck.World.Tests", false, true, Reading, clock, clock.Advance, error, CancellationToken.None, Thresholds));
        var lines = error.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').Select(selector: static line => line.TrimEnd()).ToArray();

        Assert.Equal(actual: lines, expected: [
            "gate: waiting for host capacity before Puck.World.Tests (at most 120 minutes): a heavy test run is held by Puck.World.Tests 11.",
            "gate: still waiting before Puck.World.Tests after 10 minutes: a heavy test run is held by Puck.World.Tests 11.",
            "gate: still waiting before Puck.World.Tests after 20 minutes: a heavy test run is held by Puck.World.Tests 11.",
            "gate: still waiting before Puck.World.Tests after 30 minutes: a heavy test run is held by Puck.World.Tests 11.",
            "gate: waiting for host capacity before Puck.World.Tests (at most 120 minutes): a heavy test run is held by Puck.World.Tests 12.",
            "gate: still waiting before Puck.World.Tests after 45 minutes: a heavy test run is held by Puck.World.Tests 12.",
            "gate: capacity returned for Puck.World.Tests.",
        ]);
        Assert.Equal(expected: TimeSpan.FromMinutes(minutes: 50), actual: clock.GetElapsedTime(startingTimestamp: 0));

        // A step that runs no heavy suite never waits on one.
        using var light = new StringWriter();

        Assert.True(condition: HostAdmission.Wait("baselines state", false, false, () => Sample(heavy: "Puck.World.Tests 11"), clock, _ => Assert.Fail(message: "a light step must not wait on a heavy run"), light, CancellationToken.None, Thresholds));
        Assert.Equal(expected: "", actual: light.ToString());
    }
    [Fact]
    public void AHeavySuiteIsRefusedOnlyAtItsTwoHourBound() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("Puck.World.Tests", false, true, () => Sample(heavy: "Puck.World.Tests 11"), clock, clock.Advance, error, CancellationToken.None, Thresholds));
        Assert.Equal(expected: TimeSpan.FromHours(hours: 2), actual: clock.GetElapsedTime(startingTimestamp: 0));
        Assert.Equal(expected: CliLease.DefaultWait, actual: TimeSpan.FromHours(hours: 2));
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
            asked.Add(item: (step, device, heavySuite, kind));

            return Task.FromResult(result: HostAdmission.Wait(step, device, heavySuite, () => ((readings++ < 2) ? Sample(commit: 98, limit: 64) : Sample(commit: 60, limit: 64)), clock, clock.Advance, error, cancellationToken, Thresholds, bound, stage, kind, reserve: reserve));
        };

        WorldAdmission.Install(admit: admit);
        try {
            var worker = new WorldLaunchRequest(Artifact: "Puck.World.dll", Step: "test worker") { Arguments = ["--test-batch", "batch.json"], Checkout = checkout.Root, HeavySuite = true, Shape = WorldShape.TestBatch };

            // The first replica waits two samples at 90% and starts on the third.
            WorldLaunch.Admit(cancellationToken: TestContext.Current.CancellationToken, request: worker).Dispose();
            Assert.Equal(expected: 4, actual: readings);
            Assert.Equal(expected: TimeSpan.FromSeconds(seconds: 20), actual: clock.GetElapsedTime(startingTimestamp: 0));
            Assert.Contains(actualString: error.ToString(), expectedSubstring: "no memory headroom: commit headroom 1.3GB (admission needs at least 9.0GB");
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

            // Another lane's start, short of headroom: it waits the shortage out, then queues behind the lane that holds.
            tight = true;

            var queued = WorldLaunch.AdmitAsync((lane with { Holder = "b", Lane = "b" }), token);

            await shortReadings.WaitAsync(token);
            Assert.False(queued.IsCompleted);
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
    // the 16 GB of headroom cannot hold it: the start waits, names the peak, and goes ahead once 20 GB are free.
    [Fact]
    public void AWindowedStartWaitsWhenTheHeadroomIsBelowItsPeakEvenAtSixtyPercentCommit() {
        var clock = new GateClock();
        using var error = new StringWriter();

        Assert.False(condition: HostAdmission.Wait("puck_world_start", true, false, () => Sample(commit: 60), clock, clock.Advance, error, CancellationToken.None, Thresholds, TimeSpan.FromSeconds(seconds: 25), null, HostStartKind.WindowedWorld, new HostStartTracker()));
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
