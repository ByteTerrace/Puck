using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldConfiguredExtensions {
    private sealed record Request(WorldExtensionConnection Connection, string Key, string Input, string CacheKey, bool Applied);
    private readonly Dictionary<string, WorldExtensionOperationSnapshot> m_status = new(StringComparer.Ordinal);

    /// <summary>Snapshots authorized request tables and recovery evidence at a closed world boundary, then schedules
    /// bounded asynchronous journal work. Service calls and persistence never run on the simulation pump.</summary>
    /// <param name="completedTick">This authority's completed tick, used only to pace scans.</param>
    /// <remarks>Run on the simulation thread. Old clients remain revoked after replay; restarting live integration
    /// requires a fresh composition. Results are keyed by the original request key, so late completion cannot
    /// overwrite another incarnation's result.</remarks>
    public void Pump(ulong completedTick) {
        if (m_disposed || !m_work.IsCompleted || (m_scanned && completedTick >= m_lastScan &&
            completedTick - m_lastScan < (ulong)m_configuration.ScanEveryTicks)) { return; }
        m_lastScan = completedTick; m_scanned = true;
        try {
            if (m_configuration.World != m_server.Definition.DocumentId) { throw new InvalidOperationException("Configured extension world identity changed."); }
            var requests = new List<Request>();
            m_server.ExecuteAuthorityOperation(() => {
                foreach (var connection in m_configuration.Connections) {
                    try {
                        var client = Client(ParsePrincipal(connection.Client));
                        if (!client.Runtime.IsActive) { continue; }
                        RequireTable(connection.Requests, CellKind.Text);
                        RequireTable(connection.Status, CellKind.Int);
                        if (connection.Results is { } resultRow) { RequireTable(resultRow, CellKind.Text); }
                        var statuses = ReadTable(client, connection.Status, CellKind.Int).ToDictionary(cell => cell.Key, StringComparer.Ordinal);
                        var results = connection.Results is { } resultName
                            ? ReadTable(client, resultName, CellKind.Text).ToDictionary(cell => cell.Key, StringComparer.Ordinal) : null;
                        foreach (var cell in ReadRequests(client, connection)) {
                            var cacheKey = connection.Name.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + connection.Name + ":" + cell.Key;
                            var input = cell.Text ?? "";
                            if (m_observed.TryGetValue(cacheKey, out var previous) && previous != input) {
                                Volatile.Write(ref m_lastFailure, $"Connection '{connection.Name}' reused an immutable request key.");
                                continue;
                            }
                            var applied = m_status.TryGetValue(cacheKey, out var status) && statuses.TryGetValue(cell.Key, out var statusCell) &&
                                statusCell.Value == (long)status.Status && (results is null || (results.TryGetValue(cell.Key, out var resultCell) && resultCell.Text == status.Result));
                            if (applied && status!.Status is WorldExternalOperationStatus.Succeeded or WorldExternalOperationStatus.Failed) { continue; }
                            if (requests.Count >= m_configuration.MaximumEntries) { throw new InvalidOperationException("Connection scan exceeds its request budget."); }
                            requests.Add(new(connection, cell.Key, input, cacheKey, applied));
                        }
                    } catch (Exception exception) {
                        Volatile.Write(ref m_lastFailure, $"Connection '{connection.Name}': {exception.GetType().Name}");
                    }
                }
                if (requests.Count == 0) { return; }
                // The exact observed request and cause are captured together, before any asynchronous boundary.
                var cause = requests.Any(request => !m_observed.ContainsKey(request.CacheKey)) ? m_captureCause() : null;
                m_work = Task.Run(() => ProcessRequestsAsync(requests, cause));
            });
        } catch (Exception exception) {
            // Configuration/state errors are actionable but never forward arbitrary SDK or filesystem messages.
            Volatile.Write(ref m_lastFailure, exception is InvalidOperationException ? exception.Message : exception.GetType().Name);
        }
    }

    /// <summary>Waits for the already-scheduled connection pass to finish journal admission and queue its gameplay
    /// contributions. Does not wait for service completion or apply those contributions.</summary>
    /// <param name="cancellationToken">Cancels this wait, without cancelling admitted operations.</param>
    /// <returns>Completion of the current connection pass.</returns>
    public Task FlushAsync(CancellationToken cancellationToken = default) => m_work.WaitAsync(cancellationToken);

    private async Task ProcessRequestsAsync(IReadOnlyList<Request> requests, string? cause) {
        foreach (var request in requests) {
            if (m_stop.IsCancellationRequested) { return; }
            try {
                var client = Client(ParsePrincipal(request.Connection.Client));
                var handle = await Host.InvokeAsync(client, request.Connection.Operation, request.Key, request.Input,
                    m_stop.Token, () => cause ?? throw new InvalidOperationException("Previously committed operation history is missing.")).ConfigureAwait(false);
                if (m_observed.Count >= m_configuration.MaximumEntries && !m_observed.ContainsKey(request.CacheKey)) {
                    throw new InvalidOperationException("Connection history capacity is exhausted.");
                }
                m_observed[request.CacheKey] = request.Input;
                var status = await handle.ReadAsync(m_stop.Token).ConfigureAwait(false);
                if (status is null || (request.Applied && m_status.TryGetValue(request.CacheKey, out var previous) && previous == status)) { continue; }
                var mutations = new List<WorldMutation> {
                    new WorldMutation.UpsertStateCell(client.Principal, request.Connection.Status, request.Key,
                        (long)status.Status, WorldDocumentWriteKind.Set),
                };
                if (request.Connection.Results is { } results) {
                    mutations.Add(new WorldMutation.UpsertStateCell(client.Principal, results, request.Key, 0,
                        WorldDocumentWriteKind.Set, Text: status.Result));
                }
                client.Submit(new WorldMutation.Batch(client.Principal, mutations));
                // Queued is not applied. Re-check the authorized status observation before suppressing another
                // projection, so a refused contribution can be retried after the host corrects its grants.
                m_status[request.CacheKey] = status;
            } catch (OperationCanceledException) when (m_stop.IsCancellationRequested) { return; }
            catch (Exception exception) { Volatile.Write(ref m_lastFailure, $"Connection '{request.Connection.Name}': {exception.GetType().Name}"); }
        }
    }
}
