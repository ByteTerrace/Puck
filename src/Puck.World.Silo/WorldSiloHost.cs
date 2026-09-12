using System.Collections.Concurrent;
using System.Security.Cryptography;
using Puck.Abstractions.Machines;
using Puck.Attestation;
using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Machines;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>
/// The silo's own <see cref="IWorldAuthorityHost"/> — one boot-free <see cref="WorldInstanceHost"/>, one activation
/// mailbox drained at the tick thread's master boundary, and the per-row bookkeeping (federation identity, adjacency
/// resolver, checkpoint outcomes) a hosted row needs beyond what <see cref="WorldInstance"/> itself carries.
/// <see cref="ActivateAsync"/>/<see cref="DeactivateAsync"/> perform their own store I/O off the tick thread and post
/// a short mailbox action to touch the host's registry; <see cref="CheckpointNowAsync"/> and
/// <see cref="TryDescribeRow"/> likewise cross onto (or read only from) the tick thread rather than touching the
/// registry from a foreign thread unguarded.
/// </summary>
public sealed partial class WorldSiloHost : IWorldAuthorityHost, IWorldWaitGateResolver {
    private sealed class RowBookkeeping {
        public required WorldAdjacencyFields Adjacencies { get; init; }
        public required WorldConsoleWaitGate Gate { get; init; }
        public required WorldAuthorityFence Fence { get; init; }
        public long PublishedJournalSequence = -1;
        public bool PersistenceBlocked;
        public bool Released;
        public bool Initializing = true;

        public int CheckpointDeferredCount;

        // All checkpoint and journal publications share this queue. A snapshot is enqueued at capture time,
        // before later mutations, so its coverage watermark can never include a mutation absent from its bytes.
        // Only the pump replaces the tail; continuations update PublishedJournalSequence in queue order.
        public Task JournalTail = Task.CompletedTask;

        public int PendingJournalAppends;
        public long CheckpointTimestamp;
        public long JournalTimestamp;
        public bool JournalFailed;
        public ulong JournalFailureTick;

        public string LastCheckpointOutcome = "never captured";
        public string LastJournalOutcome = "none yet";
        public long LastCheckpointOrdinal = -1;

        public ulong LastCheckpointTick;

        public required bool Pinned { get; init; }
    }

    private readonly IObjectBlobStore m_blobStore;

    private readonly WorldAuthorityCheckpointCadenceCounter m_cadence = new();

    private readonly WorldSiloDefinition m_definition;
    private readonly WorldMachineCatalog m_machineCatalog;
    private readonly IMachineContentAdmissionPolicy m_contentAdmissionPolicy;
    private readonly string m_catalogFingerprint;
    private readonly Guid m_machineId;

    private readonly ConcurrentQueue<Action> m_mailbox = new();

    private readonly SiloConsoleRouting m_routing;

    private readonly Dictionary<string, RowBookkeeping> m_rows = new(comparer: StringComparer.Ordinal);

    private readonly ObjectStorageTarget m_storageTarget;
    private readonly IWorldAuthorityStore m_store;

    private ulong m_masterElapsedEngineTicks;
    private int m_draining;

    private readonly Lock m_drainLock = new();
    private readonly SemaphoreSlim m_activationGate = new(1, 1);

    private Task? m_drainTask;

    private readonly List<Task> m_checkpointUploads = [];
    private readonly List<Task> m_persistenceOperations = [];

    private bool m_ready;
    private readonly Func<WorldSiloExtension, Puck.Networking.IAuthenticator, Puck.Networking.IAuthenticator>? m_authentication;

    /// <summary>Initializes the silo host over a validated document and its resolved blob store.</summary>
    /// <param name="definition">The validated silo document.</param>
    /// <param name="blobStore">The composed blob store.</param>
    /// <param name="storageTarget">The target supplied by the selected persistence extension.</param>
    /// <param name="routing">Where every admitted row's own tagged console session is registered and retired.</param>
    /// <param name="authentication">The composition root's installed authentication-provider resolver.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <param name="machineCatalog">The immutable machine catalog selected by this silo host.</param>
    /// <param name="contentAdmissionPolicy">The captured host policy for machine content; defaults to the open local policy.</param>
    public WorldSiloHost(WorldSiloDefinition definition, IObjectBlobStore blobStore, SiloConsoleRouting routing, ObjectStorageTarget storageTarget, WorldMachineCatalog? machineCatalog = null,
        Func<WorldSiloExtension, Puck.Networking.IAuthenticator, Puck.Networking.IAuthenticator>? authentication = null, IMachineContentAdmissionPolicy? contentAdmissionPolicy = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: blobStore);
        ArgumentNullException.ThrowIfNull(argument: routing);
        ArgumentNullException.ThrowIfNull(argument: storageTarget);
        machineCatalog ??= new WorldMachineCatalog([]);

        m_definition = definition;
        m_machineCatalog = machineCatalog;
        m_contentAdmissionPolicy = contentAdmissionPolicy ?? MachineContentAdmissionPolicy.Open(MachineAssetAdmission.Allow);
        m_catalogFingerprint = machineCatalog.CompositionFingerprint;
        m_authentication = authentication;
        m_blobStore = blobStore;
        m_routing = routing;
        m_storageTarget = storageTarget;
        m_store = new WorldAuthorityBlobStore(
            store: blobStore,
            target: m_storageTarget
        );
        m_machineId = ResolveMachineId(stateDir: definition.StateDir);
        Instances = new WorldInstanceHost(
            admitsSpawn: false,
            applicationStopping: CancellationToken.None,
            machineHostFactory: MachineHostFactory,
            machineId: m_machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: definition.StateDir,
            machineCatalog: m_machineCatalog,
            catalogFingerprint: m_catalogFingerprint
        );
    }

    /// <summary>Gets the immutable machine catalog selected for this silo.</summary>
    public WorldMachineCatalog MachineCatalog => m_machineCatalog;
    /// <summary>Gets the stable fingerprint of the selected machine metadata.</summary>
    public string MachineCatalogFingerprint => m_catalogFingerprint;
    /// <summary>Whether all pinned worlds have established their durable startup baseline.</summary>
    public bool Ready { get => Volatile.Read(location: ref m_ready); internal set => Volatile.Write(location: ref m_ready, value: value); }
    /// <summary>Whether the host has stopped stepping worlds for retirement.</summary>
    public bool IsDraining => (Volatile.Read(location: ref m_draining) != 0);

    /// <summary>Freezes all worlds at one pump boundary and durably saves them before retirement.</summary>
    /// <param name="ct">This caller's retirement deadline. A caller may retry a cancelled or failed save.</param>
    /// <returns>Completion after the final checkpoints are durable. Concurrent callers share an attempt, but each
    /// observes its own deadline. A cancelled attempt can be retried by a caller with time remaining.</returns>
    /// <remarks>After ingress closes, worlds remain frozen even when saving fails. Reopening requires a fresh host;
    /// a retry captures the same frozen state. Only successful retirement is cached permanently.</remarks>
    public async Task DrainAsync(CancellationToken ct) {
        while (true) {
            ct.ThrowIfCancellationRequested();
            Task attempt;

            lock (m_drainLock) {
                if ((m_drainTask is null) || (m_drainTask.IsCompleted && !m_drainTask.IsCompletedSuccessfully)) {
                    ObserveCompleted(operations: m_persistenceOperations);
                    m_drainTask = DrainCoreAsync(ct: ct, pendingOperations: m_persistenceOperations.ToArray());
                }
                attempt = m_drainTask;
            }
            try { await attempt.WaitAsync(cancellationToken: ct); return; } catch (OperationCanceledException) when ((!ct.IsCancellationRequested && attempt.IsCanceled)) { }
        }
    }

    private async Task DrainCoreAsync(CancellationToken ct, Task[] pendingOperations) {
        // Accepted reloads need a subsequent simulation step. Let them settle before freezing the pump.
        await ObservePersistenceAsync(Task.WhenAll(pendingOperations), ct);
        await ObservePersistenceAsync(Task.WhenAll(m_pendingReleases.Values.Select(static release => release.Applied)), ct);
        var capture = new TaskCompletionSource<List<(WorldAuthorityIdentity Identity, RowBookkeeping Bookkeeping, Task<WorldAuthorityStoreOutcome> Save)>>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        m_mailbox.Enqueue(() => {
            try {
                ct.ThrowIfCancellationRequested();
                Volatile.Write(location: ref m_draining, value: 1);
                var rows = new List<(WorldAuthorityIdentity, RowBookkeeping, Task<WorldAuthorityStoreOutcome>)>();

                foreach (var declared in m_definition.Worlds) {
                    if (!Instances.TryGet(declared.World.Value, out var row) || (row is null)) { continue; }
                    var bookkeeping = m_rows[declared.World.Value];
                    if (bookkeeping.Released) { continue; }
                    row.Door?.SuspendIngress();
                    row.Server.FreezeForRetirement();
                    if (!TryCaptureRow(encoded: out var encoded, outcome: out var outcome, row: row, tick: out var tick)) {
                        throw new InvalidOperationException(message: $"Cannot retire '{declared.World}': {outcome}");
                    }
                    var identity = new WorldAuthorityIdentity(Owner: declared.Owner, World: declared.World);
                    rows.Add(item: (identity, bookkeeping, QueueCheckpoint(encoded, identity, row.Name, tick, bookkeeping, ct)));
                }
                capture.TrySetResult(result: rows);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { capture.TrySetCanceled(cancellationToken: ct); } catch (Exception ex) { capture.TrySetException(exception: ex); }
        });
        var captured = await capture.Task.WaitAsync(cancellationToken: ct);

        foreach (var row in captured) {
            var result = await row.Save.WaitAsync(ct);

            ct.ThrowIfCancellationRequested();
            if (!result.Ok) { throw new IOException(message: $"Final checkpoint for '{row.Identity}' failed: {result.Detail}"); }
            var release = await m_store.ReleaseActivationAsync(row.Identity, row.Bookkeeping.Fence, ct);
            if (!release.Ok) { throw new IOException($"Authority release for '{row.Identity}' failed: {release.Detail}"); }
            row.Bookkeeping.Released = true;
        }
        Console.Error.WriteLine(value: $"[silo.drain: saved {captured.Count} worlds]");
    }
    // Historical persistence failures are observable, but the final frozen checkpoint supersedes them.
    private static async Task ObservePersistenceAsync(Task operation, CancellationToken ct) {
        try { await operation.WaitAsync(cancellationToken: ct); } catch (Exception error) when ((operation.IsCompleted && !ct.IsCancellationRequested)) {
            Console.Error.WriteLine(value: $"[silo.persistence: {error.Message}]");
        }
    }
    private static void ObserveCompleted(List<Task> operations) {
        operations.RemoveAll(match: static task => {
            if (!task.IsCompleted) { return false; }
            if (task.Exception is { } error) { Console.Error.WriteLine(value: $"[silo.persistence: {error.GetBaseException().Message}]"); }
            return true;
        });
    }
    // Runtime creation and replay use the same selected catalog and policy as document admission.
    private IWorldMachineHost MachineHostFactory(IReadOnlyList<WorldScreen> screens, IEnumerable<IMachineEngine> engines, string? documentPath, WorldOutputHub? narrationHub) => new WorldMachineHost(
        screens: screens,
        catalog: m_machineCatalog,
        documentPath: documentPath,
        narrationHub: narrationHub,
        contentAdmissionPolicy: m_contentAdmissionPolicy
    );

    /// <summary>Gets the silo document this host was built from.</summary>
    public WorldSiloDefinition Definition => m_definition;
    /// <summary>Gets the boot-free host engine every activated row is admitted into.</summary>
    public WorldInstanceHost Instances { get; }
    /// <summary>Gets the fastest active, unpaused, nonzero-rate row's authored rate — 0 while nothing is
    /// active.</summary>
    public uint MasterRateHz { get; private set; }

    /// <inheritdoc/>
    public WorldConsoleWaitGate GateFor(WorldInstance instance) => m_rows[instance.Name].Gate;
    /// <summary>Finds the declared row naming <paramref name="key"/> — either <c>owner/{oid}/{world}</c> verbatim or
    /// the bare world id (resolves only when the id is declared exactly once).</summary>
    /// <param name="key">The key text.</param>
    /// <param name="identity">The resolved identity, on success.</param>
    /// <param name="reason">Why no row resolved, on failure.</param>
    /// <returns><see langword="true"/> when exactly one declared row matches.</returns>
    public bool TryResolveKey(string key, out WorldAuthorityIdentity identity, out string reason) {
        if (key.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "owner/"
        )) {
            var parts = key.Split(separator: '/');

            if (
                (parts.Length == 3) &&
                Guid.TryParse(
                input: parts[1],
                result: out var owner
            ) &&
                SafeName.TryParse(
                candidate: parts[2],
                name: out var world,
                reason: out _
            ) &&
                (FindWorldRow(name: world.Value) is { } row) &&
                (row.Owner == owner)
            ) {
                identity = new WorldAuthorityIdentity(
                    Owner: owner,
                    World: world
                );
                reason = string.Empty;

                return true;
            }

            identity = default;
            reason = $"'{key}' names no declared row";

            return false;
        }

        var matches = m_definition.Worlds.Where(predicate: candidate => string.Equals(
            a: candidate.World.Value,
            b: key,
            comparisonType: StringComparison.Ordinal
        )).ToArray();

        if (matches.Length == 1) {
            identity = new WorldAuthorityIdentity(
                Owner: matches[0].Owner,
                World: matches[0].World
            );
            reason = string.Empty;

            return true;
        }

        identity = default;
        reason = ((matches.Length == 0)
            ? $"'{key}' names no declared world id"
            : $"'{key}' is ambiguous — {matches.Length} declared rows share that world id; use owner/{{oid}}/{key}"
        );

        return false;
    }
    /// <summary>Publishes a composed definition to the hosted store under the identity's own key — the one writer of
    /// a hosted <c>definition.json</c>.</summary>
    /// <param name="identity">The row to publish under.</param>
    /// <param name="composed">The composed definition.</param>
    /// <param name="ct">A token to observe.</param>
    /// <returns>The write outcome.</returns>
    public Task<WorldAuthorityStoreOutcome> PublishDefinitionAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken ct) {
        lock (m_drainLock) {
            if (m_drainTask is not null) { return Task.FromResult(WorldAuthorityStoreOutcome.Failed("The silo is retiring.")); }
            ObserveCompleted(m_persistenceOperations);
            var operation = PublishDefinitionCoreAsync(identity, composed, ct);
            m_persistenceOperations.Add(operation);
            return operation;
        }
    }

    private static Guid ResolveMachineId(string stateDir) {
        Directory.CreateDirectory(path: stateDir);

        var path = Path.Combine(
            path1: stateDir,
            path2: "silo-machine.id"
        );

        try {
            if (
                File.Exists(path: path) &&
                Guid.TryParse(
                input: File.ReadAllText(path: path).Trim(),
                result: out var stored
            ) &&
                (stored != Guid.Empty)
            ) {
                return stored;
            }

            var created = Guid.NewGuid();

            File.WriteAllText(
                contents: created.ToString(format: "D"),
                path: path
            );

            return created;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[silo] machine id is session-only ({exception.Message})");

            return Guid.NewGuid();
        }
    }
    private static string RowKey(WorldAuthorityIdentity identity) => $"owner/{identity.Owner:D}/{identity.World}";
    private bool AllAdjacenciesPrimed(WorldInstance row) {
        if (!m_rows.TryGetValue(
            key: row.Name,
            value: out var bookkeeping
        )) {
            return true;
        }

        foreach (var adjacency in (row.Server.Definition.Adjacencies ?? [])) {
            if (
                bookkeeping!.Adjacencies.TryResolve(
                adjacencyName: adjacency.Name.Value,
                neighbour: out var neighbour
            ) &&
                (neighbour is { SnapshotRevision: < 1 })
            ) {
                return false;
            }
        }

        return true;
    }
    private void CaptureRowsArmedByCadence() {
        foreach (var name in Instances.Names) {
            if (
                !Instances.TryGet(
                instance: out var row,
                name: name
            ) ||
                (row is not { AwaitingMirrors: false })
            ) {
                continue;
            }

            if (TryCaptureRow(
                encoded: out var encoded,
                outcome: out var outcome,
                row: row,
                tick: out var tick
            )) {
                // Serialize+upload off the tick thread from the captured buffer — fire-and-forget from here; the
                // outcome lands in this row's bookkeeping whenever the write completes.
                var captured = encoded;
                var capturedTick = tick;

                ObserveCompleted(operations: m_checkpointUploads);
                m_checkpointUploads.Add(item: QueueCheckpoint(
                    encoded: captured,
                    identity: new WorldAuthorityIdentity(
                        Owner: (FindWorldRow(name: name)?.Owner ?? Guid.Empty),
                        World: (SafeName.TryParse(candidate: name, name: out var world, reason: out _) ? world : default)
                    ),
                    tick: capturedTick,
                    worldId: name,
                    bookkeeping: m_rows[name],
                    cancellationToken: CancellationToken.None
                ));
            } else {
                if (m_rows.TryGetValue(
                    key: name,
                    value: out var bookkeeping
                )) {
                    bookkeeping.CheckpointDeferredCount++;
                    bookkeeping.LastCheckpointOutcome = outcome;
                }
            }
        }

        m_cadence.Clear();
    }
    private WorldSiloWorldRow? FindWorldRow(string name) => m_definition.Worlds.FirstOrDefault(predicate: row => string.Equals(
        a: row.World.Value,
        b: name,
        comparisonType: StringComparison.Ordinal
    ));
    private WorldSiloWorldRow? FindWorldRow(WorldAuthorityIdentity identity) => m_definition.Worlds.FirstOrDefault(predicate: row => ((row.Owner == identity.Owner) && string.Equals(
        a: row.World.Value,
        b: identity.World.Value,
        comparisonType: StringComparison.Ordinal
    )));
    private void RecomputeMasterRateHz() {
        var fastest = 0U;

        foreach (var name in Instances.Names) {
            if (
                !Instances.TryGet(
                instance: out var row,
                name: name
            ) ||
                (row is not { IsPaused: false, AwaitingMirrors: false }) || row.Server.IsRetiring
            ) {
                continue;
            }

            var rate = row.Server.Definition.SimulationRateHz;

            if (
                (rate > 0) &&
                (((uint)rate) > fastest)
            ) {
                fastest = ((uint)rate);
            }
        }

        MasterRateHz = fastest;
    }
    private void SweepAwaitingMirrors() {
        foreach (var name in Instances.Names) {
            if (
                Instances.TryGet(
                instance: out var row,
                name: name
            ) &&
                (row is { AwaitingMirrors: true }) && !row.Server.IsRetiring &&
                m_rows.TryGetValue(name, out var bookkeeping) && !bookkeeping.Initializing &&
                AllAdjacenciesPrimed(row: row)
            ) {
                Instances.ReleaseHold(row: row);
            }
        }
    }
    private bool TryBuildFederationIdentity(WorldDefinition definition, WorldSiloWorldRow worldRow, Func<IReadOnlyList<WorldAdmissionEntry>?> trustEntries, out WorldFederationIdentity federation, out string reason) {
        if (string.IsNullOrEmpty(value: definition.Host.Authority)) {
            federation = default;
            reason = $"'{worldRow.World}' loaded with no host.authority — a hosted row without one cannot sign or be addressed";

            return false;
        }

        try {
            var pkcs8 = File.ReadAllBytes(path: worldRow.Federation.KeyFile);
            // The one key-import path in the tree: refuses trailing bytes and any curve other than the one the
            // signing algorithm names, so a wrong key file fails here by name rather than at the first signed claim.
            var key = AttestationKeys.ImportPkcs8PrivateKey(
                algorithm: AttestationAlgorithms.EcdsaP256Sha256,
                pkcs8: pkcs8
            );

            var subject = definition.Host.Authority;

            federation = new WorldFederationIdentity(
                Authenticator: WrapAuthentication(worldRow, new WorldAttestedAuthenticator(
                    oracle: new LocalKeySigningOracle(
                        key: key,
                        subject: subject,
                        validity: WorldAttestedAuthenticator.MaximumClaimAge
                    ),
                    trustEntries: trustEntries
                )),
                Subject: subject,
                Network: new WorldPeerNetwork(identityFile: worldRow.Federation.KeyFile)
            );
            reason = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)) {
            federation = default;
            reason = $"'{worldRow.World}' federation.keyFile could not be read — {exception.Message}";

            return false;
        }
    }
    private Puck.Networking.IAuthenticator WrapAuthentication(WorldSiloWorldRow row, Puck.Networking.IAuthenticator federation) =>
        row.Federation.Authentication is { } selection
            ? (m_authentication ?? throw new InvalidOperationException("No authentication provider registry is installed."))(selection, federation)
            : federation;

    private bool TryCaptureRow(WorldInstance row, out byte[] encoded, out string outcome, out ulong tick) {
        var hostRow = Instances.CaptureRow(row: row);

        if (!row.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: hostRow,
            reason: out var reason
        )) {
            encoded = [];
            outcome = reason;
            tick = 0;

            return false;
        }

        encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);
        outcome = "ok";
        tick = row.CompletedTicks;

        return true;
    }
    private void RecordCheckpointFailure(string worldId, RowBookkeeping bookkeeping, Exception error) {
        m_mailbox.Enqueue(item: () => {
            if (m_rows.TryGetValue(key: worldId, value: out var current) && ReferenceEquals(current, bookkeeping)) {
                bookkeeping.LastCheckpointOutcome = $"failed ({error.Message})";
            }
        });
    }
    private Task<WorldAuthorityStoreOutcome> QueueCheckpoint(byte[] encoded, WorldAuthorityIdentity identity, string worldId, ulong tick,
        RowBookkeeping bookkeeping, CancellationToken cancellationToken) {
        var previous = bookkeeping.JournalTail;
        var queued = UploadCheckpointAsync(encoded, identity, worldId, tick, bookkeeping, previous, cancellationToken);
        bookkeeping.JournalTail = queued;
        return queued;
    }
    private async Task<WorldAuthorityStoreOutcome> UploadCheckpointAsync(byte[] encoded, WorldAuthorityIdentity identity, string worldId, ulong tick,
        RowBookkeeping bookkeeping, Task previous, CancellationToken cancellationToken) {
        try {
            await ObservePersistenceAsync(previous, cancellationToken);
            if (bookkeeping.PersistenceBlocked) { return WorldAuthorityStoreOutcome.RecoveryRequired("This activation must recover before publishing again."); }
            var outcome = await m_store.WriteCheckpointAsync(
                cancellationToken: cancellationToken,
                encoded: encoded,
                identity: identity,
                tick: tick,
                fence: bookkeeping.Fence,
                capturedJournalSequence: bookkeeping.PublishedJournalSequence
            );
            ObservePublication(bookkeeping, outcome);

            m_mailbox.Enqueue(item: () => {
                if (m_rows.TryGetValue(
                    key: worldId,
                    value: out var current
                ) && ReferenceEquals(current, bookkeeping)) {
                    bookkeeping.LastCheckpointOutcome = (outcome.Ok
                        ? "ok"
                        : $"failed ({outcome.Detail})"
                    );

                    if (outcome.PublishedRoot is { } published) {
                        bookkeeping.CheckpointTimestamp = m_clock.GetTimestamp();
                        bookkeeping.LastCheckpointOrdinal = published.Root.CheckpointOrdinal;
                        bookkeeping.LastCheckpointTick = published.Root.CheckpointTick;
                    }
                }
            });
            return outcome;
        } catch (Exception error) {
            RecordCheckpointFailure(error: error, worldId: worldId, bookkeeping: bookkeeping);
            throw;
        }
    }
    private async Task AppendJournalEntryAsync(WorldAuthorityIdentity identity, string worldId, ulong tick, byte[] encoded, RowBookkeeping bookkeeping) {
        try {
            var outcome = bookkeeping.PersistenceBlocked
                ? WorldAuthorityStoreOutcome.RecoveryRequired("This activation must recover before publishing again.")
                : await m_store.AppendJournalAsync(
                cancellationToken: CancellationToken.None,
                entry: new WorldMutationJournalEntry(Encoded: encoded, Tick: tick),
                identity: identity,
                fence: bookkeeping.Fence
            );
            ObservePublication(bookkeeping, outcome);

            m_mailbox.Enqueue(item: () => {
                if (m_rows.TryGetValue(
                    key: worldId,
                    value: out var current
                ) && ReferenceEquals(current, bookkeeping)) {
                    bookkeeping.PendingJournalAppends--;
                    bookkeeping.JournalTimestamp = m_clock.GetTimestamp();
                    bookkeeping.JournalFailed |= !outcome.Ok;
                    if (!outcome.Ok) { bookkeeping.JournalFailureTick = tick; }
                    bookkeeping.LastJournalOutcome = (outcome.Ok
                        ? "ok"
                        : $"failed ({outcome.Detail})"
                    );
                }
            });
        } catch (Exception error) {
            m_mailbox.Enqueue(item: () => {
                if (m_rows.TryGetValue(key: worldId, value: out var current) && ReferenceEquals(current, bookkeeping)) {
                    bookkeeping.PendingJournalAppends--;
                    bookkeeping.JournalFailed = true;
                    bookkeeping.JournalFailureTick = tick;
                    bookkeeping.LastJournalOutcome = $"failed ({error.Message})";
                }
            });
            throw;
        }
    }
    // Called from WorldServer.MutationJournalTap, always on the tick thread — the one writer of JournalTail.
    private void ScheduleJournalAppend(string worldId, WorldAuthorityIdentity identity, ulong tick, WorldMutation mutation, WorldServer source) {
        if (!m_rows.TryGetValue(
            key: worldId,
            value: out var bookkeeping
        )) {
            return;
        }

        if (!Instances.TryGet(worldId, out var active) || active is null || !ReferenceEquals(active.Server, source)) { return; }

        if (!WorldSubmissionCodec.TryEncodeCommittedMutation(
            bytes: out var encoded,
            failure: out var failure,
            mutation: mutation
        )) {
            bookkeeping.JournalFailed = true;
            bookkeeping.JournalFailureTick = tick;
            bookkeeping.LastJournalOutcome = $"failed (encoding: {failure})";
            Console.Error.WriteLine(value: $"[silo.journal: '{RowKey(identity: identity)}' a mutation would not re-encode for the durable journal ({failure}) — this tick's mutation is unrecoverable after a restart with no later checkpoint]");

            return;
        }

        if (bookkeeping.PendingJournalAppends == 0) { bookkeeping.JournalTimestamp = m_clock.GetTimestamp(); }
        bookkeeping.PendingJournalAppends++;
        bookkeeping.JournalTail = bookkeeping.JournalTail.ContinueWith(
            continuationFunction: _ => AppendJournalEntryAsync(
                encoded: encoded,
                identity: identity,
                tick: tick,
                worldId: worldId,
                bookkeeping: bookkeeping
            ),
            scheduler: TaskScheduler.Default
        ).Unwrap();
    }

    /// <inheritdoc/>
    public Task<bool> ActivateAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        lock (m_drainLock) {
            if (m_drainTask is not null) { return Task.FromResult(false); }
            ObserveCompleted(m_persistenceOperations);
            var operation = ActivateSerializedAsync(identity, ct);
            m_persistenceOperations.Add(operation);
            return operation;
        }
    }
    private async Task<bool> ActivateSerializedAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        if (FindWorldRow(identity) is null) { return false; }
        await m_activationGate.WaitAsync(ct);
        try {
            var existing = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            m_mailbox.Enqueue(() => existing.TrySetResult(Instances.TryGet(identity.World.Value, out var row) && row is not null
                ? !row.Server.IsRetiring : null));
            if (await existing.Task.WaitAsync(ct) is { } active) { return active; }
            return await ActivateCoreAsync(identity, ct);
        } finally { m_activationGate.Release(); }
    }
    private async Task<bool> ActivateCoreAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        if (IsDraining) { return false; }
        if (FindWorldRow(identity: identity) is not { } worldRow) {
            Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (not declared in this silo's document)]");

            return false;
        }

        var acquired = await m_store.AcquireActivationAsync(identity, ct);
        if (acquired is not { } fence) { return false; }
        var admitted = false;
        try {
            var recovery = await m_store.LoadRecoveryAsync(identity, ct)
                ?? throw new InvalidDataException("The acquired authority has no recovery root.");
            if (recovery.Root.Root.Epoch != fence.Epoch || recovery.Root.Root.FenceToken != fence.Token) { return false; }
            var origin = new WorldHostedOrigin(
                owner: identity.Owner,
                store: m_blobStore,
                target: m_storageTarget,
                world: identity.World
            );

            var definition = recovery.Definition;
            if (definition is null) {
                Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (no published definition)]");

                return false;
            }

            // The silo genuinely cannot mount an addon guest — refuse an initial candidate that names one enabled BY
            // NAME, before any server exists to install it, rather than accepting the document and running it addon-
            // less. WorldNoAddonHost.TryPrepare is the identical door the attached live host below enforces; reusing it
            // here means this refusal and that one can never disagree.
            if (!new WorldNoAddonHost().TryPrepare(
                candidate: definition!,
                current: null,
                plan: out _,
                reason: out var addonReason
            )) {
                Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (addon {addonReason})]");

                return false;
            }

            var checkpointBlob = recovery.Checkpoint;
            WorldAuthorityCheckpoint? checkpoint = null;

            if (checkpointBlob is { } blob) {
                if (!WorldAuthorityCheckpointCodec.TryDecode(
                    bytes: blob.Encoded.Span,
                    checkpoint: out checkpoint,
                    reason: out var decodeReason
                )) {
                    Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (checkpoint decode: {decodeReason})]");

                    return false;
                }
            }

            var ownedWorldsDirectory = Path.Combine(
                path1: m_definition.StateDir,
                path2: "hosted",
                path3: identity.World.Value,
                path4: "owned-worlds"
            );
            var profiles = new WorldOwnedWorlds(
                directory: ownedWorldsDirectory,
                machineId: m_machineId,
                neighbours: origin.Neighbours,
                template: definition!,
                machineCatalog: m_machineCatalog,
                catalogFingerprint: m_catalogFingerprint
            );
            var machines = new WorldMachineHost(
                catalog: m_machineCatalog,
                screens: definition!.Screens,
                contentAdmissionPolicy: m_contentAdmissionPolicy
            );
            WorldServer server;
            WorldPopulation population;

            if (checkpoint is { } cp) {
                (server, population) = WorldServer.FromCheckpoint(
                    checkpoint: cp,
                    instanceIdentity: identity.World.Value,
                    machines: machines,
                    profiles: profiles
                );
            } else {
                population = new WorldPopulation(definition: definition);
                server = new WorldServer(
                    definition: definition,
                    envelope: new WorldRenderEnvelope(),
                    instanceIdentity: identity.World.Value,
                    machines: machines,
                    population: population,
                    profiles: profiles
                );
            }

            server.Neighbours = origin.Neighbours;
            // Attached BEFORE journal-tail replay and live admission. TryApplyMutation and ApplyRebuild both refuse an
            // addon-affecting operation outright when NO host is attached at all, so this is not what stops those two —
            // it is what closes world.undo's own gap: WorldServer.AddonsCanPrepare treats a null m_addons as vacuously
            // nothing to check, so an undo that restores an enabled addon row would otherwise install silently on a
            // server with no host attached at all. WorldNoAddonHost.TryPrepare refuses that row BY NAME instead, the
            // identical door the initial-candidate check above already used, so the two refusals can never disagree.
            server.AttachAddons(runtime: new WorldNoAddonHost());

            {
                foreach (var entry in recovery.Journal.Entries) {
                    if (
                        !WorldSubmissionCodec.TryDecodeCommittedMutation(
                        bytes: entry.Encoded.Span,
                        failure: out var failure,
                        mutation: out var mutation
                    ) ||
                        (mutation is null)
                    ) {
                        Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (journal decode: {failure})]");
                        machines.Dispose();

                        return false;
                    }

                    if (!server.TryApplyJournalTailMutation(
                        mutation: mutation,
                        tick: entry.Tick
                    )) {
                        Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (journal replay rejected a recorded mutation)]");
                        machines.Dispose();

                        return false;
                    }
                }
            }

            // Wired AFTER the tail replay above: a replayed entry is already durable (it came FROM the store), so
            // re-journaling it here would append a duplicate. Every mutation applied from here on — this row's live
            // operation — is new and gets appended.
            server.MutationJournalTap = (tick, mutation) => ScheduleJournalAppend(
                identity: identity,
                mutation: mutation,
                tick: tick,
                worldId: identity.World.Value,
                source: server
            );

            if (!TryBuildFederationIdentity(
                definition: definition,
                trustEntries: () => server.Definition.Admission,
                federation: out var federation,
                reason: out var federationReason,
                worldRow: worldRow
            )) {
                Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused ({federationReason})]");
                machines.Dispose();

                return false;
            }

            var link = new LoopbackTransport(server: server);
            var tape = new WorldReplayTape(
                addonHostFactory: static (_, _) => new WorldNoAddonHost(),
                engines: [],
                liveServer: server,
                machineHostFactory: MachineHostFactory,
                profiles: profiles,
                transport: link
            );
            var adjacencies = new WorldAdjacencyFields(
                instances: Instances,
                sourceInstanceName: identity.World.Value
            );

            server.Adjacencies = adjacencies;

            var door = new WorldPeerHost(
                authenticator: federation.Authenticator,
                network: federation.Network,
                server: server
            );
            var row = new WorldInstance(
                documentOrigin: origin,
                federation: federation,
                link: link,
                name: identity.World.Value,
                origin: () => origin.Identity,
                ownedAdjacencies: adjacencies,
                ownedMachines: machines,
                ownedNetwork: federation.Network,
                server: server
            ) {
                AwaitingMirrors = true,
                Door = door,
                ListenEndpoint = definition.Host.Listen,
                Tape = tape,
            };
            var slice = checkpoint?.HostRow;
            var tcs = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            m_mailbox.Enqueue(item: () => {
                try {
                    if (IsDraining) {
                        row.Dispose();
                        tcs.TrySetResult(result: false);
                        return;
                    }
                    var gate = new WorldConsoleWaitGate();

                    row.PublishTick = gate.PublishTick;
                    Instances.Admit(row: row);

                    if (slice is { } restoreSlice) {
                        Instances.RestoreRow(
                            row: row,
                            slice: restoreSlice
                        );
                    }

                    m_rows[row.Name] = new RowBookkeeping {
                        Adjacencies = adjacencies,
                        Gate = gate,
                        Fence = fence,
                        PublishedJournalSequence = recovery.Root.Root.JournalSequence,
                        LastCheckpointOrdinal = (checkpointBlob?.Ordinal ?? -1),
                        LastCheckpointOutcome = ((checkpointBlob is null) ? "never captured" : "restored"),
                        LastCheckpointTick = (checkpointBlob?.Tick ?? 0UL),
                        CheckpointTimestamp = m_clock.GetTimestamp(),
                        Pinned = worldRow.Pinned,
                    };

                    tcs.TrySetResult(result: true);
                } catch (Exception exception) {
                    tcs.TrySetException(exception: exception);
                }
            });

            admitted = await tcs.Task;
            if (!admitted) { return false; }
            try {
                // A journal is relative to a checkpoint. Establish that baseline before socket or console admission.
                if (checkpointBlob is null && !await CheckpointNowCoreAsync(identity, ct)) {
                    throw new IOException("The initial authority checkpoint could not be published.");
                }
                var releaseHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                m_mailbox.Enqueue(() => {
                    try {
                        var bookkeeping = m_rows[row.Name];
                        bookkeeping.Initializing = false;
                        _ = m_routing.Register(worldId: row.Name);
                        if (AllAdjacenciesPrimed(row)) { Instances.ReleaseHold(row); }
                        releaseHold.TrySetResult();
                    } catch (Exception error) { releaseHold.TrySetException(error); }
                });
                await releaseHold.Task;
            } catch {
                var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                m_mailbox.Enqueue(() => {
                    try {
                        row.Server.FreezeForRetirement();
                        _ = Instances.TryStop(row.Name, out _);
                        _ = m_rows.Remove(row.Name);
                        m_routing.Unregister(row.Name);
                        cleanup.TrySetResult();
                    } catch (Exception error) { cleanup.TrySetException(error); }
                });
                await cleanup.Task;
                admitted = false;
                throw;
            }
            return admitted;
        } finally {
            if (!admitted) {
                using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                _ = await m_store.ReleaseActivationAsync(identity, fence, releaseDeadline.Token);
            }
        }
    }
    /// <summary>Requests an immediate checkpoint for one activated row — <c>silo.checkpoint &lt;key&gt;</c>.</summary>
    /// <param name="identity">The row to checkpoint.</param>
    /// <param name="ct">A token to observe.</param>
    /// <returns><see langword="true"/> when the checkpoint captured and wrote successfully.</returns>
    public Task<bool> CheckpointNowAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        lock (m_drainLock) {
            if (m_drainTask is not null) { return Task.FromResult(result: false); }
            ObserveCompleted(operations: m_persistenceOperations);
            var operation = CheckpointNowCoreAsync(ct: ct, identity: identity);

            m_persistenceOperations.Add(item: operation);
            return operation;
        }
    }

    private async Task<bool> CheckpointNowCoreAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        try {
            var worldId = identity.World.Value;
            var captureTcs = new TaskCompletionSource<Task<WorldAuthorityStoreOutcome>?>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            m_mailbox.Enqueue(item: () => {
                try {
                    ct.ThrowIfCancellationRequested();
                    if (
                        !Instances.TryGet(
                        instance: out var row,
                        name: worldId
                    ) ||
                        (row is null)
                    ) {
                        captureTcs.TrySetResult(result: null);

                        return;
                    }

                    if (TryCaptureRow(
                        encoded: out var encoded,
                        outcome: out var outcome,
                        row: row,
                        tick: out var tick
                    )) {
                        captureTcs.TrySetResult(result: QueueCheckpoint(encoded, identity, worldId, tick, m_rows[worldId], ct));
                    } else {
                        if (m_rows.TryGetValue(
                            key: worldId,
                            value: out var bookkeeping
                        )) {
                            bookkeeping.CheckpointDeferredCount++;
                            bookkeeping.LastCheckpointOutcome = outcome;
                        }

                        captureTcs.TrySetResult(result: null);
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) { captureTcs.TrySetCanceled(ct); } catch (Exception error) { captureTcs.TrySetException(error); }
            });

            var queued = await captureTcs.Task.WaitAsync(ct);
            return queued is not null && (await queued.WaitAsync(ct)).Ok;
        } catch (Exception error) {
            Console.Error.WriteLine($"[silo.checkpoint: '{identity}' failed ({error.Message})]");
            throw;
        }
    }

    /// <inheritdoc/>
    public Task DeactivateAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        lock (m_drainLock) {
            if (IsDraining || (m_drainTask is { IsCompleted: false })) { return Task.FromException(exception: new InvalidOperationException(message: "The silo is retiring.")); }
            ObserveCompleted(operations: m_persistenceOperations);
            var operation = DeactivateCoreAsync(ct: ct, identity: identity);

            m_persistenceOperations.Add(item: operation);
            return operation;
        }
    }

    private async Task DeactivateCoreAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        try {
            var worldId = identity.World.Value;
            var captureTcs = new TaskCompletionSource<(RowBookkeeping Bookkeeping, Task<WorldAuthorityStoreOutcome> Save)?>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            m_mailbox.Enqueue(item: () => {
                try {
                    ct.ThrowIfCancellationRequested();
                    if (!Instances.TryGet(instance: out var row, name: worldId) || row is null) {
                        captureTcs.TrySetResult(result: null);
                        return;
                    }
                    row.Door?.SuspendIngress();
                    row.Server.FreezeForRetirement();
                    if (TryCaptureRow(
                    encoded: out var encoded,
                    outcome: out _,
                    row: row,
                    tick: out var tick
                    )) {
                        var bookkeeping = m_rows[worldId];
                        captureTcs.TrySetResult(result: (bookkeeping, QueueCheckpoint(encoded, identity, worldId, tick, bookkeeping, ct)));
                    } else {
                        captureTcs.TrySetResult(result: null);
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    captureTcs.TrySetCanceled(cancellationToken: ct);
                } catch (Exception error) {
                    captureTcs.TrySetException(exception: error);
                }
            });

            var captured = await captureTcs.Task.WaitAsync(ct);
            if (captured is not { } saved) {
                throw new InvalidOperationException(message: $"Cannot deactivate '{worldId}': no final checkpoint could be captured.");
            }
            var outcome = await saved.Save.WaitAsync(ct);
            if (!outcome.Ok) { throw new IOException(message: $"Cannot deactivate '{worldId}': {outcome.Detail}"); }
            var release = await m_store.ReleaseActivationAsync(identity, saved.Bookkeeping.Fence, ct);
            if (!release.Ok) { throw new IOException($"Cannot release '{worldId}': {release.Detail}"); }
            saved.Bookkeeping.Released = true;
            Console.Error.WriteLine(value: $"[silo.deactivate: '{RowKey(identity: identity)}' final checkpoint ok]");

            var removeTcs = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            m_mailbox.Enqueue(item: () => {
                if (!m_rows.TryGetValue(worldId, out var current) || !ReferenceEquals(current, saved.Bookkeeping)) {
                    removeTcs.TrySetException(new InvalidOperationException("The activation changed before retirement removal."));
                    return;
                }
                _ = Instances.TryStop(
                    name: worldId,
                    reason: out _
                );
                _ = m_rows.Remove(key: worldId);
                m_routing.Unregister(worldId: worldId);
                removeTcs.TrySetResult(result: true);
            });

            await removeTcs.Task;
        } catch (Exception error) {
            Console.Error.WriteLine($"[silo.deactivate: '{identity}' failed ({error.Message})]");
            throw;
        }
    }

    /// <summary>Drains queued activation/deactivation/checkpoint work built off the tick thread, then sweeps every
    /// held row for adjacency priming and recomputes the master cadence — the one thing every
    /// <see cref="Puck.Hosting.IFixedStepSimulation.Step"/> call must do before stepping.</summary>
    public void DrainActivationMailbox() {
        while (m_mailbox.TryDequeue(result: out var action)) {
            action();
        }

        if (IsDraining) { return; }
        SweepAwaitingMirrors();
        RecomputeMasterRateHz();
    }
    /// <summary>Reports one master step's own engine-tick width toward the checkpoint cadence, arming and honouring a
    /// silo-wide capture request at the accumulated threshold.</summary>
    /// <param name="stepTicks">The master step's own engine-tick width.</param>
    public void NoteMasterStep(ulong stepTicks) {
        Volatile.Write(location: ref m_progressTimestamp, value: m_clock.GetTimestamp());
        m_masterElapsedEngineTicks += stepTicks;
        m_cadence.NoteMasterStep(stepTicks: stepTicks);

        if (m_cadence.IsArmed) {
            CaptureRowsArmedByCadence();
        }
    }
    /// <summary>Builds one row's own read-back — <c>silo.grains</c>' per-row payload and <see cref="IWorldGrain.StatusAsync"/>'s
    /// answer.</summary>
    /// <param name="worldId">The row's world id.</param>
    /// <returns>The row's status, or <see langword="null"/> when no such row is admitted.</returns>
    public WorldGrainStatus? TryDescribeRow(string worldId) {
        if (
            !Instances.TryGet(
            instance: out var row,
            name: worldId
        ) ||
            (row is null)
        ) {
            return null;
        }

        m_rows.TryGetValue(
            key: worldId,
            value: out var bookkeeping
        );

        var behindTicks = ((m_masterElapsedEngineTicks > row.ElapsedEngineTicks)
            ? (m_masterElapsedEngineTicks - row.ElapsedEngineTicks)
            : 0UL
        );

        return new WorldGrainStatus {
            AwaitingMirrors = row.AwaitingMirrors,
            BehindTicks = behindTicks,
            CheckpointDeferredCount = (bookkeeping?.CheckpointDeferredCount ?? 0),
            DoorEndpoint = (row.Door?.ListenEndpoint ?? string.Empty),
            ElapsedEngineTicks = row.ElapsedEngineTicks,
            FederationSubject = row.Federation.Subject,
            Key = ((FindWorldRow(name: worldId) is { } worldRow)
                ? RowKey(identity: new WorldAuthorityIdentity(Owner: worldRow.Owner, World: worldRow.World))
                : worldId),
            LastCheckpointOrdinal = (bookkeeping?.LastCheckpointOrdinal ?? -1),
            LastCheckpointOutcome = (bookkeeping?.LastCheckpointOutcome ?? "never captured"),
            LastCheckpointTick = (bookkeeping?.LastCheckpointTick ?? 0UL),
            LastJournalOutcome = (bookkeeping?.LastJournalOutcome ?? "none yet"),
            Paused = row.IsPaused,
            PendingJournalAppends = (bookkeeping?.PendingJournalAppends ?? 0),
            RateHz = row.Server.Definition.SimulationRateHz,
            ScheduleAccumulatorTicks = row.ScheduleAccumulatorTicks,
            Tick = row.CompletedTicks,
            World = worldId,
        };
    }
    /// <summary>Reads back every currently admitted row — <c>silo.grains</c>' full table.</summary>
    public IReadOnlyList<WorldGrainStatus> DescribeRows() {
        var rows = new List<WorldGrainStatus>();

        foreach (var name in Instances.Names) {
            if (TryDescribeRow(worldId: name) is { } status) {
                rows.Add(item: status);
            }
        }

        return rows;
    }
}
