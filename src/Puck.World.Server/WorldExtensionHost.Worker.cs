namespace Puck.World.Server;

public sealed partial class WorldExtensionHost {
    /// <summary>Starts the owned worker. Repeated calls are harmless. No provider code runs on the simulation pump.</summary>
    public void Start() {
        lock (m_gate) {
            ObjectDisposedException.ThrowIf(
                condition: m_disposed,
                instance: this
            );
            m_worker ??= Task.Run(function: WorkAsync);
        }
    }

    private async Task WorkAsync() {
        while (!m_stop.IsCancellationRequested) {
            try { await RunOnceAsync(cancellationToken: m_stop.Token).ConfigureAwait(continueOnCapturedContext: false); } catch (OperationCanceledException) when (m_stop.IsCancellationRequested) { return; } catch (Exception exception) { Volatile.Write(
                location: ref m_lastFailure,
                value: exception.GetType().Name
            ); }
            try { await Task.Delay(
                m_options.PollInterval,
                m_time,
                m_stop.Token
            ).ConfigureAwait(continueOnCapturedContext: false); } catch (OperationCanceledException) when (m_stop.IsCancellationRequested) { return; }
        }
    }

    /// <summary>Runs one bounded worker pass, also useful to hosts with their own service scheduler. Serializes
    /// with the owned worker. Recovered claims are reconciled, never resent.</summary>
    /// <param name="cancellationToken">Cancels this pass.</param>
    /// <returns>Completion after the selected operations have yielded durable observations.</returns>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default) {
        await m_pump.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        try {
            WorldExtensionClient[] clients;

            lock (m_gate) { ObjectDisposedException.ThrowIf(
                condition: m_disposed,
                instance: this
            ); clients = m_clients.Values.ToArray(); }
            var entries = await m_journal.ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            var now = m_time.GetUtcNow();
            var ready = new List<(WorldExtensionClient Client, WorldExternalOperationEntry Entry)>();

            foreach (var entry in entries) {
                if (entry.Status is WorldExternalOperationStatus.Succeeded or WorldExternalOperationStatus.Failed) { m_due.Remove(key: entry.Operation.Id); continue; }
                var client = clients.FirstOrDefault(predicate: client => entry.Operation.Id.StartsWith(
                    client.Prefix,
                    StringComparison.Ordinal
                ));

                if (
                    (client is null) ||
                    !client.Runtime.IsActive ||
                    !client.Operations.TryGetValue(
                    key: entry.Operation.Binding,
                    value: out var operation
                )
                ) { continue; }
                if (
                    (entry.Status != WorldExternalOperationStatus.Pending) &&
                    !m_due.ContainsKey(key: entry.Operation.Id)
                ) {
                    m_due[entry.Operation.Id] = NextPoll(
                        entry: entry,
                        now: now,
                        operation: operation
                    );
                }
                if (
                    (entry.Status == WorldExternalOperationStatus.Pending) ||
                    (m_due[entry.Operation.Id] <= now)
                ) { ready.Add(item: (client, entry)); }
            }
            if (ready.Count == 0) { return; }
            var count = Math.Min(
                val1: ready.Count,
                val2: m_options.MaximumConcurrentOperations
            );
            var work = new Task[count];

            for (var i = 0; (i < count); i++) {
                var (client, entry) = ready[((m_cursor + i) % ready.Count)];
                work[i] = ProcessAsync(
                    cancellationToken: cancellationToken,
                    client: client,
                    entry: entry
                );
            }
            m_cursor = ((m_cursor + count) % ready.Count);
            await Task.WhenAll(work).ConfigureAwait(continueOnCapturedContext: false);
        } finally { m_pump.Release(); }
    }

    private async Task ProcessAsync(WorldExtensionClient client, WorldExternalOperationEntry entry, CancellationToken cancellationToken) {
        using var timeout = new CancellationTokenSource(
            delay: m_options.OperationTimeout,
            timeProvider: m_time
        );
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token,
            m_stop.Token
        );

        try {
            var result = ((entry.Status == WorldExternalOperationStatus.Pending)
                ? await client.Dispatcher.DispatchAsync(
                    entry.Operation.Id,
                    linked.Token
                ).ConfigureAwait(continueOnCapturedContext: false)
                : await client.Dispatcher.ReconcileAsync(
                    entry.Operation.Id,
                    linked.Token
                ).ConfigureAwait(continueOnCapturedContext: false)
            );
            // Concurrent network calls finish independently; scheduling state is shared only under this lock.
            lock (m_due) { m_due[entry.Operation.Id] = NextPoll(
                client.Operations[entry.Operation.Binding],
                result,
                m_time.GetUtcNow()
            ); }
        } catch (Exception exception) when (((exception is not OperationCanceledException) || !cancellationToken.IsCancellationRequested)) {
            Volatile.Write(
                location: ref m_lastFailure,
                value: exception.GetType().Name
            );
            lock (m_due) { m_due[entry.Operation.Id] = (m_time.GetUtcNow() + m_options.PollInterval); }
        }
    }
    private DateTimeOffset NextPoll(WorldExtensionOperation operation, WorldExternalOperationEntry entry, DateTimeOffset now) {
        var delay = (operation.PollingDelay?.Invoke(
            new(
                entry.Status,
                entry.Result
            ),
            now
        ) ?? m_options.PollInterval);

        if (delay < m_options.PollInterval) { delay = m_options.PollInterval; }
        return ((delay >= (DateTimeOffset.MaxValue - now))
            ? DateTimeOffset.MaxValue
            : (now + delay)
        );
    }

    /// <summary>Revokes clients, cancels worker calls, and waits for the owned worker. Does not dispose borrowed
    /// providers or erase durable operations. In-process providers must cooperate with cancellation.</summary>
    public async ValueTask DisposeAsync() {
        Task? worker;

        lock (m_gate) {
            if (m_disposed) { return; }
            m_disposed = true;
            foreach (var client in m_clients.Values) { client.Dispose(); }
            worker = m_worker;
        }
        await m_stop.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
        if (worker is not null) { await worker.ConfigureAwait(continueOnCapturedContext: false); }
        await m_pump.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
        m_pump.Release();
        // Semaphores remain valid for callers already completing admission; revoked clients cannot begin anew.
    }
}
