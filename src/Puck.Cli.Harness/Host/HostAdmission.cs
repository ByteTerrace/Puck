using System.Globalization;

namespace Puck.Cli.Host;

/// <summary>Bounded admission by the machine's host-load thresholds (<see cref="HostLoadThresholds.For"/>), the one
/// admission every heavy start passes: the gate and <c>affected</c> for their steps, <c>puck lease heavy</c> and
/// <c>puck lease gpu</c> for their commands, and the MCP adapter for a World it starts. Memory and disk decide whether a
/// step can run, since running out of either is what fails a build or a heavy suite: a step waits while free memory is
/// under the pressure threshold, while the commit headroom is under its reserve (allocation fails at the limit with
/// physical memory still free) and while free disk is under the pressure threshold. A start is admitted by what it
/// needs, never by a fixed free-memory or commit floor (<see cref="HostStartTracker"/>): the commit headroom must cover
/// the start's own peak in private bytes (what its <see cref="HostStartProfile"/> has measured on this machine, else
/// <see cref="HostLoadThresholds.DefaultPeakGb"/>, the machine's World budget)
/// plus what the admitted starts still running have left to grow, since admitted Worlds grow into each other, and
/// still hold the commit reserve afterwards (<see cref="HostLoadThresholds.CommitCovers"/>); free physical memory must
/// cover the start's own private working set plus the running starts' private working-set growth and still sit at the
/// pressure floor
/// (<see cref="HostLoadThresholds.PhysicalCovers"/>). A start whose own need exceeds the machine's totals is refused by
/// name at once (<see cref="HostLoadThresholds.NeverFits"/>). The growth of what other processes were admitted under a
/// lease counts from the grant (<see cref="HostSample.Holds"/>), so heavy holders share the machine by measured headroom
/// rather than one at a time. A busy CPU only slows a step, so the CPU is advisory for a lone start: a step admitted over
/// the CPU threshold runs, and the admission prints the load it ran under. A heavy start beside another process's running
/// heavy hold also waits while the CPU is at or over the capacity threshold, since it would take the cores that work is
/// using. A step that opens a device likewise waits only while another device holder (a device process or another
/// process's hold for device work that has not gone idle) is working and the GPU's measured video memory in use is the
/// whole of the device's (<see cref="HostLoadThresholds.DeviceHasHeadroom"/>), so devices share the GPU by measured
/// headroom;
/// any other step never waits on a GPU holder. A wait is the host's timed wait on the caller's token, never a
/// spin.</summary>
public static class HostAdmission {
    /// <summary>How long a step waits before it is refused.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(minutes: 30);
    /// <summary>The prefix of the stage text a wait reports when it refuses a start that no wait could admit; what follows
    /// it names why. A bound that passes first reports no such stage.</summary>
    public const string RefusedStage = "refused: ";
    /// <summary>How often a wait samples the machine.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(seconds: 10);
    /// <summary>How long a wait goes without printing before it prints that it is still waiting.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(minutes: 10);

    private static string KindName(HostStartProfile profile) {
        var named = profile.Class;

        return named.Kind switch {
            HostStartKind.WindowedWorld => string.Concat("windowed World", (named.Store == StoreWarmth.Cold) ? " with a cold mesh store" : string.Empty, (named.Device is { } device) ? $" on {device}" : string.Empty),
            HostStartKind.HeadlessWorld => "headless World",
            HostStartKind.HeavySuite => "heavy suite",
            HostStartKind.ReproduceReplica => "test-batch replica",
            _ => "step",
        };
    }
    // What holds a reading back from memory headroom, each as its reading and what admission needs of it.
    private static List<(string Reading, string Need)> Shortfalls(HostSample reading, HostLoadThresholds thresholds, HostStartDemand? demand) {
        List<(string Reading, string Need)> shortfalls = [];

        var reserve = thresholds.CommitReserveGb(reading: reading);

        if (!thresholds.CommitHasHeadroom(reading: reading)) {
            shortfalls.Add(item: (
                string.Create(provider: CultureInfo.InvariantCulture, handler: $"commit headroom {(reading.CommitLimitGb - reading.CommitChargeGb):0.0}GB ({reading.CommitPercent:0}% of limit charged)"),
                string.Create(provider: CultureInfo.InvariantCulture, handler: $"at least {reserve:0.0}GB (the commit reserve, {thresholds.CommitReservePercent:0}% of the limit)")
            ));
        } else if (demand is { CommitCovered: false } uncovered) {
            shortfalls.Add(item: (
                string.Create(provider: CultureInfo.InvariantCulture, handler: $"commit headroom {uncovered.HeadroomGb:0.0}GB"),
                string.Create(provider: CultureInfo.InvariantCulture, handler: $"at least {(uncovered.NeedGb + reserve):0.0}GB ({uncovered.OwnGb:0.0}GB expected peak of a {KindName(profile: uncovered.Profile ?? uncovered.Kind)} plus {uncovered.GrowthGb:0.0}GB still to grow in {uncovered.Growing} admitted start(s) plus the {reserve:0.0}GB commit reserve)")
            ));
        }
        if ((thresholds.PressureRamGb is { } floor) && (reading.FreeRamGb < floor)) {
            shortfalls.Add(item: (string.Create(provider: CultureInfo.InvariantCulture, handler: $"freeRAM={reading.FreeRamGb:0.0}GB"), string.Create(provider: CultureInfo.InvariantCulture, handler: $"at least {floor:0.0}GB (memory pressure)")));
        } else if (demand is { PhysicalCovered: false } crowded) {
            shortfalls.Add(item: (
                string.Create(provider: CultureInfo.InvariantCulture, handler: $"freeRAM={crowded.FreeRamGb:0.0}GB"),
                string.Create(provider: CultureInfo.InvariantCulture, handler: $"at least {(crowded.ResidentNeedGb + (thresholds.PressureRamGb ?? 0)):0.0}GB ({crowded.ResidentOwnGb:0.0}GB expected private working set of a {KindName(profile: crowded.Profile ?? crowded.Kind)}{(double.IsFinite(crowded.WorkingSetOwnGb) ? $" of its {crowded.WorkingSetOwnGb:0.0}GB working set" : string.Empty)} plus {crowded.ResidentGrowthGb:0.0}GB still to grow in the admitted starts plus the {thresholds.PressureRamGb ?? 0:0.0}GB pressure floor)")
            ));
        }
        if (!(reading.FreeDiskGb >= (thresholds.PressureDiskGb ?? 0))) {
            shortfalls.Add(item: (string.Create(provider: CultureInfo.InvariantCulture, handler: $"freeDisk={reading.FreeDiskGb:0.0}GB"), string.Create(provider: CultureInfo.InvariantCulture, handler: $"at least {thresholds.PressureDiskGb:0.0}GB")));
        }

        return shortfalls;
    }
    // What holds the step back, named so a wait that lasts can be traced to a process or a threshold: the line a wait
    // prints, and the shorter phrase a caller shows as the stage it is in.
    private static (string Line, string Stage) Reason(string? gpu, string? cpu, HostSample reading, HostLoadThresholds thresholds, HostStartDemand demand) {
        if (gpu is not null) { return ($"the GPU is held by {gpu}", $"the GPU, held by {gpu}"); }
        if (cpu is not null) { return (cpu, "CPU headroom beside the running heavy work"); }

        var shortfalls = Shortfalls(reading: reading, thresholds: thresholds, demand: demand);

        return (
            $"no memory headroom: {string.Join(separator: "; ", values: shortfalls.Select(selector: static shortfall => $"{shortfall.Reading} (admission needs {shortfall.Need})"))}{(demand.GrowthGb > 0 ? "; running starts: " + string.Join(", ", (reading.Starts ?? []).Select(start => $"{start.Kind} PID {start.Id} private={start.PrivateGb:0.0}GB ready={start.Ready}{start.IdleNote}")) : string.Empty)}{((demand.GrowthGb > 0) && (reading.Holds is { Count: > 0 } holds) ? "; other holds: " + string.Join(", ", holds.Select(hold => $"{hold.Lease} {hold.Holder} ({hold.Kind}) PID {hold.Id} tree private={hold.PrivateGb:0.0}GB")) : string.Empty)}",
            $"memory headroom: {string.Join(separator: ", ", values: shortfalls.Select(selector: static shortfall => shortfall.Reading))}"
        );
    }
    // What holds a heavy start back on CPU, or null: another process's heavy hold is running and the CPU is at or over the
    // capacity threshold. A lone heavy start is never held back on CPU, which only slows it; a further one would take cores
    // the running heavy work is using.
    private static string? Crowded(HostSample reading, HostLoadThresholds thresholds) {
        var running = (reading.Holds ?? []).Where(predicate: static hold => (hold.Lease == "heavy")).ToArray();

        return (((running.Length > 0) && (thresholds.CapacityCpuPercent is { } capacity) && (reading.CpuPercent >= capacity))
            ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"no CPU headroom: cpu={reading.CpuPercent:0}% with {running.Length} other heavy holder(s) running ({string.Join(separator: ", ", values: running.Select(selector: static hold => $"{hold.Holder} PID {hold.Id}"))}); a further heavy start needs cpu under {capacity:0.#}%")
            : null);
    }
    // What holds a device start back, or null: a further device holder is working (the reading names one that is not idle and
    // not this process's own) and the device has no headroom for another (HostLoadThresholds.DeviceHasHeadroom). A lone device
    // start is never held back, and a holder that has gone idle gives its place back: the probe leaves it out.
    private static string? DeviceFull(HostSample reading, HostLoadThresholds thresholds) => (((reading.GpuHolder is { } holder) && !thresholds.DeviceHasHeadroom(reading: reading))
        ? (double.IsFinite(d: reading.DeviceTotalGb) && double.IsFinite(d: reading.DeviceUsedGb)
            ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{holder}, with {reading.DeviceUsedGb:0.0}GB of {reading.DeviceTotalGb:0.0}GB device memory in use")
            : holder)
        : null);
    private static string Span(TimeSpan span) => ((span >= TimeSpan.FromMinutes(minutes: 1))
        ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{span.TotalMinutes:0} minutes")
        : string.Create(provider: CultureInfo.InvariantCulture, handler: $"{span.TotalSeconds:0} seconds"));

    /// <summary>Waits until the host can take <paramref name="step"/>, at most <paramref name="bound"/>, or
    /// <see cref="Timeout"/> (<see cref="CliLease.DefaultWait"/> for a heavy suite) when it is <see langword="null"/>.
    /// CPU load never holds a step back.</summary>
    /// <param name="step">The step's name, for the wait lines.</param>
    /// <param name="device">Whether the step opens a GPU device, and so must also wait for the GPU to be idle.</param>
    /// <param name="heavySuite">Whether the step runs heavy work, and so must also wait for CPU headroom while another
    /// process's heavy hold runs.</param>
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
    /// <param name="reserve">Whether an admitted start reserves its expected peak; a caller that only waits for headroom to
    /// exist, and judges the start itself later, reserves nothing.</param>
    /// <returns><see langword="true"/> once the host can take the step; <see langword="false"/> when the bound passed
    /// first.</returns>
    public static bool Wait(string step, bool device, bool heavySuite, Func<HostSample> sample, TimeProvider clock, Action<TimeSpan> delay, TextWriter error, CancellationToken cancellationToken, HostLoadThresholds thresholds, TimeSpan? bound = null, Action<string>? stage = null, HostStartProfile? kind = null, HostStartTracker? tracker = null, bool reserve = true) {
        var started = clock.GetTimestamp();
        var limit = (bound ?? (heavySuite ? CliLease.DefaultWait : Timeout));
        var printed = started;
        var peak = (kind ?? new HostStartProfile(Kind: (heavySuite ? HostStartKind.HeavySuite : HostStartKind.Step)));
        var starts = (tracker ?? new HostStartTracker());
        string? reason = null;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var reading = sample();
            var gpu = (device ? DeviceFull(reading: reading, thresholds: thresholds) : null);
            var cpu = (heavySuite ? Crowded(reading: reading, thresholds: thresholds) : null);
            // The peak is judged last: only a start that nothing else holds back reserves its headroom.
            var held = ((gpu is not null) || (cpu is not null) || Shortfalls(reading: reading, thresholds: thresholds, demand: null).Any());
            var demand = starts.Judge(clock: clock, kind: peak, reading: reading, reserve: (reserve && !held), thresholds: thresholds);

            // A start the idle machine could not hold is refused by name at once: no wait changes it.
            if (thresholds.NeverFits(reading: reading, ownGb: demand.LoneOwnGb, residentOwnGb: demand.LoneResidentOwnGb) is { } never) {
                var refusal = $"a {KindName(profile: peak)} can never be admitted on this machine, {never}";

                error.WriteLine(value: $"gate: refused {step}: {refusal}.");
                error.Flush();
                stage?.Invoke(obj: (RefusedStage + refusal));

                return false;
            }

            if (!held && demand.Covered) {
                if (peak.Kind != HostStartKind.Step && double.IsFinite(demand.HeadroomGb)) { error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gate: admitted {step}: commit headroom {demand.HeadroomGb:0.0}GB covers {demand.NeedGb:0.0}GB ({demand.OwnGb:0.0}GB expected peak plus {demand.GrowthGb:0.0}GB growth in {demand.Growing} start(s)).")); }
                if (reason is not null) { error.WriteLine(value: $"gate: capacity returned for {step}."); }
                if (reading.CpuPercent >= (thresholds.CapacityCpuPercent ?? 100)) {
                    error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"gate: {step} runs under cpu={reading.CpuPercent:0}% (over {thresholds.CapacityCpuPercent}%; CPU load is advisory and only slows the step)."));
                }
                return true;
            }
            var (now, phrase) = Reason(cpu: cpu, gpu: gpu, reading: reading, thresholds: thresholds, demand: demand);
            var waited = clock.GetElapsedTime(startingTimestamp: started);

            stage?.Invoke(obj: $"waiting for {phrase}");

            // A holder is named again whenever it changes; a memory reading prints once per wait, not per sample. A wait
            // that has printed nothing for a heartbeat says it is still waiting, and on what.
            if ((reason is null) || ((gpu is not null) && (now != reason))) {
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

            if (remaining <= TimeSpan.Zero) {
                error.WriteLine($"gate: refused {step} after {Span(waited)}: {now}.");
                error.Flush();
                return false;
            }
            delay(((remaining < Interval) ? remaining : Interval));
        }
    }
    /// <summary>Waits as <see cref="Wait(string, bool, bool, Func{HostSample}, TimeProvider, Action{TimeSpan}, TextWriter, CancellationToken, HostLoadThresholds, TimeSpan?, Action{string}?, HostStartProfile?, HostStartTracker?, bool)"/>
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
    /// <param name="kind">What the start is (<see cref="HostStartProfile"/>); <see langword="null"/> for a heavy suite when
    /// <paramref name="heavySuite"/> is set and an ordinary step otherwise. The start is read against
    /// <see cref="HostStartTracker.Session"/>.</param>
    /// <param name="reserve">Whether an admitted start reserves its expected peak.</param>
    /// <returns><see langword="true"/> once the host can take the step; <see langword="false"/> when the bound passed
    /// first.</returns>
    public static bool Wait(string step, bool device, bool heavySuite, HostProbe probe, TimeProvider clock, TextWriter error, CancellationToken cancellationToken, TimeSpan? bound = null, Action<string>? stage = null, HostStartProfile? kind = null, bool reserve = true) =>
        Wait(step, device, heavySuite, () => probe.Sample(firstInterval: TimeSpan.FromSeconds(seconds: 1)), clock, delay => Task.Delay(cancellationToken: cancellationToken, delay: delay, timeProvider: clock).GetAwaiter().GetResult(), error, cancellationToken, HostLoadThresholds.ThisMachine, bound, stage, kind, HostStartTracker.Session, reserve);
    /// <summary>Gets the admission the root hands to the verbs that cannot reference this assembly: each call samples with a
    /// <see cref="HostProbe"/> over the caller's checkout and waits on a thread-pool thread.</summary>
    /// <param name="clock">The clock the bound and the sleeps are measured on.</param>
    /// <returns>The admission.</returns>
    public static HostAdmit For(TimeProvider clock) =>
        (checkout, step, device, heavySuite, kind, bound, reserve, progress, stage, cancellationToken) => Task.Run(
            function: () => Wait(step, device, heavySuite, new HostProbe(checkoutRoot: checkout), clock, progress, cancellationToken, bound, stage, kind, reserve),
            cancellationToken: cancellationToken
        );
}
