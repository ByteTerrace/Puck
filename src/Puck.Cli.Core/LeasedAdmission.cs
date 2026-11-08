namespace Puck.Cli;

/// <summary>Acquires a lease and admits a start through the same bounded host judgement for every caller.</summary>
public static class LeasedAdmission {
    /// <summary>The longest a device start holds an idle GPU while judging headroom.</summary>
    public static readonly TimeSpan LeasedAdmissionBound = TimeSpan.FromSeconds(seconds: 30);
    /// <summary>The maximum number of acquisitions after headroom changes while queuing.</summary>
    public const int MaximumRequeues = 8;

    /// <summary>Waits unreserved before queuing for a device, then reserves under its lease. A failed leased
    /// judgement releases the hold before retrying. Non-device starts judge once with their lease held.</summary>
    public static async Task<IDisposable> AcquireAsync(Func<TimeSpan?, CancellationToken, Task<IDisposable>> acquire, HostAdmit? admit,
        string checkout, string step, bool device, bool heavySuite, HostStartKind kind, TimeProvider clock,
        TimeSpan? bound, TextWriter progress, Action<string>? stage, CancellationToken cancellationToken) {
        var started = clock.GetTimestamp();
        TimeSpan? Remaining() => bound is { } limit ? TimeSpan.FromTicks( Math.Max(val1: 0, val2: (limit - clock.GetElapsedTime(startingTimestamp: started)).Ticks)) : null;
        Task<bool> Judge(bool reserve, TimeSpan? limit) => admit?.Invoke(checkout, step, device, heavySuite, kind, limit, reserve, progress, stage, cancellationToken) ?? Task.FromResult(result: true);

        for (var attempt = 1; ; attempt++) {
            if (device) {
                stage?.Invoke(obj: "host-admission");
                if (!await Judge(reserve: false, limit: Remaining()).ConfigureAwait(false)) { throw new HostAdmissionRefusedException(step: step); }
            }
            stage?.Invoke(obj: device ? "gpu-lease" : "lease");
            var held = await acquire(Remaining(), cancellationToken).ConfigureAwait(false);
            try {
                stage?.Invoke(obj: "host-admission");
                var left = Remaining();
                var limit = device && (left is null || left > LeasedAdmissionBound) ? LeasedAdmissionBound : left;
                if (await Judge(reserve: true, limit: limit).ConfigureAwait(false)) { return held; }
            } catch {
                held.Dispose();
                throw;
            }
            held.Dispose();
            if (!device || attempt == MaximumRequeues || Remaining() == TimeSpan.Zero) { throw new HostAdmissionRefusedException(step: step); }
            progress.WriteLine(value: $"{step}: the host's headroom went to another start while the GPU lease was held; released it and waiting for headroom again.");
        }
    }
}

/// <summary>A host start was refused before its process could run.</summary>
public sealed class HostAdmissionRefusedException(string step) : InvalidOperationException(message: $"the machine had no memory, commit or disk headroom (or its GPU stayed busy) within the admission bound, so {step} did not start.");
