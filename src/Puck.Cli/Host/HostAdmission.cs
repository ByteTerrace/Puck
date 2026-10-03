using System.Globalization;

namespace Puck.Cli.Host;

/// <summary>Bounded admission by host load's default thresholds. Memory and disk decide whether a step can run, since
/// running out of either is what fails a build or a heavy suite: a step waits for free memory over the capacity
/// threshold and free disk over the pressure threshold. A busy CPU only slows a step, so the CPU is advisory: a step
/// admitted over the CPU threshold runs, and the admission prints the load it ran under. A step that opens a device also
/// waits for an idle GPU; any other step never waits on a GPU holder.</summary>
internal static class HostAdmission {
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(minutes: 30);
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(seconds: 10);

    private static HostLoadThresholds Thresholds => HostLoadThresholds.Default;

    private static bool HasHeadroom(HostSample reading) => ((reading.FreeRamGb > (Thresholds.CapacityRamGb ?? 0)) && (reading.FreeDiskGb >= (Thresholds.PressureDiskGb ?? 0)));
    // What holds the step back, named so a wait that lasts can be traced to a process or a threshold.
    private static string Reason(string? holder, HostSample reading) => ((holder is not null)
        ? $"the GPU is held by {holder}"
        : string.Create(provider: CultureInfo.InvariantCulture, handler: $"no memory headroom: freeRAM={reading.FreeRamGb:0.0}GB freeDisk={reading.FreeDiskGb:0.0}GB (admission needs freeRAM>{Thresholds.CapacityRamGb}GB and freeDisk>={Thresholds.PressureDiskGb}GB)"));

    /// <summary>Waits until the host can take <paramref name="step"/>, at most <see cref="Timeout"/>. CPU load never
    /// holds a step back.</summary>
    /// <param name="step">The step's name, for the wait lines.</param>
    /// <param name="device">Whether the step opens a GPU device, and so must also wait for the GPU to be idle.</param>
    /// <param name="sample">Takes one reading.</param>
    /// <param name="clock">The clock the bound is measured on.</param>
    /// <param name="delay">Waits one interval.</param>
    /// <param name="error">Receives the wait lines and the advisory CPU line.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns><see langword="true"/> once the host can take the step; <see langword="false"/> when the bound passed
    /// without memory headroom or, for a device step, an idle GPU.</returns>
    public static bool Wait(string step, bool device, Func<HostSample> sample, TimeProvider clock, Action<TimeSpan> delay, TextWriter error, CancellationToken cancellationToken) {
        var started = clock.GetTimestamp();
        string? reason = null;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var reading = sample();
            var holder = (device ? reading.GpuHolder : null);

            if (HasHeadroom(reading: reading) && (holder is null)) {
                if (reason is not null) { error.WriteLine(value: $"gate: capacity returned for {step}."); }
                if (reading.CpuPercent >= (Thresholds.CapacityCpuPercent ?? 100)) {
                    error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"gate: {step} runs under cpu={reading.CpuPercent:0}% (over {Thresholds.CapacityCpuPercent}%; CPU load is advisory and only slows the step)."));
                }
                return true;
            }
            var now = Reason(holder: holder, reading: reading);

            // The GPU holder is named again whenever it changes; a memory reading prints once per wait, not per sample.
            if ((reason is null) || ((holder is not null) && (now != reason))) {
                error.WriteLine(value: $"gate: waiting for host capacity before {step} (at most {Timeout.TotalMinutes:0} minutes): {now}.");
                error.Flush();
                reason = now;
            }
            var remaining = (Timeout - clock.GetElapsedTime(startingTimestamp: started));

            if (remaining <= TimeSpan.Zero) { return false; }
            delay(((remaining < Interval) ? remaining : Interval));
        }
    }
}
