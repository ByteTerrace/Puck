using System.Security.Cryptography;
using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldConfiguredExtensions {
    private sealed class EmbeddingConnectionState : IDisposable {
        public required WorldExtensionEmbeddingSettings Settings { get; init; }
        public required IWorldEmbeddingSource Source { get; init; }
        public required WorldExtensionClient Client { get; init; }
        public required StateSpace Space { get; init; }

        public readonly Dictionary<string, LinkedListNode<LruEntry>> CacheLookup = new(comparer: StringComparer.Ordinal);
        public readonly LinkedList<LruEntry> CacheOrder = new();
        public readonly Lock CacheGate = new();

        public Task InFlightTask = Task.CompletedTask;
        public bool InFlight => !InFlightTask.IsCompleted;

        public readonly Dictionary<string, KeyTracking> Tracking = new(comparer: StringComparer.Ordinal);

        public long Selected;
        public long Cached;
        public long Submitted;
        public long Failed;
        public ulong? LastCallTick;
        public string? LastFailure;

        public void Dispose() {
            Source.Dispose();
        }
    }

    private sealed record LruEntry(string Key, StateVector Vector);

    private sealed class KeyTracking {
        public string? LastSubmittedText { get; set; }
        public ulong? LastFailedTick { get; set; }
        public string? LastFailedText { get; set; }
    }

    private readonly List<EmbeddingConnectionState> m_embeddingConnections = [];
    private readonly List<IWorldConfiguredEmbeddingProvider> m_embeddingProviders = [];
    private ulong m_lastEmbeddingScan;
    private bool m_embeddingScanned;

    /// <summary>Gets the diagnostic status for all active embedding connections.</summary>
    public IReadOnlyList<WorldExtensionEmbeddingStatus> Embeddings =>
        m_embeddingConnections.Select(selector: e => new WorldExtensionEmbeddingStatus(
            Cached: Volatile.Read(location: ref e.Cached),
            Dimensions: e.Space.Dimensions,
            Failed: Volatile.Read(location: ref e.Failed),
            InFlight: e.InFlight,
            LastCallTick: e.LastCallTick,
            LastFailure: Volatile.Read(location: ref e.LastFailure),
            Model: e.Space.Model,
            Name: e.Settings.Name,
            Revision: e.Space.Revision,
            Selected: Volatile.Read(location: ref e.Selected),
            Space: e.Settings.Space,
            Submitted: Volatile.Read(location: ref e.Submitted)
        )).ToArray();

    private void PumpEmbeddings(ulong completedTick) {
        if (m_disposed || (m_embeddingConnections.Count == 0)) { return; }
        if (
            m_embeddingScanned &&
            (completedTick >= m_lastEmbeddingScan) &&
            ((completedTick - m_lastEmbeddingScan) < ((ulong)m_configuration.ScanEveryTicks))
        ) {
            return;
        }

        m_lastEmbeddingScan = completedTick;
        m_embeddingScanned = true;

        foreach (var state in m_embeddingConnections) {
            if (!state.Client.Runtime.IsActive) { continue; }
            if (state.InFlight) { continue; }

            IReadOnlyList<WorldObservedCell> requests;
            Dictionary<string, WorldObservedCell> results;
            try {
                requests = ReadTable(client: state.Client, kind: CellKind.Text, name: state.Settings.Requests);
                results = ReadTable(client: state.Client, kind: CellKind.Vector, name: state.Settings.Results)
                    .ToDictionary(keySelector: cell => cell.Key, comparer: StringComparer.Ordinal);
            } catch (Exception ex) {
                Volatile.Write(location: ref state.LastFailure, value: ex.GetType().Name);
                continue;
            }

            var selected = new List<WorldObservedCell>();
            foreach (var cell in requests) {
                if (cell.Hidden) { continue; }
                var key = cell.Key;
                var text = (cell.Text ?? "");

                var hasResult = (results.TryGetValue(key: key, value: out var resultCell) && (resultCell.Vector is not null));
                var tracking = state.Tracking.GetValueOrDefault(key: key);
                var textChanged = ((tracking is null) || !string.Equals(a: tracking.LastSubmittedText, b: text, comparisonType: StringComparison.Ordinal));

                if (!hasResult || textChanged) {
                    if (
                        (tracking?.LastFailedTick is { } failedTick) &&
                        string.Equals(a: tracking.LastFailedText, b: text, comparisonType: StringComparison.Ordinal) &&
                        (completedTick < (failedTick + ((ulong)state.Settings.RetryTicks)))
                    ) {
                        continue;
                    }

                    selected.Add(item: cell);
                    if (selected.Count >= state.Settings.MaximumItems) { break; }
                }
            }

            if (selected.Count == 0) { continue; }

            Interlocked.Add(ref state.Selected, selected.Count);

            if (state.Settings.Status is { } statusTable) {
                var statusZeroMutations = selected.Select(selector: c =>
                    (WorldMutation)new WorldMutation.UpsertStateCell(
                        state.Client.Principal,
                        statusTable,
                        c.Key,
                        0L,
                        WorldDocumentWriteKind.Set
                    )).ToList();
                try {
                    state.Client.Submit(mutation: new WorldMutation.Batch(Principal: state.Client.Principal, Mutations: statusZeroMutations));
                } catch (Exception ex) {
                    Volatile.Write(location: ref state.LastFailure, value: ex.GetType().Name);
                }
            }

            var hits = new List<(string Key, string Text, StateVector Vector)>();
            var misses = new List<(string Key, string Text)>();

            foreach (var cell in selected) {
                var key = cell.Key;
                var text = (cell.Text ?? "");
                var cacheKey = ComputeLruKey(identity: state.Source.Identity, text: text);

                lock (state.CacheGate) {
                    if ((state.Settings.CacheEntries > 0) && state.CacheLookup.TryGetValue(key: cacheKey, value: out var node)) {
                        state.CacheOrder.Remove(node: node);
                        state.CacheOrder.AddFirst(node: node);
                        hits.Add((key, text, node.Value.Vector));
                        Interlocked.Increment(location: ref state.Cached);
                    } else {
                        misses.Add((key, text));
                    }
                }
            }

            state.LastCallTick = completedTick;
            var capturedTick = completedTick;
            state.InFlightTask = Task.Run(function: async () => {
                await ProcessEmbeddingPassAsync(
                    cancellationToken: m_stop.Token,
                    hits: hits,
                    misses: misses,
                    state: state,
                    tick: capturedTick
                ).ConfigureAwait(continueOnCapturedContext: false);
            });
        }
    }

    private static async Task ProcessEmbeddingPassAsync(
        EmbeddingConnectionState state,
        List<(string Key, string Text, StateVector Vector)> hits,
        List<(string Key, string Text)> misses,
        ulong tick,
        CancellationToken cancellationToken
    ) {
        try {
            var answeredMisses = new List<(string Key, string Text, StateVector? Vector, string? Refusal)>();

            if (misses.Count > 0) {
                var batchSize = state.Settings.BatchSize;
                for (var i = 0; i < misses.Count; i += batchSize) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(val1: batchSize, val2: (misses.Count - i));
                    var chunk = misses.GetRange(index: i, count: count);
                    var texts = chunk.Select(selector: m => m.Text).ToArray();

                    IReadOnlyList<EmbeddingAnswer> answers;
                    try {
                        answers = await state.Source.EmbedAsync(texts: texts, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                    } catch (Exception ex) {
                        Volatile.Write(location: ref state.LastFailure, value: ex.GetType().Name);
                        for (var j = 0; j < chunk.Count; j++) {
                            answeredMisses.Add((chunk[j].Key, chunk[j].Text, null, ex.Message));
                        }
                        continue;
                    }

                    for (var j = 0; j < chunk.Count; j++) {
                        var ans = ((j < answers.Count) ? answers[j] : new EmbeddingAnswer(null, "Answer count mismatch"));
                        answeredMisses.Add((chunk[j].Key, chunk[j].Text, ans.Vector, ans.Refusal));
                    }
                }
            }

            var mutations = new List<WorldMutation>();
            var expectations = new List<WorldStateExpectation>();

            foreach (var hit in hits) {
                mutations.Add(item: new WorldMutation.UpsertStateCell(
                    state.Client.Principal,
                    state.Settings.Results,
                    hit.Key,
                    0,
                    WorldDocumentWriteKind.Set,
                    Vector: hit.Vector
                ));
                if (state.Settings.Status is { } statusTable) {
                    mutations.Add(item: new WorldMutation.UpsertStateCell(
                        state.Client.Principal,
                        statusTable,
                        hit.Key,
                        3L,
                        WorldDocumentWriteKind.Set
                    ));
                }
                expectations.Add(item: new WorldStateExpectation(
                    Row: state.Settings.Requests,
                    Key: hit.Key,
                    Value: 0,
                    Kind: CellKind.Text,
                    Text: hit.Text
                ));

                var tr = (state.Tracking.GetValueOrDefault(key: hit.Key) ?? new KeyTracking());
                tr.LastSubmittedText = hit.Text;
                state.Tracking[hit.Key] = tr;
                Interlocked.Increment(location: ref state.Submitted);
            }

            foreach (var miss in answeredMisses) {
                if (miss.Vector is { } vec) {
                    mutations.Add(item: new WorldMutation.UpsertStateCell(
                        state.Client.Principal,
                        state.Settings.Results,
                        miss.Key,
                        0,
                        WorldDocumentWriteKind.Set,
                        Vector: vec
                    ));
                    if (state.Settings.Status is { } statusTable) {
                        mutations.Add(item: new WorldMutation.UpsertStateCell(
                            state.Client.Principal,
                            statusTable,
                            miss.Key,
                            3L,
                            WorldDocumentWriteKind.Set
                        ));
                    }
                    expectations.Add(item: new WorldStateExpectation(
                        Row: state.Settings.Requests,
                        Key: miss.Key,
                        Value: 0,
                        Kind: CellKind.Text,
                        Text: miss.Text
                    ));

                    if (state.Settings.CacheEntries > 0) {
                        var cacheKey = ComputeLruKey(identity: state.Source.Identity, text: miss.Text);
                        lock (state.CacheGate) {
                            if (state.CacheLookup.TryGetValue(key: cacheKey, value: out var existingNode)) {
                                state.CacheOrder.Remove(node: existingNode);
                            } else if (state.CacheLookup.Count >= state.Settings.CacheEntries) {
                                var oldest = state.CacheOrder.Last;
                                if (oldest is not null) {
                                    state.CacheOrder.RemoveLast();
                                    state.CacheLookup.Remove(key: oldest.Value.Key);
                                }
                            }
                            var newNode = new LinkedListNode<LruEntry>(value: new LruEntry(cacheKey, vec));
                            state.CacheOrder.AddFirst(node: newNode);
                            state.CacheLookup[cacheKey] = newNode;
                        }
                    }

                    var tr = (state.Tracking.GetValueOrDefault(key: miss.Key) ?? new KeyTracking());
                    tr.LastSubmittedText = miss.Text;
                    state.Tracking[miss.Key] = tr;
                    Interlocked.Increment(location: ref state.Submitted);
                } else {
                    if (state.Settings.Status is { } statusTable) {
                        mutations.Add(item: new WorldMutation.UpsertStateCell(
                            state.Client.Principal,
                            statusTable,
                            miss.Key,
                            4L,
                            WorldDocumentWriteKind.Set
                        ));
                    }
                    expectations.Add(item: new WorldStateExpectation(
                        Row: state.Settings.Requests,
                        Key: miss.Key,
                        Value: 0,
                        Kind: CellKind.Text,
                        Text: miss.Text
                    ));

                    var tr = (state.Tracking.GetValueOrDefault(key: miss.Key) ?? new KeyTracking());
                    tr.LastFailedTick = tick;
                    tr.LastFailedText = miss.Text;
                    state.Tracking[miss.Key] = tr;
                    Interlocked.Increment(location: ref state.Failed);
                }
            }

            if (mutations.Count > 0) {
                state.Client.Submit(mutation: new WorldMutation.Batch(
                    Principal: state.Client.Principal,
                    Mutations: mutations,
                    ExpectedCells: expectations
                ));
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Drainage / clean cancellation
        } catch (Exception ex) {
            Volatile.Write(location: ref state.LastFailure, value: ex.GetType().Name);
        }
    }

    private static string ComputeLruKey(EmbeddingIdentity identity, string text) {
        var hash = Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: text)));
        return $"{identity.Model}:{identity.Revision}:{identity.Dimensions}:{hash}";
    }
}
