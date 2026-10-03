namespace Puck.Cli.Host;

/// <summary>Bounded admission through the host monitor's current classification, independent of its line throttles. A
/// step that opens a device waits for capacity and an idle GPU; any other step waits for capacity alone, since a GPU
/// holder takes nothing it needs.</summary>
internal static class HostAdmission {
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(minutes: 30);
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(seconds: 10);

    // What holds the step back, named so a wait that lasts can be traced to a process or a threshold.
    private static string Reason(string? holder, HostSample reading) => ((holder is not null)
        ? $"the GPU is held by {holder}"
        : string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"no capacity: cpu={reading.CpuPercent:0}% freeRAM={reading.FreeRamGb:0.0}GB freeDisk={reading.FreeDiskGb:0.0}GB (capacity needs cpu<{HostLoadThresholds.Default.CapacityCpuPercent}% and freeRAM>{HostLoadThresholds.Default.CapacityRamGb}GB)"));

    /// <summary>Waits until the host can take <paramref name="step"/>, at most <see cref="Timeout"/>.</summary>
    /// <param name="step">The step's name, for the wait lines.</param>
    /// <param name="device">Whether the step opens a GPU device, and so must also wait for the GPU to be idle.</param>
    /// <param name="sample">Takes one reading.</param>
    /// <param name="clock">The clock the bound is measured on.</param>
    /// <param name="delay">Waits one interval.</param>
    /// <param name="error">Receives the wait lines.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns><see langword="true"/> once the host can take the step; <see langword="false"/> when the bound passed.</returns>
    public static bool Wait(string step, bool device, Func<HostSample> sample, TimeProvider clock, Action<TimeSpan> delay, TextWriter error, CancellationToken cancellationToken) {
        var monitor = new HostLoadMonitor(HostLoadThresholds.Default, cpuSamples: 1);
        var started = clock.GetTimestamp();
        string? reason = null;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var reading = sample();
            var holder = (device ? reading.GpuHolder : null);

            _ = monitor.Observe(sample: reading);
            if (monitor.HasCapacity && (holder is null)) {
                if (reason is not null) { error.WriteLine(value: $"gate: capacity returned for {step}."); }
                return true;
            }
            var now = Reason(holder: holder, reading: reading);

            // The GPU holder is named again whenever it changes; a capacity reading prints once per wait, not per sample.
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
