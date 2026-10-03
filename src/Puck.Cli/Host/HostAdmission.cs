namespace Puck.Cli.Host;

/// <summary>Bounded admission through the host monitor's current classification, independent of its line throttles.</summary>
internal static class HostAdmission {
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(minutes: 30);
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(seconds: 10);

    // What holds the step back, named so a wait that lasts can be traced to a process or a threshold.
    private static string Reason(HostSample reading) => ((reading.GpuHolder is { } holder)
        ? $"the GPU is held by {holder}"
        : string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"no capacity: cpu={reading.CpuPercent:0}% freeRAM={reading.FreeRamGb:0.0}GB freeDisk={reading.FreeDiskGb:0.0}GB (capacity needs cpu<{HostLoadThresholds.Default.CapacityCpuPercent}% and freeRAM>{HostLoadThresholds.Default.CapacityRamGb}GB)"));

    public static bool Wait(string step, Func<HostSample> sample, TimeProvider clock, Action<TimeSpan> delay, TextWriter error, CancellationToken cancellationToken) {
        var monitor = new HostLoadMonitor(HostLoadThresholds.Default, cpuSamples: 1);
        var started = clock.GetTimestamp();
        string? reason = null;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var reading = sample();

            _ = monitor.Observe(sample: reading);
            if (monitor.HasCapacity && (reading.GpuHolder is null)) {
                if (reason is not null) { error.WriteLine(value: $"gate: capacity returned for {step}."); }
                return true;
            }
            var now = Reason(reading: reading);

            // The GPU holder is named again whenever it changes; a capacity reading prints once per wait, not per sample.
            if ((reason is null) || ((reading.GpuHolder is not null) && (now != reason))) {
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
