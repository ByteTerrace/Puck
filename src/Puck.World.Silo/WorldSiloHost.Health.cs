namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    private long m_progressTimestamp;
    // What the pump is inside, for health to name when it stops stepping. Written by the pump thread alone and read by
    // health on any thread; a torn item/instant pair only misdates a diagnosis, so the reads need no lock. The mailbox
    // item is the posted delegate itself, so noting one allocates nothing; a held step allocates once per new reason.
    private Action? m_pumpMailboxItem;
    private long m_pumpMailboxSince;
    private HeldStep? m_pumpWait;
    private Exception? m_pumpFault;
    // Startup activation's progress and its first refusal, for health to name before the host is ready.
    private ActivationProgress? m_activation;
    private string? m_activationFailure;

    private sealed record HeldStep(Puck.Abstractions.Waiting.WaitReason Reason, long Since);
    private sealed record ActivationProgress(string Row, string? FinishedStage, long Since);

    /// <summary>Checks pump progress independently of storage availability; retirement is intentionally not live.</summary>
    public bool Live => (Ready && !IsDraining && !Stalled);

    private bool Stalled => (m_clock.GetElapsedTime(startingTimestamp: Volatile.Read(location: ref m_progressTimestamp)) >=
        TimeSpan.FromSeconds(seconds: (m_definition.Lifecycle?.ProgressTimeoutSeconds ?? 30)));

    /// <summary>Records that startup activation of a row stopped, so health names the cause rather than a pump that was
    /// never asked to step. The first failure is kept; the host is expected to stop after it.</summary>
    /// <param name="reason">The refusal or fault that stopped activation.</param>
    public void NoteActivationFailure(string reason) => _ = Interlocked.CompareExchange(
        comparand: null,
        location1: ref m_activationFailure,
        value: reason
    );
    private void NoteActivation(string row, string? finishedStage) => Volatile.Write(
        location: ref m_activation,
        value: new ActivationProgress(Row: row, FinishedStage: finishedStage, Since: m_clock.GetTimestamp())
    );
    private void RefuseActivation(string row, string reason) {
        Console.Error.WriteLine(value: $"[silo.activate: '{row}' refused ({reason})]");
        NoteActivationFailure(reason: $"'{row}' refused ({reason})");
    }
    // Runs one posted mailbox item with the pump's diagnosis naming it while it runs.
    private void RunMailboxItem(Action action) {
        Volatile.Write(location: ref m_pumpMailboxSince, value: m_clock.GetTimestamp());
        Volatile.Write(location: ref m_pumpMailboxItem, value: action);
        action();
        Volatile.Write(location: ref m_pumpMailboxItem, value: null);
    }
    /// <summary>Records what the step in progress waits on, or that it finished (<see langword="null"/>). A wait keeps the
    /// instant it began across the pump's repeated resumptions of the same step.</summary>
    /// <param name="reason">The reason the instance step is held, or <see langword="null"/> once the step completed.</param>
    public void NotePumpWait(Puck.Abstractions.Waiting.WaitReason? reason) {
        if (reason is not { } held) {
            Volatile.Write(location: ref m_pumpWait, value: null);
            return;
        }
        if (Volatile.Read(location: ref m_pumpWait)?.Reason != held) {
            Volatile.Write(location: ref m_pumpWait, value: new HeldStep(Reason: held, Since: m_clock.GetTimestamp()));
        }
    }
    /// <summary>Records the exception that ended the pump. The host loop stops the application on it; health names it
    /// until the process exits.</summary>
    /// <param name="fault">The exception the simulation step threw.</param>
    public void NotePumpFault(Exception fault) => _ = Interlocked.CompareExchange(
        comparand: null,
        location1: ref m_pumpFault,
        value: fault
    );
    /// <summary>Describes why the host is not live: draining, the pump's fault, startup activation's progress or
    /// refusal, or what the pump is inside since its last step. Empty when the host is live.</summary>
    /// <returns>The diagnosis, or an empty string.</returns>
    public string DescribeLiveness() {
        if (IsDraining) { return "draining"; }
        if (Volatile.Read(location: ref m_pumpFault) is { } fault) {
            return $"the simulation pump faulted ({fault.GetType().Name}: {fault.Message.ReplaceLineEndings(replacementText: " ")})";
        }
        if (!Ready) {
            if (Volatile.Read(location: ref m_activationFailure) is { } failure) { return $"activation failed: {failure}"; }
            if (Volatile.Read(location: ref m_activation) is not { } activation) { return "activation has not started"; }
            var activating = $"activating '{activation.Row}' for {Seconds(since: activation.Since)} s";

            return ((activation.FinishedStage is { } stage) ? $"{activating} (last finished stage: {stage})" : activating);
        }
        if (!Stalled) { return ""; }
        var stalled = $"simulation is not progressing: no step for {Seconds(since: Volatile.Read(location: ref m_progressTimestamp))} s";

        if (Volatile.Read(location: ref m_pumpMailboxItem) is { } item) {
            return $"{stalled}; the pump is in mailbox work '{MailboxItemName(item: item)}' for {Seconds(since: Volatile.Read(location: ref m_pumpMailboxSince))} s";
        }
        var standing = (Puck.Abstractions.Waiting.HostWaits.DescribeStanding() is { } waits) ? $"; standing waits: {waits}" : "";

        if (Volatile.Read(location: ref m_pumpWait) is { } wait) {
            return $"{stalled}; the step is held on {wait.Reason} for {Seconds(since: wait.Since)} s{standing}";
        }
        return $"{stalled}; the pump is inside no step or mailbox work, so nothing is running it{standing}";

        long Seconds(long since) => (long)m_clock.GetElapsedTime(startingTimestamp: since).TotalSeconds;
    }
    // A posted lambda compiles to a method named for the member that posted it ("<ActivateCoreAsync>b__12_0"), which
    // is the useful name; any other delegate is named by its method.
    private static string MailboxItemName(Action item) {
        var name = item.Method.Name;
        var close = name.IndexOf(value: '>');

        return (((name.Length > 0) && (name[0] == '<') && (close > 1)) ? name[1..close] : name);
    }

    /// <summary>Reads persistence health at a pump boundary. An unresponsive pump fails within the caller's deadline.</summary>
    /// <param name="cancellationToken">Bounds waiting for the simulation thread.</param>
    /// <returns>An empty string when ready, otherwise the reason readiness is withheld.</returns>
    public Task<string> CheckHealthAsync(CancellationToken cancellationToken) => CheckHealthCoreAsync(
        requireAdmission: true,
        cancellationToken
    );
    /// <summary>Checks candidate persistence and pump health without treating private verification as admission. Unlike
    /// public readiness it also fails on authority retention, so an operator and release verification see leaked
    /// storage.</summary>
    public Task<string> CheckPrivateHealthAsync(CancellationToken cancellationToken) => CheckHealthCoreAsync(
        requireAdmission: false,
        cancellationToken
    );

    private async Task<string> CheckHealthCoreAsync(bool requireAdmission, CancellationToken cancellationToken) {
        if (!Live) { return DescribeLiveness(); }
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
                // Retention deletes what a newer checkpoint superseded. Failing it leaks storage, which private health and
                // the operator see, but the world it serves is whole, so public readiness never withdraws players for it.
                if (!requireAdmission && (row.RetentionFailures >= RetentionFailureLimit)) {
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
                : DescribeLiveness()));
        });
        return await completion.Task.WaitAsync(cancellationToken: cancellationToken);
    }
}
