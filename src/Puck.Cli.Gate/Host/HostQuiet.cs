namespace Puck.Cli.Host;

/// <summary>Waits for the machine to be idle and stay idle, by the same readings <c>puck host load</c> prints
/// (<see cref="HostProbe"/> judged by <see cref="HostLoadMonitor"/>): nothing is a second sampler.</summary>
public static class HostQuiet {
    /// <summary>How often a wait takes a reading.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(seconds: 2);
    /// <summary>The readings the CPU mean covers.</summary>
    public const int Window = 3;

    /// <summary>Names what keeps a reading from being idle.</summary>
    /// <param name="sample">The reading.</param>
    /// <param name="monitor">The monitor that observed this reading; its capacity explanation uses the CPU mean.</param>
    /// <returns><see langword="null"/> when the machine is idle: no GPU work, no heavy test run another process started, and capacity;
    /// otherwise the reason.</returns>
    public static string? Busy(HostSample sample, HostLoadMonitor monitor) =>
        ((sample.GpuHolder is not null)
            ? $"the GPU is held by {sample.GpuHolder}"
            : ((sample.HeavyTestHolder is not null)
                ? $"a heavy test run is held by {sample.HeavyTestHolder}"
                : monitor.CapacityWait));

    /// <summary>Takes readings until the machine has stayed idle (<see cref="Busy"/>) for the settle period without a break.</summary>
    /// <param name="settle">How long the machine must stay idle.</param>
    /// <param name="timeout">How long to wait, at most, from the first reading.</param>
    /// <param name="progress">Receives a line when the start is announced and whenever what the wait is on changes.</param>
    /// <param name="clock">The clock the bounds are measured on.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns><see langword="null"/> once the machine has been idle for <paramref name="settle"/>; otherwise why it was not by <paramref name="timeout"/>.</returns>
    public static async Task<string?> SettleAsync(TimeSpan settle, TimeSpan timeout, TextWriter progress, TimeProvider clock, CancellationToken cancellationToken) {
        var thresholds = HostLoadThresholds.ThisMachine;
        var probe = new HostProbe(checkoutRoot: Environment.CurrentDirectory);
        return await SettleAsync(settle, timeout, progress, clock, cancellationToken,
            () => Task.Run(() => probe.Sample(TimeSpan.FromSeconds(1)), cancellationToken), thresholds).ConfigureAwait(false);
    }

    /// <summary>Settles injected host readings using the same idle decision as the real sampler.</summary>
    /// <param name="settle">Required uninterrupted idle time.</param>
    /// <param name="timeout">Settle time plus the extra load wait allowance, after collecting the CPU window.</param>
    /// <param name="progress">Receives progress and blockers.</param>
    /// <param name="clock">Clock for host waits.</param>
    /// <param name="cancellationToken">Cancels sampling and waits.</param>
    /// <param name="sample">Reads the host.</param>
    /// <param name="thresholds">Idle thresholds.</param>
    /// <returns>Null when settled, otherwise the blocker at the deadline.</returns>
    public static async Task<string?> SettleAsync(TimeSpan settle, TimeSpan timeout, TextWriter progress, TimeProvider clock,
        CancellationToken cancellationToken, Func<Task<HostSample>> sample, HostLoadThresholds thresholds) {
        var monitor = new HostLoadMonitor(cpuSamples: Window, thresholds: thresholds);
        long? started = null;
        var samples = 0;
        long? idleSince = null;
        string? reported = null;

        progress.WriteLine(value: $"lease quiet: waiting for {settle.TotalSeconds:0} seconds of idle machine (at most {timeout.TotalSeconds:0}): no GPU work, no heavy test run, cpu mean under {thresholds.CapacityCpuPercent:0.#}%, free memory over {thresholds.CapacityRamGb:0.0}GB.");

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();

            var reading = await sample().ConfigureAwait(false);

            _ = monitor.Observe(sample: reading);
            // Collecting a CPU mean is sampler setup, not load and not part of the required idle window.
            if (++samples < Window) {
                await Task.Delay(Interval, clock, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var now = clock.GetTimestamp();
            started ??= now;

            var why = Busy(monitor: monitor, sample: reading);

            if (why is null) {
                idleSince ??= now;

                if (clock.GetElapsedTime(startingTimestamp: idleSince.Value) >= settle) {
                    progress.WriteLine(value: $"lease quiet: the machine has been idle for {settle.TotalSeconds:0} seconds.");

                    return null;
                }
                progress.WriteLine(value: $"lease quiet: settling: {clock.GetElapsedTime(idleSince.Value).TotalSeconds:0} s of {settle.TotalSeconds:0} s idle.");
                reported = null;
            } else {
                idleSince = null;

                if (why != reported) {
                    progress.WriteLine(value: $"lease quiet: waiting: {why}.");
                    reported = why;
                }
            }

            var remaining = timeout - clock.GetElapsedTime(started.Value, now);
            var idle = idleSince is { } since ? clock.GetElapsedTime(since, now) : TimeSpan.Zero;

            if (remaining <= TimeSpan.Zero || remaining < settle - idle) { return $"the machine was not idle for {settle.TotalSeconds:0} seconds within {timeout.TotalSeconds:0}: {(why ?? "the idle window had not yet run")}."; }

            await Task.Delay(delay: ((remaining < Interval) ? remaining : Interval), timeProvider: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
