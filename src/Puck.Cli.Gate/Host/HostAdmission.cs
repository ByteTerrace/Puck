using System.Globalization;

namespace Puck.Cli.Host;

/// <summary>Bounded admission by host load's default thresholds. Memory and disk decide whether a step can run, since
/// running out of either is what fails a build or a heavy suite: a step waits for free memory over the capacity
/// threshold and free disk over the pressure threshold. A busy CPU only slows a step, so the CPU is advisory: a step
/// admitted over the CPU threshold runs, and the admission prints the load it ran under. A step that opens a device also
/// waits for an idle GPU; any other step never waits on a GPU holder. A heavy suite (<see cref="HostProcesses.IsHeavyTestAssembly"/>)
/// also waits while another process runs one, machine-wide, since two at once exhaust memory; the waiting process's own
/// runs never hold it back.</summary>
public static class HostAdmission {
    /// <summary>How long a step waits before it is refused.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(minutes: 30);
    /// <summary>How long a heavy suite waits before it is refused: another heavy run can take half an hour under load,
    /// and a gate can queue behind two, so refusing sooner would throw away the gate's build.</summary>
    public static readonly TimeSpan HeavyTimeout = TimeSpan.FromHours(hours: 2);
    /// <summary>How often a wait samples the machine.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(seconds: 10);
    /// <summary>How long a wait goes without printing before it prints that it is still waiting.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(minutes: 10);

    private static HostLoadThresholds Thresholds => HostLoadThresholds.Default;

    private static bool HasHeadroom(HostSample reading) => ((reading.FreeRamGb > (Thresholds.CapacityRamGb ?? 0)) && (reading.FreeDiskGb >= (Thresholds.PressureDiskGb ?? 0)));
    // What holds the step back, named so a wait that lasts can be traced to a process or a threshold.
    private static string Reason(string? gpu, string? heavy, HostSample reading) => ((gpu is not null)
        ? $"the GPU is held by {gpu}"
        : ((heavy is not null)
            ? $"a heavy test run is held by {heavy}"
            : string.Create(provider: CultureInfo.InvariantCulture, handler: $"no memory headroom: freeRAM={reading.FreeRamGb:0.0}GB freeDisk={reading.FreeDiskGb:0.0}GB (admission needs freeRAM>{Thresholds.CapacityRamGb}GB and freeDisk>={Thresholds.PressureDiskGb}GB)")));

    /// <summary>Waits until the host can take <paramref name="step"/>, at most <see cref="Timeout"/>, or
    /// <see cref="HeavyTimeout"/> for a heavy suite. CPU load never holds a step back.</summary>
    /// <param name="step">The step's name, for the wait lines.</param>
    /// <param name="device">Whether the step opens a GPU device, and so must also wait for the GPU to be idle.</param>
    /// <param name="heavySuite">Whether the step runs a heavy suite, and so must also wait while another process runs
    /// one.</param>
    /// <param name="sample">Takes one reading.</param>
    /// <param name="clock">The clock the bound is measured on.</param>
    /// <param name="delay">Waits one interval.</param>
    /// <param name="error">Receives the wait lines and the advisory CPU line.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns><see langword="true"/> once the host can take the step; <see langword="false"/> when the bound passed
    /// first.</returns>
    public static bool Wait(string step, bool device, bool heavySuite, Func<HostSample> sample, TimeProvider clock, Action<TimeSpan> delay, TextWriter error, CancellationToken cancellationToken) {
        var started = clock.GetTimestamp();
        var bound = (heavySuite ? HeavyTimeout : Timeout);
        var printed = started;
        string? reason = null;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var reading = sample();
            var gpu = (device ? reading.GpuHolder : null);
            var heavy = (heavySuite ? reading.HeavyTestHolder : null);

            if (HasHeadroom(reading: reading) && (gpu is null) && (heavy is null)) {
                if (reason is not null) { error.WriteLine(value: $"gate: capacity returned for {step}."); }
                if (reading.CpuPercent >= (Thresholds.CapacityCpuPercent ?? 100)) {
                    error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"gate: {step} runs under cpu={reading.CpuPercent:0}% (over {Thresholds.CapacityCpuPercent}%; CPU load is advisory and only slows the step)."));
                }
                return true;
            }
            var now = Reason(gpu: gpu, heavy: heavy, reading: reading);
            var waited = clock.GetElapsedTime(startingTimestamp: started);

            // A holder is named again whenever it changes; a memory reading prints once per wait, not per sample. A wait
            // that has printed nothing for a heartbeat says it is still waiting, and on what.
            if ((reason is null) || (((gpu ?? heavy) is not null) && (now != reason))) {
                error.WriteLine(value: $"gate: waiting for host capacity before {step} (at most {bound.TotalMinutes:0} minutes): {now}.");
                error.Flush();
                reason = now;
                printed = clock.GetTimestamp();
            } else if (clock.GetElapsedTime(startingTimestamp: printed) >= Heartbeat) {
                error.WriteLine(value: $"gate: still waiting before {step} after {waited.TotalMinutes:0} minutes: {now}.");
                error.Flush();
                printed = clock.GetTimestamp();
            }
            var remaining = (bound - waited);

            if (remaining <= TimeSpan.Zero) { return false; }
            delay(((remaining < Interval) ? remaining : Interval));
        }
    }
}
