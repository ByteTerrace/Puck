using System.Security.Cryptography;
using Puck.Abstractions.Machines;
using Puck.Attestation;
using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Machines;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Result of the explicit managed group publication barrier.</summary>
public enum WorldReleaseAdmissionPublication { Opened, CandidatePrivate, Refused }
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
        public required WorldAuthorityFence Fence { get; init; }
        public required WorldConsoleWaitGate Gate { get; init; }
        public required bool Pinned { get; init; }

        public long PublishedJournalSequence = -1;
        public bool Initializing = true;
        // All checkpoint and journal publications share this queue. A snapshot is enqueued at capture time,
        // before later mutations, so its coverage watermark can never include a mutation absent from its bytes.
        // Only the pump replaces the tail; continuations update PublishedJournalSequence in queue order.
        public Task JournalTail = Task.CompletedTask;
        public string LastCheckpointOutcome = "never captured";
        public string LastJournalOutcome = "none yet";
        public long LastCheckpointOrdinal = -1;

        public int CheckpointDeferredCount;
        public long CheckpointTimestamp;
        public bool JournalFailed;
        public ulong JournalFailureTick;
        public long JournalTimestamp;
        public ulong LastCheckpointTick;
        public int PendingJournalAppends;
        public bool PersistenceBlocked;
        public bool Released;
    }

    /// <summary>Gets how long a refused activation waits, on the silo clock, to release the authority fence it
    /// acquired.</summary>
    public static TimeSpan ReleaseActivationTimeout { get; } = TimeSpan.FromSeconds(seconds: 10);

    private readonly IObjectBlobStore m_blobStore;
    private readonly TimeProvider m_clock;

    private readonly WorldAuthorityCheckpointCadenceCounter m_cadence = new();

    private readonly WorldSiloDefinition m_definition;
    private readonly WorldMachineCatalog m_machineCatalog;
    private readonly IMachineContentAdmissionPolicy m_contentAdmissionPolicy;
    private readonly string m_catalogFingerprint;
    private readonly Guid m_machineId;
    private readonly SiloConsoleRouting m_routing;
    private readonly WorldStateRoot m_stateRoot;

    private readonly Dictionary<string, RowBookkeeping> m_rows = new(comparer: StringComparer.Ordinal);

    private readonly ObjectStorageTarget m_storageTarget;
    private readonly IWorldAuthorityStore m_store;
    private readonly WorldReleaseGroupStore? m_releaseGroupStore;
    private readonly WorldSiloReleaseManagement? m_releaseManagement;

    private int m_releaseAdmissionOpen;
    private Guid m_publishedAdmissionClaim;
    private ulong m_masterElapsedEngineTicks;
    private int m_draining;

    private readonly Lock m_drainLock = new();
    private readonly SemaphoreSlim m_activationGate = new(
        initialCount: 1,
        maxCount: 1
    );

    private Task? m_drainTask;

    private readonly List<Task> m_checkpointUploads = [];
    private readonly List<Task> m_persistenceOperations = [];

    private bool m_ready;
    private Puck.Abstractions.HostResourceUnavailableException? m_hostUnavailable;

    private readonly Func<WorldSiloExtension, Puck.Networking.IAuthenticator, TimeProvider, Puck.Networking.IAuthenticator>? m_authentication;

    /// <summary>Initializes the silo host over a validated document and its resolved blob store.</summary>
    /// <param name="definition">The validated silo document.</param>
    /// <param name="blobStore">The composed blob store.</param>
    /// <param name="storageTarget">The target supplied by the selected persistence extension.</param>
    /// <param name="routing">Where every admitted row's own tagged console session is registered and retired.</param>
    /// <param name="authentication">The composition root's installed authentication-provider resolver, handed the
    /// silo's clock for the provider it builds.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <param name="machineCatalog">The immutable machine catalog selected by this silo host.</param>
    /// <param name="contentAdmissionPolicy">The captured host policy for machine content; defaults to the open local policy.</param>
    /// <param name="timeProvider">The silo's one clock (<see cref="Clock"/>); <see langword="null"/> is
    /// <see cref="TimeProvider.System"/>.</param>
    /// <param name="extensions">The silo's composed extensions, from which a row's <c>extensions</c> configuration
    /// selects its providers and participants; <see langword="null"/> is an empty set.</param>
    public WorldSiloHost(WorldSiloDefinition definition, IObjectBlobStore blobStore, SiloConsoleRouting routing, ObjectStorageTarget storageTarget, WorldMachineCatalog? machineCatalog = null,
        Func<WorldSiloExtension, Puck.Networking.IAuthenticator, TimeProvider, Puck.Networking.IAuthenticator>? authentication = null, IMachineContentAdmissionPolicy? contentAdmissionPolicy = null,
        TimeProvider? timeProvider = null, Puck.Abstractions.PuckExtensionSet? extensions = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: blobStore);
        ArgumentNullException.ThrowIfNull(argument: routing);
        ArgumentNullException.ThrowIfNull(argument: storageTarget);
        machineCatalog ??= new WorldMachineCatalog([]);

        m_clock = (timeProvider ?? TimeProvider.System);
        m_progressTimestamp = m_clock.GetTimestamp();
        m_definition = definition;
        m_machineCatalog = machineCatalog;
        m_contentAdmissionPolicy = (contentAdmissionPolicy ?? MachineContentAdmissionPolicy.Open(assetAdmission: MachineAssetAdmission.Allow));
        m_catalogFingerprint = machineCatalog.CompositionFingerprint;
        m_authentication = authentication;
        m_extensions = (extensions ?? Puck.Abstractions.PuckExtensionSet.Compose(extensions: []));
        m_blobStore = blobStore;
        m_routing = routing;
        m_storageTarget = storageTarget;
        m_store = new WorldAuthorityBlobStore(
            machines: m_machineCatalog,
            store: blobStore,
            target: m_storageTarget,
            timeProvider: m_clock
        );
        m_releaseManagement = definition.Release;
        m_releaseGroupStore = ((m_releaseManagement is { } managed)
            ? new WorldReleaseGroupStore(
                blobStore,
                m_storageTarget,
                managed.Owner
            )
            : null
        );
        m_stateRoot = new WorldStateRoot(path: definition.StateDir);
        m_machineId = m_stateRoot.MachineId(
            failure: out var machineIdFailure,
            fileName: "silo-machine.id"
        );
        if (machineIdFailure is not null) {
            Console.Error.WriteLine(value: $"[silo] machine id is session-only ({machineIdFailure})");
        }
        Instances = new WorldInstanceHost(
            admitsSpawn: false,
            applicationStopping: CancellationToken.None,
            machineHostFactory: MachineHostFactory,
            machineId: m_machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: m_stateRoot,
            machineCatalog: m_machineCatalog,
            catalogFingerprint: m_catalogFingerprint
        );
        if (ClosedGroupRewind) {
            if (definition.Worlds.Any(predicate: row => (!row.Pinned || (row.Owner != m_releaseManagement!.Owner)))) {
                throw new InvalidDataException(message: "closed rewind group requires every declared world to be pinned under its owner");
            }
            Instances.ConstrainTransferInventory(names: definition.Worlds.Select(selector: row => row.World.Value));
            foreach (var row in definition.Worlds) { m_rewindAuthorities.Add(item: row.World.Value); }
        }
    }

    /// <summary>Gets the silo's one clock: every host deadline — storage, drain, reload, health, retirement, release
    /// control, and the peer network's — and the progress and persistence health windows read it. Never read by
    /// simulation.</summary>
    public TimeProvider Clock => m_clock;
    /// <summary>Gets the immutable machine catalog selected for this silo.</summary>
    public WorldMachineCatalog MachineCatalog => m_machineCatalog;
    /// <summary>Gets the stable fingerprint of the selected machine metadata.</summary>
    public string MachineCatalogFingerprint => m_catalogFingerprint;
    /// <summary>Whether all pinned worlds have established their durable startup baseline.</summary>
    public bool Ready {
        get => Volatile.Read(location: ref m_ready); internal set => Volatile.Write(
        location: ref m_ready,
        value: value
    );
    }
    /// <summary>Gets the first environment failure a row's door met binding its listen endpoint, or
    /// <see langword="null"/> when every door this host started bound. The failure still travels its own path (a
    /// grain call, the tick thread, the release barrier); this is the copy the application reports when the run ends.</summary>
    public Puck.Abstractions.HostResourceUnavailableException? HostUnavailable => Volatile.Read(location: ref m_hostUnavailable);
    /// <summary>Whether the host has stopped stepping worlds for retirement.</summary>
    public bool IsDraining => (Volatile.Read(location: ref m_draining) != 0);
    /// <summary>Whether managed public, federation, and row-console admission has passed the durable release gate.</summary>
    public bool ReleaseAdmissionOpen => ((m_releaseGroupStore is null) || (Volatile.Read(location: ref m_releaseAdmissionOpen) != 0));
    /// <summary>Whether the host can be observed privately while a managed candidate remains held.</summary>
    public bool PrivateCandidateHealthy => (Ready && !IsDraining);

    /// <summary>Reads the fresh persisted fence census for every pinned managed row.</summary>
    public async Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> CaptureReleaseFencesAsync(CancellationToken ct = default) {
        var captured = new TaskCompletionSource<List<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => {
            try {
                var rows = new List<(WorldAuthorityIdentity, WorldAuthorityFence)>();

                foreach (var declared in m_definition.Worlds.Where(predicate: static row => row.Pinned)) {
                    if (
                        IsDraining ||
                        !m_rows.TryGetValue(
                        key: declared.World.Value,
                        value: out var bookkeeping
                    ) ||
                        bookkeeping.Initializing ||
                        bookkeeping.Released ||
                        bookkeeping.PersistenceBlocked
                    ) {
                        throw new InvalidOperationException(message: $"'{declared.World}' does not own a ready activation");
                    }
                    rows.Add(item: (new(
                        Owner: declared.Owner,
                        World: declared.World
                    ), bookkeeping.Fence));
                }
                captured.TrySetResult(result: rows);
            } catch (Exception error) { captured.TrySetException(exception: error); }
        });
        var census = await captured.Task.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        foreach (var row in census) {
            var root = await m_store.LoadRootAsync(
                cancellationToken: ct,
                identity: row.Identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (root is not { } current) ||
                (current.Root.Epoch != row.Fence.Epoch) ||
                (current.Root.FenceToken != row.Fence.Token)
            ) {
                throw new InvalidOperationException(message: $"'{row.Identity.World}' lost its activation fence");
            }
        }
        return census;
    }
    /// <summary>Persists immutable, operation-bound recovery roots for every pinned row after a successful drain.</summary>
    public async Task<IReadOnlyDictionary<string, string>> CaptureReleaseRootsAsync(Guid operationId, CancellationToken ct = default) {
        if (
            !IsDraining ||
            (m_store is not IWorldAuthorityRecoveryStore recovery)
        ) {
            throw new InvalidOperationException(message: "release capture requires a drained host and a recovery-capable authority store");
        }
        _ = await RequireSourceOperationAsync(
            ct: ct,
            operationId: operationId,
            phase: WorldReleaseOperationPhase.Drain
        ).ConfigureAwait(continueOnCapturedContext: false);
        var roots = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var row in m_definition.Worlds.Where(predicate: static row => row.Pinned)) {
            var identity = new WorldAuthorityIdentity(
                Owner: row.Owner,
                World: row.World
            );
            var root = await recovery.CaptureRecoveryRootAsync(
                cancellationToken: ct,
                identity: identity,
                operationId: operationId
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (root is not { } current) ||
                (current.Root.FenceToken != Guid.Empty)
            ) {
                throw new InvalidDataException(message: $"authority root for '{identity.World}' is missing or still owned");
            }
            roots[$"{identity.Owner:D}/{identity.World.Value}"] = current.Pin;
        }
        _ = await RequireSourceOperationAsync(
            ct: ct,
            operationId: operationId,
            phase: WorldReleaseOperationPhase.Drain
        ).ConfigureAwait(continueOnCapturedContext: false);
        return roots;
    }
    /// <summary>Restores every operation-bound root before this empty host is permitted to activate the source.</summary>
    public async Task RestoreReleaseRootsAsync(WorldReleaseGroupRecord operation, CancellationToken ct = default) {
        if (
            (m_store is not IWorldAuthorityRecoveryStore recovery) ||
            (m_releaseGroupStore is null) ||
            (m_releaseManagement is not { } managed)
        ) {
            throw new InvalidOperationException(message: "managed recovery requires a recovery-capable authority store");
        }
        var rows = m_definition.Worlds.Where(predicate: static row => row.Pinned).ToArray();

        if (
            (operation.PendingOperationId is not { } operationId) ||
            (operation.RecoveryRoots.Count != rows.Length) ||
            (operation.DeploymentGroup != managed.Group) ||
            (operation.Owner != managed.Owner)
        ) {
            throw new InvalidOperationException(message: "the recovery inventory does not match this managed group");
        }
        await m_activationGate.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        try {
            var empty = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            Post(action: () => empty.TrySetResult(result: ((m_rows.Count == 0) && !ReleaseAdmissionOpen && !IsDraining)));
            if (!await empty.Task.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false)) { throw new InvalidOperationException(message: "source restoration requires an empty private host"); }
            foreach (var row in rows) {
                var identity = new WorldAuthorityIdentity(
                    Owner: row.Owner,
                    World: row.World
                );

                if (
                    !operation.RecoveryRoots.TryGetValue(
                    key: $"{identity.Owner:D}/{identity.World}",
                    value: out var pin
                ) ||
                    (await recovery.LoadRecoveryRootAsync(
                    cancellationToken: ct,
                    identity: identity,
                    operationId: operationId,
                    pin: pin
                ).ConfigureAwait(continueOnCapturedContext: false) is null)
                ) {
                    throw new InvalidDataException(message: $"protected root for '{row.World}' is missing");
                }
            }
            foreach (var row in rows) {
                var state = await RequireSourceOperationAsync(
                    ct: ct,
                    operationId: operationId,
                    phase: WorldReleaseOperationPhase.Recover
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (
                    (state.RecoveryRoots.Count != operation.RecoveryRoots.Count) ||
                    operation.RecoveryRoots.Any(predicate: root =>
                    (!state.RecoveryRoots.TryGetValue(
                    key: root.Key,
                    value: out var pin
                ) || (pin != root.Value)))
                ) {
                    throw new InvalidOperationException(message: "the recovery operation changed before root restoration");
                }
                var identity = new WorldAuthorityIdentity(
                    Owner: row.Owner,
                    World: row.World
                );
                var fence = (await m_store.AcquireActivationAsync(
                    cancellationToken: ct,
                    identity: identity
                ).ConfigureAwait(continueOnCapturedContext: false) ?? throw new IOException(message: "maintenance ownership could not be acquired"));
                var restored = await recovery.RestoreRecoveryRootAsync(
                    identity,
                    operation.RecoveryRoots[$"{identity.Owner:D}/{identity.World}"],
                    operationId,
                    fence,
                    ct
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!restored.Ok) { throw new IOException(message: $"'{row.World}' restoration refused: {restored.Detail}"); }
            }
        } finally { m_activationGate.Release(); }
    }

    private async Task<WorldReleaseGroupRecord> RequireSourceOperationAsync(Guid operationId, WorldReleaseOperationPhase phase, CancellationToken ct) {
        if (
            (m_releaseGroupStore is not { } groups) ||
            (m_releaseManagement is not { } managed) ||
            (operationId == Guid.Empty)
        ) {
            throw new InvalidOperationException(message: "source maintenance requires a managed release operation");
        }
        var snapshot = await groups.LoadAsync(
            managed.Group,
            ct
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (snapshot is not { } state) ||
            (state.Record.PendingOperationId != operationId) ||
            (state.Record.PendingPhase != phase) ||
            state.Record.PendingCommitted ||
            ((state.Record.PendingSourceRelease ?? state.Record.PendingTargetRelease) != managed.ExpectedRelease)
        ) {
            throw new InvalidOperationException(message: "the source release or maintenance operation no longer matches the durable group");
        }
        return state.Record;
    }

    /// <summary>Reads this worker's authoritative deployment group from its configured private store.</summary>
    public async Task<WorldReleaseGroupSnapshot> ReadManagedReleaseAsync(CancellationToken ct = default) {
        if (
            (m_releaseGroupStore is not { } groups) ||
            (m_releaseManagement is not { } managed)
        ) {
            throw new InvalidOperationException(message: "this worker does not have managed release configuration");
        }
        return (await groups.LoadAsync(
            managed.Group,
            ct
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidOperationException(message: "the managed deployment group is missing"));
    }
    /// <summary>Stops a private failed candidate without requiring its uncommitted state to checkpoint successfully.</summary>
    public async Task AbandonPrivateReleaseAsync(CancellationToken ct = default) {
        var stopped = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => {
            try {
                if (
                    (m_releaseManagement is null) ||
                    ReleaseAdmissionOpen
                ) { throw new InvalidOperationException(message: "an admitted world cannot be abandoned as a private candidate"); }
                Volatile.Write(
                    location: ref m_draining,
                    value: 1
                );
                Ready = false;
                Instances.Dispose();
                stopped.TrySetResult();
            } catch (Exception error) { stopped.TrySetException(exception: error); }
        });
        await stopped.Task.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
    }
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
                if (
                    (m_drainTask is null) ||
                    (m_drainTask.IsCompleted && !m_drainTask.IsCompletedSuccessfully)
                ) {
                    ObserveCompleted(operations: m_persistenceOperations);
                    m_drainTask = DrainCoreAsync(
                        ct: ct,
                        pendingOperations: m_persistenceOperations.ToArray()
                    );
                }
                attempt = m_drainTask;
            }
            try { await attempt.WaitAsync(cancellationToken: ct); return; } catch (OperationCanceledException) when ((!ct.IsCancellationRequested && attempt.IsCanceled)) { }
        }
    }

    private async Task DrainCoreAsync(CancellationToken ct, Task[] pendingOperations) {
        // Accepted reloads need a subsequent simulation step. Let them settle before freezing the pump.
        await ObservePersistenceAsync(
            Task.WhenAll(pendingOperations),
            ct
        );
        await ObservePersistenceAsync(
            Task.WhenAll(tasks: m_pendingReleases.Values.Select(selector: static release => release.Applied)),
            ct
        );
        await RetireExtensionsAsync(worldIds: [.. m_definition.Worlds.Select(selector: static declared => declared.World.Value)]);
        var capture = new TaskCompletionSource<List<(WorldAuthorityIdentity Identity, RowBookkeeping Bookkeeping, Task<WorldAuthorityStoreOutcome> Save)>>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => {
            try {
                ct.ThrowIfCancellationRequested();
                Volatile.Write(
                    location: ref m_draining,
                    value: 1
                );
                var rows = new List<(WorldAuthorityIdentity, RowBookkeeping, Task<WorldAuthorityStoreOutcome>)>();

                foreach (var declared in m_definition.Worlds) {
                    if (
                        !Instances.TryGet(
                        declared.World.Value,
                        out var row
                    ) ||
                        (row is null)
                    ) { continue; }
                    var bookkeeping = m_rows[declared.World.Value];

                    if (bookkeeping.Released) { continue; }
                    row.Door?.SuspendIngress();
                    row.Server.FreezeForRetirement();
                    if (!TryCaptureRow(
                        encoded: out var encoded,
                        outcome: out var outcome,
                        row: row,
                        tick: out var tick
                    )) {
                        throw new InvalidOperationException(message: $"Cannot retire '{declared.World}': {outcome}");
                    }
                    var identity = new WorldAuthorityIdentity(
                        Owner: declared.Owner,
                        World: declared.World
                    );

                    rows.Add(item: (identity, bookkeeping, QueueCheckpoint(
                        encoded,
                        identity,
                        row.Name,
                        tick,
                        bookkeeping,
                        ct
                    )));
                }
                capture.TrySetResult(result: rows);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { capture.TrySetCanceled(cancellationToken: ct); } catch (Exception ex) { capture.TrySetException(exception: ex); }
        });
        var captured = await capture.Task.WaitAsync(cancellationToken: ct);

        foreach (var row in captured) {
            var result = await row.Save.WaitAsync(cancellationToken: ct);

            ct.ThrowIfCancellationRequested();
            if (!result.Ok) { throw new IOException(message: $"Final checkpoint for '{row.Identity}' failed: {result.Detail}"); }
            var release = await m_store.ReleaseActivationAsync(
                row.Identity,
                row.Bookkeeping.Fence,
                ct
            );

            if (!release.Ok) { throw new IOException(message: $"Authority release for '{row.Identity}' failed: {release.Detail}"); }
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
        catalog: m_machineCatalog,
        contentAdmissionPolicy: m_contentAdmissionPolicy,
        documentPath: documentPath,
        narrationHub: narrationHub,
        screens: screens
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
    /// <summary>Publishes a composed definition to the hosted store through the identity's authority root.</summary>
    /// <param name="identity">The row to publish under.</param>
    /// <param name="composed">The composed definition.</param>
    /// <param name="ct">A token to observe.</param>
    /// <returns>The write outcome.</returns>
    public Task<WorldAuthorityStoreOutcome> PublishDefinitionAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken ct) {
        lock (m_drainLock) {
            if (m_drainTask is not null) { return Task.FromResult(result: WorldAuthorityStoreOutcome.Failed(detail: "The silo is retiring.")); }
            ObserveCompleted(operations: m_persistenceOperations);
            var operation = PublishDefinitionCoreAsync(
                composed: composed,
                ct: ct,
                identity: identity
            );

            m_persistenceOperations.Add(item: operation);
            return operation;
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
                        World: (SafeName.TryParse(
                            candidate: name,
                            name: out var world,
                            reason: out _
                        )
                    ? world
                    : default)
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
                (row is not { IsPaused: false, AwaitingMirrors: false }) ||
                row.Server.IsRetiring
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
    // Every door this host starts binds through here, so a listen endpoint this host cannot bind is kept for the run's
    // exit however its failure travels on.
    private void ReleaseHold(WorldInstance row) {
        try {
            Instances.ReleaseHold(row: row);
        } catch (Puck.Abstractions.HostResourceUnavailableException unavailable) {
            _ = Interlocked.CompareExchange(
                comparand: null,
                location1: ref m_hostUnavailable,
                value: unavailable
            );

            throw;
        }
    }
    private void SweepAwaitingMirrors() {
        if (!ReleaseAdmissionOpen) { return; }
        foreach (var name in Instances.Names) {
            if (
                Instances.TryGet(
                instance: out var row,
                name: name
            ) &&
                (row is { AwaitingMirrors: true }) &&
                !row.Server.IsRetiring &&
                m_rows.TryGetValue(
                key: name,
                value: out var bookkeeping
            ) &&
                !bookkeeping.Initializing &&
                AllAdjacenciesPrimed(row: row)
            ) {
                ReleaseHold(row: row);
            }
        }
    }
    private bool TryBuildFederationIdentity(WorldDefinition definition, WorldSiloWorldRow worldRow, Func<IReadOnlyList<WorldAdmissionEntry>?> trustEntries, out WorldFederationIdentity federation, out string reason) {
        if (
            string.IsNullOrEmpty(value: definition.Host.Authority) &&
            !string.IsNullOrEmpty(value: definition.Host.Listen)
        ) {
            federation = default;
            reason = $"'{worldRow.World}' declares host.listen without host.authority — a listening row needs an advertised endpoint";

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

            // Match WorldServer.AuthorityIdentity for a colocated row: it signs as its stable instance name
            // and has no remote endpoint. The configured row inventory keeps names unique in this host.
            var subject = (definition.Host.Authority ?? worldRow.World.Value);

            if (ClosedGroupRewind) {
                lock (m_rewindAuthoritiesGate) { m_rewindAuthorities.Add(item: subject); }
                key.Dispose();
            }

            federation = new WorldFederationIdentity(
                Authenticator: WrapAuthentication(
                    worldRow,
                    new WorldAttestedAuthenticator(
                        oracle: (ClosedGroupRewind
                ? null
                : new LocalKeySigningOracle(
                                key: key,
                                subject: subject,
                                validity: WorldAttestedAuthenticator.MaximumClaimAge
                            )),
                        trustEntries: (ClosedGroupRewind
                ? null
                : trustEntries)
                    )
                ),
                Subject: subject,
                Network: new WorldPeerNetwork(
                    identityFile: worldRow.Federation.KeyFile,
                    allowOutbound: !ClosedGroupRewind,
                    timeProvider: m_clock
                )
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
        ((row.Federation.Authentication is { } selection)
            ? (m_authentication ?? throw new InvalidOperationException(message: "No authentication provider registry is installed."))(
                selection,
                federation,
                m_clock
            )
            : federation
        );
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
        Post(action: () => {
            if (
                m_rows.TryGetValue(
                key: worldId,
                value: out var current
            ) &&
                ReferenceEquals(
                objA: current,
                objB: bookkeeping
            )
            ) {
                bookkeeping.LastCheckpointOutcome = $"failed ({error.Message})";
            }
        });
    }
    private Task<WorldAuthorityStoreOutcome> QueueCheckpoint(byte[] encoded, WorldAuthorityIdentity identity, string worldId, ulong tick,
        RowBookkeeping bookkeeping, CancellationToken cancellationToken) {
        var previous = bookkeeping.JournalTail;
        var queued = UploadCheckpointAsync(
            bookkeeping: bookkeeping,
            cancellationToken: cancellationToken,
            encoded: encoded,
            identity: identity,
            previous: previous,
            tick: tick,
            worldId: worldId
        );

        bookkeeping.JournalTail = queued;
        return queued;
    }
    private async Task<WorldAuthorityStoreOutcome> UploadCheckpointAsync(byte[] encoded, WorldAuthorityIdentity identity, string worldId, ulong tick,
        RowBookkeeping bookkeeping, Task previous, CancellationToken cancellationToken) {
        try {
            await ObservePersistenceAsync(
                ct: cancellationToken,
                operation: previous
            );
            if (bookkeeping.PersistenceBlocked) { return WorldAuthorityStoreOutcome.RecoveryRequired(detail: "This activation must recover before publishing again."); }
            var outcome = await m_store.WriteCheckpointAsync(
                cancellationToken: cancellationToken,
                encoded: encoded,
                identity: identity,
                tick: tick,
                fence: bookkeeping.Fence,
                capturedJournalSequence: bookkeeping.PublishedJournalSequence
            );

            ObservePublication(
                bookkeeping: bookkeeping,
                outcome: outcome
            );

            Post(action: () => {
                if (
                    m_rows.TryGetValue(
                    key: worldId,
                    value: out var current
                ) &&
                    ReferenceEquals(
                    objA: current,
                    objB: bookkeeping
                )
                ) {
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
            RecordCheckpointFailure(
                bookkeeping: bookkeeping,
                error: error,
                worldId: worldId
            );
            throw;
        }
    }
    private async Task AppendJournalEntryAsync(WorldAuthorityIdentity identity, string worldId, ulong tick, ulong engineTick, byte[] encoded, RowBookkeeping bookkeeping) {
        try {
            var outcome = (bookkeeping.PersistenceBlocked
                ? WorldAuthorityStoreOutcome.RecoveryRequired(detail: "This activation must recover before publishing again.")
                : await m_store.AppendJournalAsync(
                    cancellationToken: CancellationToken.None,
                    entry: new WorldMutationJournalEntry(
                        Encoded: encoded,
                        EngineTick: engineTick,
                        Tick: tick
                    ),
                    identity: identity,
                    fence: bookkeeping.Fence
                )
            );

            ObservePublication(
                bookkeeping: bookkeeping,
                outcome: outcome
            );

            Post(action: () => {
                if (
                    m_rows.TryGetValue(
                    key: worldId,
                    value: out var current
                ) &&
                    ReferenceEquals(
                    objA: current,
                    objB: bookkeeping
                )
                ) {
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
            Post(action: () => {
                if (
                    m_rows.TryGetValue(
                    key: worldId,
                    value: out var current
                ) &&
                    ReferenceEquals(
                    objA: current,
                    objB: bookkeeping
                )
                ) {
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
    private void ScheduleJournalAppend(string worldId, WorldAuthorityIdentity identity, ulong tick, ulong engineTick, WorldMutation mutation, WorldServer source) {
        if (!m_rows.TryGetValue(
            key: worldId,
            value: out var bookkeeping
        )) {
            return;
        }

        if (
            !Instances.TryGet(
            instance: out var active,
            name: worldId
        ) ||
            (active is null) ||
            !ReferenceEquals(
            objA: active.Server,
            objB: source
        )
        ) { return; }

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
                bookkeeping: bookkeeping,
                encoded: encoded,
                engineTick: engineTick,
                identity: identity,
                tick: tick,
                worldId: worldId
            ),
            scheduler: TaskScheduler.Default
        ).Unwrap();
    }

    /// <inheritdoc/>
    public Task<bool> ActivateAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        lock (m_drainLock) {
            if (m_drainTask is not null) { return Task.FromResult(result: false); }
            ObserveCompleted(operations: m_persistenceOperations);
            var operation = ActivateSerializedAsync(
                ct: ct,
                identity: identity
            );

            m_persistenceOperations.Add(item: operation);
            return operation;
        }
    }

    private async Task<bool> ActivateSerializedAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        if (FindWorldRow(identity: identity) is null) { return false; }
        await m_activationGate.WaitAsync(cancellationToken: ct);
        try {
            var existing = new TaskCompletionSource<bool?>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            Post(action: () => existing.TrySetResult(result: ((Instances.TryGet(
                identity.World.Value,
                out var row
            ) && (row is not null))
                ? !row.Server.IsRetiring
                : null)));
            if (await existing.Task.WaitAsync(cancellationToken: ct) is { } active) { return active; }
            return await ActivateCoreAsync(
                ct: ct,
                identity: identity
            );
        } finally { m_activationGate.Release(); }
    }
    private async Task<bool> CheckReleaseBeforeActivationAsync(WorldSiloWorldRow worldRow, CancellationToken ct) {
        if (
            (m_releaseGroupStore is not { } groups) ||
            (m_releaseManagement is not { } managed)
        ) {
            return true;
        }
        if (worldRow.Owner != managed.Owner) {
            Console.Error.WriteLine(value: $"[silo.activate: '{worldRow.World}' refused (managed release owner does not match the row)]");
            return false;
        }
        WorldReleaseGroupSnapshot? snapshot;

        try {
            snapshot = await groups.LoadAsync(
                managed.Group,
                ct
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception error) when ((error is InvalidDataException or ArgumentException)) {
            Console.Error.WriteLine(value: $"[silo.activate: '{worldRow.World}' refused (release group: {error.Message})]");
            return false;
        }
        if (snapshot is not { } state) {
            Console.Error.WriteLine(value: $"[silo.activate: '{worldRow.World}' refused (managed release group '{managed.Group}' is not initialized)]");
            return false;
        }
        var record = state.Record;
        var candidate = ((string.Equals(
            a: record.ActiveRelease,
            b: managed.ExpectedRelease,
            comparisonType: StringComparison.Ordinal
        ) &&
            ((record.PendingOperationId is null) || ((record.PendingPhase == WorldReleaseOperationPhase.Prepare) && (record.Admission == WorldReleaseAdmissionState.Open)) || (record.PendingPhase == WorldReleaseOperationPhase.RecoverActivate))) ||
            ((record.PendingOperationId is not null) && (record.PendingPhase is WorldReleaseOperationPhase.Activate or WorldReleaseOperationPhase.Verify or WorldReleaseOperationPhase.Commit) && string.Equals(
            a: record.PendingTargetRelease,
            b: managed.ExpectedRelease,
            comparisonType: StringComparison.Ordinal
        )));

        if (!candidate) {
            Console.Error.WriteLine(value: $"[silo.activate: '{worldRow.World}' refused (expected release '{managed.ExpectedRelease}' is not the active or pending candidate)]");
            return false;
        }
        return true;
    }
    private Task<bool> EstablishReleaseAdmissionAsync(WorldAuthorityIdentity identity, WorldAuthorityFence fence, CancellationToken ct) {
        // Managed publication is a group barrier. Activation only loads a private candidate; the startup coordinator
        // calls PublishManagedReleaseAdmissionAsync after every pinned row has restored and passed its private checks.
        if (m_releaseGroupStore is not null) {
            return Task.FromResult(result: false);
        }
        return Task.FromResult(result: true);
    }

    /// <summary>Publishes managed admission once, after every pinned candidate is ready and each row owns a fresh fence.</summary>
    public async Task<WorldReleaseAdmissionPublication> PublishManagedReleaseAdmissionAsync(CancellationToken ct = default, bool completeRecovery = false) {
        if (
            (m_releaseGroupStore is not { } groups) ||
            (m_releaseManagement is not { } managed)
        ) {
            Volatile.Write(
                location: ref m_releaseAdmissionOpen,
                value: 1
            );
            return WorldReleaseAdmissionPublication.Opened;
        }
        var previousPublicationClaim = Guid.Empty;
        var captured = new TaskCompletionSource<List<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => {
            try {
                var rows = new List<(WorldAuthorityIdentity, WorldAuthorityFence)>();

                previousPublicationClaim = m_publishedAdmissionClaim;
                foreach (var declared in m_definition.Worlds.Where(predicate: static row => row.Pinned)) {
                    if (
                        IsDraining ||
                        !m_rows.TryGetValue(
                        key: declared.World.Value,
                        value: out var bookkeeping
                    ) ||
                        bookkeeping.Initializing ||
                        bookkeeping.PersistenceBlocked ||
                        bookkeeping.Released ||
                        !Instances.TryGet(
                        declared.World.Value,
                        out var instance
                    ) ||
                        (instance is null) ||
                        instance.Server.IsRetiring
                    ) {
                        captured.TrySetResult(result: []);
                        return;
                    }
                    if (
                        instance.AwaitingMirrors ||
                        !m_routing.TryGetSession(
                        declared.World.Value,
                        out _
                    )
                    ) { previousPublicationClaim = Guid.Empty; }
                    rows.Add(item: (new WorldAuthorityIdentity(
                        Owner: declared.Owner,
                        World: declared.World
                    ), bookkeeping.Fence));
                }
                captured.TrySetResult(result: rows);
            } catch (Exception error) { captured.TrySetException(exception: error); }
        });
        var rowsReady = await captured.Task.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        if (rowsReady.Count != m_definition.Worlds.Count(predicate: static row => row.Pinned)) { return WorldReleaseAdmissionPublication.Refused; }
        foreach (var row in rowsReady) {
            var root = await m_store.LoadRootAsync(
                cancellationToken: ct,
                identity: row.Identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (root is not { } currentRoot) ||
                (currentRoot.Root.Epoch != row.Fence.Epoch) ||
                (currentRoot.Root.FenceToken != row.Fence.Token)
            ) {
                return WorldReleaseAdmissionPublication.Refused;
            }
        }
        var groupClaim = WorldReleaseFenceClaim.Compute(fences: rowsReady);
        var snapshot = await groups.LoadAsync(
            managed.Group,
            ct
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (snapshot is not { } state) { return WorldReleaseAdmissionPublication.Refused; }
        // Recheck after the group read: the first census may have raced a replacement activation. The group CAS below
        // is still the publication gate, and this second census prevents an already stale host from reaching it.
        foreach (var row in rowsReady) {
            var root = await m_store.LoadRootAsync(
                cancellationToken: ct,
                identity: row.Identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (root is not { } currentRoot) ||
                (currentRoot.Root.Epoch != row.Fence.Epoch) ||
                (currentRoot.Root.FenceToken != row.Fence.Token)
            ) {
                return WorldReleaseAdmissionPublication.Refused;
            }
        }
        var record = state.Record;
        WorldReleaseGroupOutcome opened;

        if (
            (record.PendingOperationId is { } operationId) &&
            (record.PendingPhase == WorldReleaseOperationPhase.Commit) &&
            record.PendingCommitted
        ) {
            opened = await groups.OpenAdmissionAsync(
                state,
                managed.ExpectedRelease,
                operationId,
                groupClaim,
                ct
            ).ConfigureAwait(continueOnCapturedContext: false);
        } else if (
            (record.PendingPhase == WorldReleaseOperationPhase.RecoverActivate) &&
            string.Equals(
            a: record.PendingSourceRelease,
            b: managed.ExpectedRelease,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            if (!completeRecovery) { return WorldReleaseAdmissionPublication.CandidatePrivate; }
            opened = await groups.CompleteRecoveryAsync(
                cancellationToken: ct,
                current: state,
                sourceAuthorityLease: groupClaim
            ).ConfigureAwait(continueOnCapturedContext: false);
        } else if (
            (record.PendingOperationId is not null) &&
            (record.PendingPhase == WorldReleaseOperationPhase.Prepare) &&
            (record.Admission == WorldReleaseAdmissionState.Open) &&
            string.Equals(
            a: record.ActiveRelease,
            b: managed.ExpectedRelease,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            // Preflight has no runtime mutation and therefore leaves the source serving while this host restarts;
            // the group lease still changes through CAS so a delayed prepare writer cannot publish over Drain.
            opened = await groups.RebindPrepareAdmissionAsync(
                state,
                managed.ExpectedRelease,
                groupClaim,
                ct
            ).ConfigureAwait(continueOnCapturedContext: false);
        } else if (
            (record.PendingOperationId is null) &&
            (record.Admission == WorldReleaseAdmissionState.Open) &&
            string.Equals(
            a: record.ActiveRelease,
            b: managed.ExpectedRelease,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            opened = await groups.RebindAdmissionAsync(
                state,
                managed.ExpectedRelease,
                groupClaim,
                ct
            ).ConfigureAwait(continueOnCapturedContext: false);
        } else if (
            (record.PendingOperationId is null) &&
            string.Equals(
            a: record.ActiveRelease,
            b: managed.ExpectedRelease,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            opened = await groups.OpenRecoveredAdmissionAsync(
                state,
                managed.ExpectedRelease,
                groupClaim,
                ct
            ).ConfigureAwait(continueOnCapturedContext: false);
        } else {
            return (((record.PendingOperationId is not null) && string.Equals(
                a: record.PendingTargetRelease,
                b: managed.ExpectedRelease,
                comparisonType: StringComparison.Ordinal
            ))
                ? WorldReleaseAdmissionPublication.CandidatePrivate
                : WorldReleaseAdmissionPublication.Refused
            );
        }
        if (!opened.Ok) { return WorldReleaseAdmissionPublication.Refused; }
        // A completed publication under these exact fences already registered routes and started doors. Keep
        // the guarded group write above, but do not repeat host effects or wait for another simulation boundary.
        if (
            (previousPublicationClaim == groupClaim) &&
            ReleaseAdmissionOpen &&
            !IsDraining
        ) { return WorldReleaseAdmissionPublication.Opened; }
        var published = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => {
            try {
                if (
                    ct.IsCancellationRequested ||
                    IsDraining
                ) { published.TrySetResult(result: false); return; }
                var instances = new List<WorldInstance>();

                foreach (var declared in m_definition.Worlds.Where(predicate: static row => row.Pinned)) {
                    if (
                        !Instances.TryGet(
                        declared.World.Value,
                        out var instance
                    ) ||
                        (instance is null) ||
                        !m_rows.TryGetValue(
                        key: declared.World.Value,
                        value: out var bookkeeping
                    ) ||
                        bookkeeping.Initializing ||
                        bookkeeping.PersistenceBlocked ||
                        bookkeeping.Released ||
                        instance.Server.IsRetiring
                    ) { published.TrySetResult(result: false); return; }
                    instances.Add(item: instance);
                }
                Volatile.Write(
                    location: ref m_releaseAdmissionOpen,
                    value: 1
                );
                foreach (var instance in instances) {
                    var declared = m_definition.Worlds.First(predicate: row => string.Equals(
                        a: row.World.Value,
                        b: instance.Name,
                        comparisonType: StringComparison.Ordinal
                    ));

                    if (!m_routing.TryGetSession(
                        declared.World.Value,
                        out _
                    )) { _ = m_routing.Register(worldId: declared.World.Value); }
                    if (AllAdjacenciesPrimed(row: instance)) { ReleaseHold(row: instance); }
                }
                m_publishedAdmissionClaim = groupClaim;
                published.TrySetResult(result: true);
            } catch (Exception error) { published.TrySetException(exception: error); }
        });
        return (await published.Task.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false)
            ? WorldReleaseAdmissionPublication.Opened
            : WorldReleaseAdmissionPublication.Refused
        );
    }

    private async Task<bool> ActivateCoreAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        if (IsDraining) { return false; }
        if (FindWorldRow(identity: identity) is not { } worldRow) {
            Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (not declared in this silo's document)]");

            return false;
        }
        if (!TryLoadRowExtensions(
            configuration: out var extensionConfiguration,
            refusal: out var extensionRefusal,
            worldRow: worldRow
        )) {
            Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused ({extensionRefusal})]");

            return false;
        }
        if (!await CheckReleaseBeforeActivationAsync(
            ct: ct,
            worldRow: worldRow
        ).ConfigureAwait(continueOnCapturedContext: false)) {
            return false;
        }

        await RequireRewindActivationAsync(
            identity: identity,
            token: ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        var acquired = await m_store.AcquireActivationAsync(
            cancellationToken: ct,
            identity: identity
        );

        if (acquired is not { } fence) { return false; }
        var admitted = false;

        try {
            var recovery = (await m_store.LoadRecoveryAsync(
                cancellationToken: ct,
                identity: identity
            )
                ?? throw new InvalidDataException(message: "The acquired authority has no recovery root."));

            if (
                (recovery.Root.Root.Epoch != fence.Epoch) ||
                (recovery.Root.Root.FenceToken != fence.Token)
            ) { return false; }
            var origin = new WorldHostedOrigin(
                owner: identity.Owner,
                store: m_blobStore,
                target: m_storageTarget,
                timeProvider: m_clock,
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
            var adjacencies = new WorldAdjacencyFields(
                instances: Instances,
                sourceInstanceName: identity.World.Value
            );

            try {
                if (checkpoint is { } cp) {
                    (server, population) = WorldServer.FromCheckpoint(
                        adjacencies: adjacencies,
                        checkpoint: cp,
                        instanceIdentity: identity.World.Value,
                        machines: machines,
                        profiles: profiles
                    );
                } else {
                    population = new WorldPopulation(definition: definition);
                    server = new WorldServer(
                        // This activation's own read admitted the document against this host's catalog, so
                        // construction installs those programs instead of validating and compiling again.
                        admission: recovery.Admission,
                        definition: definition,
                        envelope: new WorldRenderEnvelope(),
                        instanceIdentity: identity.World.Value,
                        machines: machines,
                        population: population,
                        profiles: profiles
                    );
                    server.Adjacencies = adjacencies;
                }
            } catch {
                adjacencies.Dispose();
                machines.Dispose();
                throw;
            }

            if (!TryFinishActivationWiring(
                adjacencies: adjacencies,
                identity: identity,
                machines: machines,
                neighbours: origin.Neighbours,
                server: server
            )) {
                return false;
            }

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
                        adjacencies.Dispose();
                        machines.Dispose();

                        return false;
                    }

                    if (!server.TryApplyJournalTailMutation(
                        mutation: mutation,
                        tick: entry.Tick,
                        engineTick: entry.EngineTick
                    )) {
                        Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (journal replay rejected a recorded mutation)]");
                        adjacencies.Dispose();
                        machines.Dispose();

                        return false;
                    }
                }
            }

            // Wired AFTER the tail replay above: a replayed entry is already durable (it came FROM the store), so
            // re-journaling it here would append a duplicate. Every mutation applied from here on — this row's live
            // operation — is new and gets appended.
            server.MutationJournalTap = (tick, engineTick, mutation) => ScheduleJournalAppend(
                identity: identity,
                mutation: mutation,
                tick: tick,
                engineTick: engineTick,
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
                adjacencies.Dispose();
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
                stateRoot: m_stateRoot,
                transport: link
            );
            var door = new WorldPeerHost(
                authenticator: federation.Authenticator,
                network: federation.Network,
                server: server,
                timeProvider: m_clock
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
                ConsoleLink = ConsoleLinkFor(link: link, row: identity.World.Value),
                Door = door,
                ListenEndpoint = definition.Host.Listen,
                Tape = tape,
            };
            var slice = checkpoint?.HostRow;
            var tcs = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            Post(action: () => {
                try {
                    if (IsDraining) {
                        row.Dispose();
                        tcs.TrySetResult(result: false);
                        return;
                    }
                    var gate = new WorldConsoleWaitGate();

                    row.PublishTick = gate.PublishTick;
                    Instances.Admit(row: row);
                    TapDeferredVerbs(row: row);

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
                        LastCheckpointOutcome = ((checkpointBlob is null)
                        ? "never captured"
                        : "restored"),
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
                if (
                    (checkpointBlob is null) &&
                    !await CheckpointNowCoreAsync(
                    ct: ct,
                    identity: identity
                )
                ) {
                    throw new IOException(message: "The initial authority checkpoint could not be published.");
                }
                if (extensionConfiguration is { } configuration) {
                    await AttachExtensionsAsync(
                        configuration: configuration,
                        row: row
                    );
                }
                var releaseOpen = await EstablishReleaseAdmissionAsync(
                    ct: ct,
                    fence: fence,
                    identity: identity
                ).ConfigureAwait(continueOnCapturedContext: false);
                var releaseHold = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

                Post(action: () => {
                    try {
                        var bookkeeping = m_rows[row.Name];

                        bookkeeping.Initializing = false;
                        Volatile.Write(
                            location: ref m_releaseAdmissionOpen,
                            value: (releaseOpen
                            ? 1
                            : 0)
                        );
                        if (releaseOpen) {
                            _ = m_routing.Register(worldId: row.Name);
                            if (AllAdjacenciesPrimed(row: row)) { ReleaseHold(row: row); }
                        }
                        releaseHold.TrySetResult();
                    } catch (Exception error) { releaseHold.TrySetException(exception: error); }
                });
                await releaseHold.Task;
            } catch (Exception activationError) {
                await RetireExtensionsAsync(worldIds: [row.Name]);
                var cleanup = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

                Post(action: () => {
                    try {
                        row.Server.FreezeForRetirement();
                        _ = Instances.TryStop(
                            name: row.Name,
                            reason: out _
                        );
                        _ = m_rows.Remove(key: row.Name);
                        m_routing.Unregister(worldId: row.Name);
                        cleanup.TrySetResult();
                    } catch (Exception error) { cleanup.TrySetException(exception: error); }
                });
                await cleanup.Task;
                admitted = false;
                if (activationError is ExtensionsRefusedException refused) {
                    Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused ({refused.Message})]");

                    return false;
                }
                throw;
            }
            return admitted;
        } finally {
            if (!admitted) {
                using var releaseDeadline = new CancellationTokenSource(
                    delay: ReleaseActivationTimeout,
                    timeProvider: m_clock
                );

                _ = await m_store.ReleaseActivationAsync(
                    identity,
                    fence,
                    releaseDeadline.Token
                );
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
            var operation = CheckpointNowCoreAsync(
                ct: ct,
                identity: identity
            );

            m_persistenceOperations.Add(item: operation);
            return operation;
        }
    }

    private async Task<bool> CheckpointNowCoreAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        try {
            var worldId = identity.World.Value;
            var captureTcs = new TaskCompletionSource<Task<WorldAuthorityStoreOutcome>?>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            Post(action: () => {
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
                        captureTcs.TrySetResult(result: QueueCheckpoint(
                            encoded,
                            identity,
                            worldId,
                            tick,
                            m_rows[worldId],
                            ct
                        ));
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
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) { captureTcs.TrySetCanceled(cancellationToken: ct); } catch (Exception error) { captureTcs.TrySetException(exception: error); }
            });

            var queued = await captureTcs.Task.WaitAsync(cancellationToken: ct);

            return (
                (queued is not null) &&
                (await queued.WaitAsync(cancellationToken: ct)).Ok
            );
        } catch (Exception error) {
            Console.Error.WriteLine(value: $"[silo.checkpoint: '{identity}' failed ({error.Message})]");
            throw;
        }
    }

    /// <inheritdoc/>
    public Task DeactivateAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        lock (m_drainLock) {
            if (
                IsDraining ||
                (m_drainTask is { IsCompleted: false })
            ) { return Task.FromException(exception: new InvalidOperationException(message: "The silo is retiring.")); }
            ObserveCompleted(operations: m_persistenceOperations);
            var operation = DeactivateCoreAsync(
                ct: ct,
                identity: identity
            );

            m_persistenceOperations.Add(item: operation);
            return operation;
        }
    }

    private async Task DeactivateCoreAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        try {
            var worldId = identity.World.Value;

            await RetireExtensionsAsync(worldIds: [worldId]);
            var captureTcs = new TaskCompletionSource<(RowBookkeeping Bookkeeping, Task<WorldAuthorityStoreOutcome> Save)?>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            Post(action: () => {
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
                    row.Door?.SuspendIngress();
                    row.Server.FreezeForRetirement();
                    if (TryCaptureRow(
                        encoded: out var encoded,
                        outcome: out _,
                        row: row,
                        tick: out var tick
                    )) {
                        var bookkeeping = m_rows[worldId];

                        captureTcs.TrySetResult(result: (bookkeeping, QueueCheckpoint(
                            bookkeeping: bookkeeping,
                            cancellationToken: ct,
                            encoded: encoded,
                            identity: identity,
                            tick: tick,
                            worldId: worldId
                        )));
                    } else {
                        captureTcs.TrySetResult(result: null);
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    captureTcs.TrySetCanceled(cancellationToken: ct);
                } catch (Exception error) {
                    captureTcs.TrySetException(exception: error);
                }
            });

            var captured = await captureTcs.Task.WaitAsync(cancellationToken: ct);

            if (captured is not { } saved) {
                throw new InvalidOperationException(message: $"Cannot deactivate '{worldId}': no final checkpoint could be captured.");
            }
            var outcome = await saved.Save.WaitAsync(cancellationToken: ct);

            if (!outcome.Ok) { throw new IOException(message: $"Cannot deactivate '{worldId}': {outcome.Detail}"); }
            var release = await m_store.ReleaseActivationAsync(
                identity,
                saved.Bookkeeping.Fence,
                ct
            );

            if (!release.Ok) { throw new IOException(message: $"Cannot release '{worldId}': {release.Detail}"); }
            saved.Bookkeeping.Released = true;
            Console.Error.WriteLine(value: $"[silo.deactivate: '{RowKey(identity: identity)}' final checkpoint ok]");

            var removeTcs = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            Post(action: () => {
                if (
                    !m_rows.TryGetValue(
                    key: worldId,
                    value: out var current
                ) ||
                    !ReferenceEquals(
                    objA: current,
                    objB: saved.Bookkeeping
                )
                ) {
                    removeTcs.TrySetException(exception: new InvalidOperationException(message: "The activation changed before retirement removal."));
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
            Console.Error.WriteLine(value: $"[silo.deactivate: '{identity}' failed ({error.Message})]");
            throw;
        }
    }

    /// <summary>Reports one master step's own engine-tick width toward the checkpoint cadence, arming and honouring a
    /// silo-wide capture request at the accumulated threshold.</summary>
    /// <param name="stepTicks">The master step's own engine-tick width.</param>
    public void NoteMasterStep(ulong stepTicks) {
        Volatile.Write(
            location: ref m_progressTimestamp,
            value: m_clock.GetTimestamp()
        );
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
            ? RowKey(identity: new WorldAuthorityIdentity(
                Owner: worldRow.Owner,
                World: worldRow.World
            ))
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
