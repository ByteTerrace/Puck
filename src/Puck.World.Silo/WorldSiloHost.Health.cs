namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    private long m_progressTimestamp;

    /// <summary>Checks pump progress independently of storage availability; retirement is intentionally not live.</summary>
    public bool Live => (Ready && !IsDraining && (m_clock.GetElapsedTime(startingTimestamp: Volatile.Read(location: ref m_progressTimestamp)) <
        TimeSpan.FromSeconds(seconds: (m_definition.Lifecycle?.ProgressTimeoutSeconds ?? 30))));

    /// <summary>Reads persistence health at a pump boundary. An unresponsive pump fails within the caller's deadline.</summary>
    /// <param name="cancellationToken">Bounds waiting for the simulation thread.</param>
    /// <returns>An empty string when ready, otherwise the reason readiness is withheld.</returns>
    public Task<string> CheckHealthAsync(CancellationToken cancellationToken) => CheckHealthCoreAsync(
        requireAdmission: true,
        cancellationToken
    );
    /// <summary>Checks candidate persistence and pump health without treating private verification as admission.</summary>
    public Task<string> CheckPrivateHealthAsync(CancellationToken cancellationToken) => CheckHealthCoreAsync(
        requireAdmission: false,
        cancellationToken
    );

    private async Task<string> CheckHealthCoreAsync(bool requireAdmission, CancellationToken cancellationToken) {
        if (!Live) {
            return (IsDraining
            ? "draining"
            : "simulation is not progressing"
        );
        }
        if (
            requireAdmission &&
            !ReleaseAdmissionOpen
        ) { return "release admission is closed"; }
        if (!m_pendingReleases.IsEmpty) { return "world release is not durably committed"; }
        var completion = new TaskCompletionSource<string>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => {
            if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken: cancellationToken); return; }
            var policy = m_definition.Lifecycle;

            foreach (var world in m_definition.Worlds.Where(predicate: static row => row.Pinned)) {
                if (!m_rows.TryGetValue(
                    key: world.World.Value,
                    value: out var row
                )) { completion.TrySetResult(result: $"{world.World}: inactive"); return; }
                if (
                    row.Publications.Blocked ||
                    row.Released ||
                    row.Initializing ||
                    !Instances.TryGet(
                        instance: out var instance,
                        name: world.World.Value
                    ) ||
                    (instance is null)
                ) { completion.TrySetResult(result: $"{world.World}: authority activation is not ready"); return; }
                if (row.LastCheckpointOrdinal < 0) { completion.TrySetResult(result: $"{world.World}: checkpoint overdue"); return; }
                // Age counts from when the row first held state its landed checkpoint does not cover, so a frozen or
                // held row stays healthy however old that checkpoint is.
                if (!NoteCoverage(
                    bookkeeping: row,
                    row: instance
                )) {
                    if (row.CadenceRefusal is { } refusal) {
                        completion.TrySetResult(result: $"{world.World}: checkpoint refused ({refusal})"); return;
                    }
                    if (m_clock.GetElapsedTime(startingTimestamp: row.UncoveredTimestamp!.Value) > TimeSpan.FromSeconds(seconds: (policy?.CheckpointTimeoutSeconds ?? 180))) {
                        completion.TrySetResult(result: $"{world.World}: checkpoint overdue"); return;
                    }
                }
                if (row.RetentionFailures >= RetentionFailureLimit) {
                    completion.TrySetResult(result: $"{world.World}: authority retention failing ({row.RetentionFailure})"); return;
                }
                if (
                    ((row.PendingJournalAppends > 0) && (m_clock.GetElapsedTime(startingTimestamp: row.JournalTimestamp) > TimeSpan.FromSeconds(seconds: (policy?.JournalTimeoutSeconds ?? 30))))
                ) {
                    completion.TrySetResult(result: $"{world.World}: journal persistence unhealthy"); return;
                }
            }
            completion.TrySetResult(result: (Live
                ? ""
                : "simulation is not progressing"));
        });
        return await completion.Task.WaitAsync(cancellationToken: cancellationToken);
    }
}
