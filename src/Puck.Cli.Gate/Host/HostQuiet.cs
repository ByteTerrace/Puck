using System.Globalization;

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
    /// <param name="hasCapacity">Whether <see cref="HostLoadMonitor"/> found capacity at this reading.</param>
    /// <param name="thresholds">The thresholds capacity was judged by.</param>
    /// <returns><see langword="null"/> when the machine is idle: no GPU work, no heavy test run another process started, and capacity;
    /// otherwise the reason.</returns>
    public static string? Busy(HostSample sample, bool hasCapacity, HostLoadThresholds thresholds) =>
        ((sample.GpuHolder is not null)
            ? $"the GPU is held by {sample.GpuHolder}"
            : ((sample.HeavyTestHolder is not null)
                ? $"a heavy test run is held by {sample.HeavyTestHolder}"
                : (hasCapacity
                    ? null
                    : string.Create(provider: CultureInfo.InvariantCulture, handler: $"the machine is loaded: cpu={sample.CpuPercent:0}% (idle needs a mean under {thresholds.CapacityCpuPercent:0.#}%) freeRAM={sample.FreeRamGb:0.0}GB (needs over {thresholds.CapacityRamGb:0.0}GB) freeDisk={sample.FreeDiskGb:0.0}GB{(double.IsNaN(d: sample.CommitPercent) ? string.Empty : $" commit={sample.CommitPercent:0}% (needs under {thresholds.CapacityCommitPercent:0.#}%)")}"))));

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
        var monitor = new HostLoadMonitor(cpuSamples: Window, thresholds: thresholds);
        var started = clock.GetTimestamp();
        long? idleSince = null;
        string? reported = null;

        progress.WriteLine(value: $"lease quiet: waiting for {settle.TotalSeconds:0} seconds of idle machine (at most {timeout.TotalSeconds:0}): no GPU work, no heavy test run, cpu mean under {thresholds.CapacityCpuPercent:0.#}%, free memory over {thresholds.CapacityRamGb:0.0}GB.");

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();

            var sample = await Task.Run(function: () => probe.Sample(firstInterval: TimeSpan.FromSeconds(seconds: 1)), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            _ = monitor.Observe(sample: sample);

            var why = Busy(hasCapacity: monitor.HasCapacity, sample: sample, thresholds: thresholds);

            if (why is null) {
                idleSince ??= clock.GetTimestamp();

                if (clock.GetElapsedTime(startingTimestamp: idleSince.Value) >= settle) {
                    progress.WriteLine(value: $"lease quiet: the machine has been idle for {settle.TotalSeconds:0} seconds.");

                    return null;
                }
            } else {
                idleSince = null;

                if (why != reported) {
                    progress.WriteLine(value: $"lease quiet: waiting: {why}.");
                    reported = why;
                }
            }

            var remaining = (timeout - clock.GetElapsedTime(startingTimestamp: started));

            if (remaining <= TimeSpan.Zero) { return $"the machine was not idle for {settle.TotalSeconds:0} seconds within {timeout.TotalSeconds:0}: {(why ?? "the idle window had not yet run")}."; }

            await Task.Delay(delay: ((remaining < Interval) ? remaining : Interval), timeProvider: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
