using System.Globalization;

using Puck.Cli.Gate;

namespace Puck.Cli.Host;

/// <summary>Bounded admission by the machine's host-load thresholds (<see cref="HostLoadThresholds.For"/>), the one
/// admission every heavy start passes: the gate and <c>affected</c> for their steps, <c>puck lease heavy</c> and
/// <c>puck lease gpu</c> for their commands, and the MCP adapter for a World it starts. Memory and disk decide whether a
/// step can run, since running out of either is what fails a build or a heavy suite: a step waits for free memory over
/// the capacity threshold, a commit charge under its share of the commit limit (allocation fails at the limit with
/// physical memory still free) and free disk over the pressure threshold. A start is also admitted by its expected peak
/// (<see cref="HostStartTracker"/>): the commit headroom must cover the start's own peak
/// (<see cref="HostLoadThresholds.DefaultPeakGb"/> by <see cref="HostStartKind"/>) plus what the admitted starts still
/// running have left to grow, since admitted Worlds grow into each other. A busy CPU only slows a step, so the CPU is
/// advisory: a step admitted over the CPU threshold runs, and the admission prints the load it ran under. A step that
/// opens a device also waits for an idle GPU; any other step never waits on a GPU holder. A heavy suite
/// (<see cref="HostProcesses.IsHeavyTestAssembly"/>) also waits while another process runs one, machine-wide, since two
/// at once exhaust memory; the waiting process's own runs never hold it back. A wait is the host's timed wait on the
/// caller's token, never a spin.</summary>
public static class HostAdmission {
    /// <summary>How long a step waits before it is refused.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(minutes: 30);
    /// <summary>How often a wait samples the machine.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(seconds: 10);
    /// <summary>How long a wait goes without printing before it prints that it is still waiting.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(minutes: 10);

    private static string KindName(HostStartKind kind) => kind switch {
        HostStartKind.WindowedWorld => "windowed World",
        HostStartKind.HeadlessWorld => "headless World",
        HostStartKind.HeavySuite => "heavy suite",
        HostStartKind.ReproduceReplica => "test-batch replica",
        _ => "step",
    };
    // What holds a reading back from memory headroom, each as its reading and what admission needs of it.
    private static List<(string Reading, string Need)> Shortfalls(HostSample reading, HostLoadThresholds thresholds, HostStartDemand? demand) {
        List<(string Reading, string Need)> shortfalls = [];

        if (!thresholds.CommitHasHeadroom(reading: reading)) {
            shortfalls.Add(item: (string.Create(provider: CultureInfo.InvariantCulture, handler: $"commit {reading.CommitPercent:0}% of limit"), string.Create(provider: CultureInfo.InvariantCulture, handler: $"under {thresholds.CapacityCommitPercent:0}%")));
        }
        if (demand is { Covered: false } uncovered) {
            shortfalls.Add(item: (
                string.Create(provider: CultureInfo.InvariantCulture, handler: $"commit headroom {uncovered.HeadroomGb:0.0}GB"),
                string.Create(provider: CultureInfo.InvariantCulture, handler: $"at least {uncovered.NeedGb:0.0}GB ({uncovered.OwnGb:0.0}GB expected peak of a {KindName(kind: uncovered.Kind)} plus {uncovered.GrowthGb:0.0}GB still to grow in {uncovered.Growing} admitted start(s))")
            ));
        }
        if (!(reading.FreeRamGb > (thresholds.CapacityRamGb ?? 0))) {
            shortfalls.Add(item: (string.Create(provider: CultureInfo.InvariantCulture, handler: $"freeRAM={reading.FreeRamGb:0.0}GB"), string.Create(provider: CultureInfo.InvariantCulture, handler: $"over {thresholds.CapacityRamGb:0.0}GB")));
        }
        if (!(reading.FreeDiskGb >= (thresholds.PressureDiskGb ?? 0))) {
            shortfalls.Add(item: (string.Create(provider: CultureInfo.InvariantCulture, handler: $"freeDisk={reading.FreeDiskGb:0.0}GB"), string.Create(provider: CultureInfo.InvariantCulture, handler: $"at least {thresholds.PressureDiskGb:0.0}GB")));
        }

        return shortfalls;
    }
    // What holds the step back, named so a wait that lasts can be traced to a process or a threshold: the line a wait
    // prints, and the shorter phrase a caller shows as the stage it is in.
    private static (string Line, string Stage) Reason(string? gpu, string? heavy, HostSample reading, HostLoadThresholds thresholds, HostStartDemand demand) {
        if (gpu is not null) { return ($"the GPU is held by {gpu}", $"the GPU, held by {gpu}"); }
        if (heavy is not null) { return ($"a heavy test run is held by {heavy}", $"a heavy test run, held by {heavy}"); }

        var shortfalls = Shortfalls(reading: reading, thresholds: thresholds, demand: demand);

        return (
            $"no memory headroom: {string.Join(separator: "; ", values: shortfalls.Select(selector: static shortfall => $"{shortfall.Reading} (admission needs {shortfall.Need})"))}",
            $"memory headroom: {string.Join(separator: ", ", values: shortfalls.Select(selector: static shortfall => shortfall.Reading))}"
        );
    }
    private static string Span(TimeSpan span) => ((span >= TimeSpan.FromMinutes(minutes: 1))
        ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{span.TotalMinutes:0} minutes")
        : string.Create(provider: CultureInfo.InvariantCulture, handler: $"{span.TotalSeconds:0} seconds"));

    /// <summary>Waits until the host can take <paramref name="step"/>, at most <paramref name="bound"/>, or
    /// <see cref="Timeout"/> (<see cref="CliLease.DefaultWait"/> for a heavy suite) when it is <see langword="null"/>.
    /// CPU load never holds a step back.</summary>
    /// <param name="step">The step's name, for the wait lines.</param>
    /// <param name="device">Whether the step opens a GPU device, and so must also wait for the GPU to be idle.</param>
    /// <param name="heavySuite">Whether the step runs a heavy suite, and so must also wait while another process runs
    /// one.</param>
    /// <param name="sample">Takes one reading.</param>
    /// <param name="clock">The clock the bound is measured on.</param>
    /// <param name="delay">Waits one interval.</param>
    /// <param name="error">Receives the wait lines and the advisory CPU line.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <param name="thresholds">The thresholds a reading is judged by; a run on this machine passes
    /// <see cref="HostLoadThresholds.ThisMachine"/>.</param>
    /// <param name="bound">How long to wait, at most; <see langword="null"/> for the step's own bound.</param>
    /// <param name="stage">Receives what the wait is on, as a phrase such as <c>waiting for memory headroom: commit 82% of
    /// limit</c>, on every reading that holds the step back; <see langword="null"/> to ignore it.</param>
    /// <param name="kind">What the start is, which sets the commit it is expected to peak at; <see langword="null"/> for a
    /// heavy suite when <paramref name="heavySuite"/> is set and an ordinary step otherwise.</param>
    /// <param name="tracker">Reads what the starts already admitted still have to grow and reserves this start's peak when it
    /// is admitted; <see langword="null"/> for a tracker of this call alone.</param>
    /// <returns><see langword="true"/> once the host can take the step; <see langword="false"/> when the bound passed
    /// first.</returns>
    public static bool Wait(string step, bool device, bool heavySuite, Func<HostSample> sample, TimeProvider clock, Action<TimeSpan> delay, TextWriter error, CancellationToken cancellationToken, HostLoadThresholds thresholds, TimeSpan? bound = null, Action<string>? stage = null, HostStartKind? kind = null, HostStartTracker? tracker = null) {
        var started = clock.GetTimestamp();
        var limit = (bound ?? (heavySuite ? CliLease.DefaultWait : Timeout));
        var printed = started;
        var peak = (kind ?? (heavySuite ? HostStartKind.HeavySuite : HostStartKind.Step));
        var starts = (tracker ?? new HostStartTracker());
        string? reason = null;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var reading = sample();
            var gpu = (device ? reading.GpuHolder : null);
            var heavy = (heavySuite ? reading.HeavyTestHolder : null);
            // The peak is judged last: only a start that nothing else holds back reserves its headroom.
            var held = ((gpu is not null) || (heavy is not null) || Shortfalls(reading: reading, thresholds: thresholds, demand: null).Any());
            var demand = starts.Judge(clock: clock, kind: peak, reading: reading, reserve: !held, thresholds: thresholds);

            if (!held && demand.Covered) {
                if (reason is not null) { error.WriteLine(value: $"gate: capacity returned for {step}."); }
                if (reading.CpuPercent >= (thresholds.CapacityCpuPercent ?? 100)) {
                    error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"gate: {step} runs under cpu={reading.CpuPercent:0}% (over {thresholds.CapacityCpuPercent}%; CPU load is advisory and only slows the step)."));
                }
                return true;
            }
            var (now, phrase) = Reason(gpu: gpu, heavy: heavy, reading: reading, thresholds: thresholds, demand: demand);
            var waited = clock.GetElapsedTime(startingTimestamp: started);

            stage?.Invoke(obj: $"waiting for {phrase}");

            // A holder is named again whenever it changes; a memory reading prints once per wait, not per sample. A wait
            // that has printed nothing for a heartbeat says it is still waiting, and on what.
            if ((reason is null) || (((gpu ?? heavy) is not null) && (now != reason))) {
                error.WriteLine(value: $"gate: waiting for host capacity before {step} (at most {Span(span: limit)}): {now}.");
                error.Flush();
                reason = now;
                printed = clock.GetTimestamp();
            } else if (clock.GetElapsedTime(startingTimestamp: printed) >= Heartbeat) {
                error.WriteLine(value: $"gate: still waiting before {step} after {waited.TotalMinutes:0} minutes: {now}.");
                error.Flush();
                printed = clock.GetTimestamp();
            }
            var remaining = (limit - waited);

            if (remaining <= TimeSpan.Zero) { return false; }
            delay(((remaining < Interval) ? remaining : Interval));
        }
    }
    /// <summary>Waits as <see cref="Wait(string, bool, bool, Func{HostSample}, TimeProvider, Action{TimeSpan}, TextWriter, CancellationToken, HostLoadThresholds, TimeSpan?, Action{string}?, HostStartKind?, HostStartTracker?)"/>
    /// does, on this machine's thresholds and a reading from <paramref name="probe"/>, sleeping on the host's timer between
    /// readings. It blocks the calling thread.</summary>
    /// <param name="step">The step's name, for the wait lines.</param>
    /// <param name="device">Whether the step opens a GPU device.</param>
    /// <param name="heavySuite">Whether the step runs a heavy suite.</param>
    /// <param name="probe">Reads the machine.</param>
    /// <param name="clock">The clock the bound and the sleeps are measured on.</param>
    /// <param name="error">Receives the wait lines.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <param name="bound">How long to wait, at most; <see langword="null"/> for the step's own bound.</param>
    /// <param name="stage">Receives what the wait is on, or <see langword="null"/>.</param>
    /// <param name="kind">What the start is (<see cref="HostStartKind"/>); <see langword="null"/> for a heavy suite when
    /// <paramref name="heavySuite"/> is set and an ordinary step otherwise. The start is read against
    /// <see cref="HostStartTracker.Session"/>.</param>
    /// <returns><see langword="true"/> once the host can take the step; <see langword="false"/> when the bound passed
    /// first.</returns>
    public static bool Wait(string step, bool device, bool heavySuite, HostProbe probe, TimeProvider clock, TextWriter error, CancellationToken cancellationToken, TimeSpan? bound = null, Action<string>? stage = null, HostStartKind? kind = null) =>
        Wait(step, device, heavySuite, () => probe.Sample(firstInterval: TimeSpan.FromSeconds(seconds: 1)), clock, delay => Task.Delay(cancellationToken: cancellationToken, delay: delay, timeProvider: clock).GetAwaiter().GetResult(), error, cancellationToken, HostLoadThresholds.ThisMachine, bound, stage, kind, HostStartTracker.Session);
    /// <summary>Gets the admission the root hands to the verbs that cannot reference this assembly: each call samples with a
    /// <see cref="HostProbe"/> over the caller's checkout and waits on a thread-pool thread.</summary>
    /// <param name="composition">The verbs composed beside this one whose own arguments decide whether a run is GPU work.</param>
    /// <param name="clock">The clock the bound and the sleeps are measured on.</param>
    /// <returns>The admission.</returns>
    public static HostAdmit For(GateComposition composition, TimeProvider clock) =>
        (checkout, step, device, heavySuite, kind, bound, progress, stage, cancellationToken) => Task.Run(
            function: () => Wait(step, device, heavySuite, new HostProbe(checkoutRoot: checkout, gpuVerbGrammars: composition.GpuVerbGrammars), clock, progress, cancellationToken, bound, stage, kind),
            cancellationToken: cancellationToken
        );
}
