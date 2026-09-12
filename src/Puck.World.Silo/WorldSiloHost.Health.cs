namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    private readonly TimeProvider m_clock = TimeProvider.System;
    private long m_progressTimestamp = TimeProvider.System.GetTimestamp();

    /// <summary>Checks pump progress independently of storage availability; retirement is intentionally not live.</summary>
    public bool Live => (Ready && !IsDraining && (m_clock.GetElapsedTime(startingTimestamp: Volatile.Read(location: ref m_progressTimestamp)) <
        TimeSpan.FromSeconds((m_definition.Lifecycle?.ProgressTimeoutSeconds ?? 30))));

    /// <summary>Reads persistence health at a pump boundary. An unresponsive pump fails within the caller's deadline.</summary>
    /// <param name="cancellationToken">Bounds waiting for the simulation thread.</param>
    /// <returns>An empty string when ready, otherwise the reason readiness is withheld.</returns>
    public async Task<string> CheckHealthAsync(CancellationToken cancellationToken) {
        if (!Live) { return (IsDraining ? "draining" : "simulation is not progressing"); }
        if (!m_pendingReleases.IsEmpty) { return "world release is not durably committed"; }
        var completion = new TaskCompletionSource<string>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        m_mailbox.Enqueue(() => {
            if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken: cancellationToken); return; }
            var policy = m_definition.Lifecycle;

            foreach (var world in m_definition.Worlds.Where(predicate: static row => row.Pinned)) {
                if (!m_rows.TryGetValue(key: world.World.Value, value: out var row)) { completion.TrySetResult(result: $"{world.World}: inactive"); return; }
                if (row.PersistenceBlocked || row.Released || row.Initializing) { completion.TrySetResult($"{world.World}: authority activation is not ready"); return; }
                if ((row.LastCheckpointOrdinal < 0) || (m_clock.GetElapsedTime(startingTimestamp: row.CheckpointTimestamp) > TimeSpan.FromSeconds((policy?.CheckpointTimeoutSeconds ?? 180)))) {
                    completion.TrySetResult(result: $"{world.World}: checkpoint overdue"); return;
                }
                if ((row.JournalFailed && (row.LastCheckpointTick <= row.JournalFailureTick)) || (row.PendingJournalAppends > (policy?.JournalBacklogLimit ?? 1024)) ||
                    ((row.PendingJournalAppends > 0) && (m_clock.GetElapsedTime(startingTimestamp: row.JournalTimestamp) > TimeSpan.FromSeconds((policy?.JournalTimeoutSeconds ?? 30))))) {
                    completion.TrySetResult(result: $"{world.World}: journal persistence unhealthy"); return;
                }
            }
            completion.TrySetResult(result: (Live ? "" : "simulation is not progressing"));
        });
        return await completion.Task.WaitAsync(cancellationToken: cancellationToken);
    }
}
