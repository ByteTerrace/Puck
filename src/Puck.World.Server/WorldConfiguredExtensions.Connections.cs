using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldConfiguredExtensions {
    private sealed record Request(WorldExtensionConnection Connection, string Key, string Input, string CacheKey, bool Applied);

    private readonly Dictionary<string, WorldExtensionOperationSnapshot> m_status = new(comparer: StringComparer.Ordinal);

    /// <summary>Snapshots authorized request tables and recovery evidence at a closed world boundary, then schedules
    /// bounded asynchronous journal work. Service calls and persistence never run on the simulation pump.</summary>
    /// <param name="completedTick">This authority's completed tick, used only to pace scans.</param>
    /// <remarks>Run on the simulation thread. Participants run their queued world operations on every call; request
    /// and collection scans run every <c>scanEveryTicks</c>. Old clients remain revoked after replay; restarting live
    /// integration requires a fresh composition. Results are keyed by the original request key, so late completion
    /// cannot overwrite another incarnation's result.</remarks>
    public void Pump(ulong completedTick) {
        if (m_disposed) { return; }
        PumpParticipants(completedTick: completedTick);
        if (
            m_disposed ||
            !m_work.IsCompleted ||
            (m_scanned && (completedTick >= m_lastScan) &&
            ((completedTick - m_lastScan) < ((ulong)m_configuration.ScanEveryTicks)))
        ) { return; }
        m_lastScan = completedTick; m_scanned = true;
        try {
            if (m_configuration.World != m_server.Definition.DocumentId) { throw new InvalidOperationException(message: "Configured extension world identity changed."); }
            m_server.ExecuteAuthorityOperation(operation: () => PumpObservations(tick: completedTick));
            m_server.ExecuteAuthorityOperation(operation: () => PumpEmbeddings(completedTick: completedTick));
            var requests = new List<Request>();

            m_server.ExecuteAuthorityOperation(operation: () => {
                foreach (var connection in m_configuration.Connections) {
                    try {
                        var client = Client(principal: ParsePrincipal(text: connection.Client));

                        if (!client.Runtime.IsActive) { continue; }
                        RequireTable(
                            connection.Requests,
                            CellKind.Text
                        );
                        RequireTable(
                            connection.Status,
                            CellKind.Int
                        );
                        if (connection.Results is { } resultRow) {
                            RequireTable(
                            kind: CellKind.Text,
                            name: resultRow
                        );
                        }
                        var statuses = ReadTable(
                            client,
                            connection.Status,
                            CellKind.Int
                        ).ToDictionary(
                            cell => cell.Key,
                            StringComparer.Ordinal
                        );
                        var results = ((connection.Results is { } resultName)
                            ? ReadTable(
                                client: client,
                                kind: CellKind.Text,
                                name: resultName
                            ).ToDictionary(
                                cell => cell.Key,
                                StringComparer.Ordinal
                            )
                            : null
                        );

                        foreach (var cell in ReadRequests(
                            client: client,
                            connection: connection
                        )) {
                            var cacheKey = ((((connection.Name.Length.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) + ":") + connection.Name) + ":") + cell.Key);
                            var input = (cell.Text ?? "");

                            if (
                                m_observed.TryGetValue(
                                key: cacheKey,
                                value: out var previous
                            ) &&
                                (previous != input)
                            ) {
                                Volatile.Write(
                                    location: ref m_lastFailure,
                                    value: $"Connection '{connection.Name}' reused an immutable request key."
                                );
                                continue;
                            }
                            var applied = (m_status.TryGetValue(
                                key: cacheKey,
                                value: out var status
                            ) && statuses.TryGetValue(
                                key: cell.Key,
                                value: out var statusCell
                            ) &&
                                (statusCell.Value == ((long)status.Status)) && ((results is null) || (results.TryGetValue(
                                key: cell.Key,
                                value: out var resultCell
                            ) && (resultCell.Text == status.Result))));

                            if (
                                applied &&
                                (status!.Status is WorldExternalOperationStatus.Succeeded or WorldExternalOperationStatus.Failed)
                            ) { continue; }
                            if (requests.Count >= m_configuration.MaximumEntries) { throw new InvalidOperationException(message: "Connection scan exceeds its request budget."); }
                            requests.Add(item: new(
                                connection,
                                cell.Key,
                                input,
                                cacheKey,
                                applied
                            ));
                        }
                    } catch (Exception exception) {
                        Volatile.Write(
                            location: ref m_lastFailure,
                            value: $"Connection '{connection.Name}': {exception.GetType().Name}"
                        );
                    }
                }
                if (requests.Count == 0) { return; }
                // The exact observed request and cause are captured together, before any asynchronous boundary.
                var cause = (requests.Any(predicate: request => !m_observed.ContainsKey(key: request.CacheKey))
                    ? m_captureCause()
                    : null
                );

                m_work = Task.Run(function: () => ProcessRequestsAsync(
                    cause: cause,
                    requests: requests
                ));
            });
        } catch (Exception exception) {
            // Configuration/state errors are actionable but never forward arbitrary SDK or filesystem messages.
            Volatile.Write(
                location: ref m_lastFailure,
                value: ((exception is InvalidOperationException)
                ? exception.Message
                : exception.GetType().Name)
            );
        }
    }
    /// <summary>Waits for the already-scheduled connection pass to finish journal admission and queue its gameplay
    /// contributions. Does not wait for service completion or apply those contributions.</summary>
    /// <param name="cancellationToken">Cancels this wait, without cancelling admitted operations.</param>
    /// <returns>Completion of the current connection pass.</returns>
    public async Task FlushAsync(CancellationToken cancellationToken = default) {
        await m_work.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        foreach (var emb in m_embeddingConnections) {
            await emb.InFlightTask.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    private async Task ProcessRequestsAsync(IReadOnlyList<Request> requests, string? cause) {
        foreach (var request in requests) {
            if (m_stop.IsCancellationRequested) { return; }
            try {
                var client = Client(principal: ParsePrincipal(text: request.Connection.Client));
                var handle = await Host.InvokeAsync(
                    client,
                    request.Connection.Operation,
                    request.Key,
                    request.Input,
                    m_stop.Token,
                    () => (cause ?? throw new InvalidOperationException(message: "Previously committed operation history is missing."))
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (
                    (m_observed.Count >= m_configuration.MaximumEntries) &&
                    !m_observed.ContainsKey(key: request.CacheKey)
                ) {
                    throw new InvalidOperationException(message: "Connection history capacity is exhausted.");
                }
                m_observed[request.CacheKey] = request.Input;
                var status = await handle.ReadAsync(cancellationToken: m_stop.Token).ConfigureAwait(continueOnCapturedContext: false);

                if (
                    (status is null) ||
                    (request.Applied && m_status.TryGetValue(
                    key: request.CacheKey,
                    value: out var previous
                ) && (previous == status))
                ) { continue; }
                var mutations = new List<WorldMutation> {
                    new WorldMutation.UpsertStateCell(
                    client.Principal,
                    request.Connection.Status,
                    request.Key,
                    ((long)status.Status),
                    WorldDocumentWriteKind.Set
                ),
                };

                if (request.Connection.Results is { } results) {
                    mutations.Add(item: new WorldMutation.UpsertStateCell(
                        client.Principal,
                        results,
                        request.Key,
                        0,
                        WorldDocumentWriteKind.Set,
                        Text: status.Result
                    ));
                }
                client.Submit(mutation: new WorldMutation.Batch(
                    client.Principal,
                    mutations
                ));
                // Queued is not applied. Re-check the authorized status observation before suppressing another
                // projection, so a refused contribution can be retried after the host corrects its grants.
                m_status[request.CacheKey] = status;
            } catch (OperationCanceledException) when (m_stop.IsCancellationRequested) { return; } catch (Exception exception) {
                Volatile.Write(
                location: ref m_lastFailure,
                value: $"Connection '{request.Connection.Name}': {exception.GetType().Name}"
            );
            }
        }
    }
}
