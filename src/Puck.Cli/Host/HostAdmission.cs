namespace Puck.Cli.Host;

/// <summary>Bounded admission through the host monitor's current classification, independent of its line throttles.</summary>
internal static class HostAdmission {
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(minutes: 30);
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(seconds: 10);

    public static bool Wait(string step, Func<HostSample> sample, TimeProvider clock, Action<TimeSpan> delay, TextWriter error, CancellationToken cancellationToken) {
        var monitor = new HostLoadMonitor(HostLoadThresholds.Default, cpuSamples: 1);
        var started = clock.GetTimestamp();
        var waiting = false;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var reading = sample();

            _ = monitor.Observe(sample: reading);
            if (monitor.HasCapacity && (reading.GpuHolder is null)) {
                if (waiting) { error.WriteLine(value: $"gate: capacity returned for {step}."); }
                return true;
            }
            if (!waiting) {
                error.WriteLine(value: $"gate: waiting for host capacity before {step} (at most {Timeout.TotalMinutes:0} minutes).");
                error.Flush();
                waiting = true;
            }
            var remaining = (Timeout - clock.GetElapsedTime(startingTimestamp: started));

            if (remaining <= TimeSpan.Zero) { return false; }
            delay(((remaining < Interval) ? remaining : Interval));
        }
    }
}
