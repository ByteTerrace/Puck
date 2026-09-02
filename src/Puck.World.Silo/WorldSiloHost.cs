using System.Collections.Concurrent;
using System.Security.Cryptography;
using Puck.Attestation;
using Puck.Storage;
using Puck.World.Protocol;
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
public sealed class WorldSiloHost : IWorldAuthorityHost, IWorldWaitGateResolver {
    private sealed class RowBookkeeping {
        public required WorldAdjacencyFields Adjacencies { get; init; }
        public required WorldConsoleWaitGate Gate { get; init; }

        public int CheckpointDeferredCount;

        // The row's own serialized append chain (KEEP IN SYNC: WorldAuthorityBlobStore.AppendJournalAsync rewrites
        // the whole journal blob if-match-per-append, so two appends racing the same version would drop one — every
        // append for a row is scheduled as a continuation of the one before it, never fired concurrently. Touched
        // only from the tick thread (MutationJournalTap fires there); the continuation itself runs on the thread pool.
        public Task JournalTail = Task.CompletedTask;

        public int PendingJournalAppends;

        public string LastCheckpointOutcome = "never captured";
        public string LastJournalOutcome = "none yet";
        public long LastCheckpointOrdinal = -1;

        public ulong LastCheckpointTick;

        public required bool Pinned { get; init; }
    }

    private readonly IObjectBlobStore m_blobStore;

    private readonly WorldAuthorityCheckpointCadenceCounter m_cadence = new();

    private readonly WorldSiloDefinition m_definition;
    private readonly Guid m_machineId;

    private readonly ConcurrentQueue<Action> m_mailbox = new();

    private readonly SiloConsoleRouting m_routing;

    private readonly Dictionary<string, RowBookkeeping> m_rows = new(comparer: StringComparer.Ordinal);

    private readonly ObjectStorageTarget m_storageTarget;
    private readonly IWorldAuthorityStore m_store;

    private ulong m_masterElapsedEngineTicks;

    /// <summary>Initializes the silo host over a validated document and its resolved blob store.</summary>
    /// <param name="definition">The validated silo document.</param>
    /// <param name="blobStore">The composed blob store both the directory and Azure backends ride.</param>
    /// <param name="routing">Where every admitted row's own tagged console session is registered and retired.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldSiloHost(WorldSiloDefinition definition, IObjectBlobStore blobStore, SiloConsoleRouting routing) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: blobStore);
        ArgumentNullException.ThrowIfNull(argument: routing);

        m_definition = definition;
        m_blobStore = blobStore;
        m_routing = routing;
        m_storageTarget = ((definition.Store.Kind == WorldSiloStoreKind.Directory)
            ? new DirectoryObjectStorageTarget(rootPath: definition.Store.DirectoryPath!)
            : AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: definition.Store.AccountUrl!)
        );
        m_store = new WorldAuthorityBlobStore(
            store: blobStore,
            target: m_storageTarget
        );
        m_machineId = ResolveMachineId(stateDir: definition.StateDir);
        Instances = new WorldInstanceHost(
            admitsSpawn: false,
            applicationStopping: CancellationToken.None,
            machineId: m_machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: definition.StateDir
        );
    }

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
                WorldSafeName.TryParse(
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
    public Task<WorldAuthorityStoreOutcome> PublishDefinitionAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken ct) => m_store.PublishDefinitionAsync(
        cancellationToken: ct,
        composed: composed,
        identity: identity
    );

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

                _ = UploadCheckpointAsync(
                    encoded: captured,
                    identity: new WorldAuthorityIdentity(
                        Owner: (FindWorldRow(name: name)?.Owner ?? Guid.Empty),
                        World: (WorldSafeName.TryParse(candidate: name, name: out var world, reason: out _) ? world : default)
                    ),
                    tick: capturedTick,
                    worldId: name
                );
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
                (row is not { IsPaused: false, AwaitingMirrors: false })
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
                (row is { AwaitingMirrors: true }) &&
                AllAdjacenciesPrimed(row: row)
            ) {
                Instances.ReleaseHold(row: row);
            }
        }
    }
    private static bool TryBuildFederationIdentity(WorldDefinition definition, WorldSiloWorldRow worldRow, out WorldFederationIdentity federation, out string reason) {
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
                Authenticator: new WorldAttestedAuthenticator(
                    oracle: new LocalKeySigningOracle(
                        key: key,
                        subject: subject,
                        validity: WorldAttestedAuthenticator.MaximumClaimAge
                    ),
                    trustEntries: () => definition.Admission
                ),
                Subject: subject
            );
            reason = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)) {
            federation = default;
            reason = $"'{worldRow.World}' federation.keyFile could not be read — {exception.Message}";

            return false;
        }
    }
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
    private async Task UploadCheckpointAsync(byte[] encoded, WorldAuthorityIdentity identity, string worldId, ulong tick) {
        var outcome = await m_store.WriteCheckpointAsync(
            cancellationToken: CancellationToken.None,
            encoded: encoded,
            identity: identity,
            tick: tick
        );
        var latest = (outcome.Ok
            ? await m_store.LoadLatestAsync(
                cancellationToken: CancellationToken.None,
                identity: identity
            )
            : null
        );

        m_mailbox.Enqueue(item: () => {
            if (m_rows.TryGetValue(
                key: worldId,
                value: out var bookkeeping
            )) {
                bookkeeping.LastCheckpointOutcome = (outcome.Ok
                    ? "ok"
                    : $"failed ({outcome.Detail})"
                );

                if (latest is { } blob) {
                    bookkeeping.LastCheckpointOrdinal = blob.Ordinal;
                    bookkeeping.LastCheckpointTick = blob.Tick;
                }
            }
        });
    }
    private async Task AppendJournalEntryAsync(WorldAuthorityIdentity identity, string worldId, ulong tick, byte[] encoded) {
        var outcome = await m_store.AppendJournalAsync(
            cancellationToken: CancellationToken.None,
            entry: new WorldMutationJournalEntry(Encoded: encoded, Tick: tick),
            identity: identity
        );

        m_mailbox.Enqueue(item: () => {
            if (m_rows.TryGetValue(
                key: worldId,
                value: out var bookkeeping
            )) {
                bookkeeping.PendingJournalAppends--;
                bookkeeping.LastJournalOutcome = (outcome.Ok
                    ? "ok"
                    : $"failed ({outcome.Detail})"
                );
            }
        });
    }
    // Called from WorldServer.MutationJournalTap, always on the tick thread (see the property's own remarks) — the
    // ONE writer of a row's JournalTail, so no lock is needed to chain the next append onto it.
    private void ScheduleJournalAppend(string worldId, WorldAuthorityIdentity identity, ulong tick, WorldMutation mutation) {
        if (!m_rows.TryGetValue(
            key: worldId,
            value: out var bookkeeping
        )) {
            return;
        }

        if (!WorldSubmissionCodec.TryEncodeMutation(
            bytes: out var encoded,
            failure: out var failure,
            mutation: mutation
        )) {
            Console.Error.WriteLine(value: $"[silo.journal: '{RowKey(identity: identity)}' a mutation would not re-encode for the durable journal ({failure}) — this tick's mutation is unrecoverable after a restart with no later checkpoint]");

            return;
        }

        bookkeeping.PendingJournalAppends++;
        bookkeeping.JournalTail = bookkeeping.JournalTail.ContinueWith(
            continuationFunction: _ => AppendJournalEntryAsync(
                encoded: encoded,
                identity: identity,
                tick: tick,
                worldId: worldId
            ),
            scheduler: TaskScheduler.Default
        ).Unwrap();
    }

    /// <inheritdoc/>
    public async Task<bool> ActivateAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        if (FindWorldRow(identity: identity) is not { } worldRow) {
            Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (not declared in this silo's document)]");

            return false;
        }

        var origin = new WorldHostedOrigin(
            owner: identity.Owner,
            store: m_blobStore,
            target: m_storageTarget,
            world: identity.World
        );

        if (!origin.TryLoad(
            definition: out var definition,
            instanceIdentity: identity.World.Value,
            reason: out var loadReason
        )) {
            Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused ({loadReason})]");

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

        var checkpointBlob = await m_store.LoadLatestAsync(
            cancellationToken: ct,
            identity: identity
        );
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
            template: definition!
        );
        var machines = new WorldMachineHost(
            engines: [],
            screens: definition!.Screens
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

        if (checkpointBlob is { } tailBlob) {
            var tail = await m_store.LoadJournalTailAsync(
                afterOrdinal: tailBlob.Ordinal,
                cancellationToken: ct,
                identity: identity
            );

            foreach (var entry in tail.Entries) {
                if (
                    !WorldSubmissionCodec.TryDecodeMutation(
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
            worldId: identity.World.Value
        );

        if (!TryBuildFederationIdentity(
            definition: definition,
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
            profiles: profiles,
            transport: link
        );
        var adjacencies = new WorldAdjacencyFields(
            instances: Instances,
            sourceInstanceName: identity.World.Value
        );

        server.Adjacencies = adjacencies;

        var door = new WorldTcpHost(
            authenticator: federation.Authenticator,
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
            server: server
        ) {
            AwaitingMirrors = (checkpoint is not null),
            Door = door,
            Tape = tape,
        };
        var slice = checkpoint?.HostRow;
        var tcs = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        m_mailbox.Enqueue(item: () => {
            try {
                var gate = new WorldConsoleWaitGate();

                row.PublishTick = gate.PublishTick;
                _ = m_routing.Register(
                    hold: gate.IsHolding,
                    worldId: row.Name
                );

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
                    LastCheckpointOrdinal = (checkpointBlob?.Ordinal ?? -1),
                    LastCheckpointOutcome = ((checkpointBlob is null) ? "never captured" : "restored"),
                    LastCheckpointTick = (checkpointBlob?.Tick ?? 0UL),
                    Pinned = worldRow.Pinned,
                };

                if (
                    !row.AwaitingMirrors ||
                    AllAdjacenciesPrimed(row: row)
                ) {
                    Instances.ReleaseHold(row: row);
                }

                tcs.TrySetResult(result: true);
            } catch (Exception exception) {
                tcs.TrySetException(exception: exception);
            }
        });

        return await tcs.Task;
    }
    /// <summary>Requests an immediate checkpoint for one activated row — <c>silo.checkpoint &lt;key&gt;</c>.</summary>
    /// <param name="identity">The row to checkpoint.</param>
    /// <param name="ct">A token to observe.</param>
    /// <returns><see langword="true"/> when the checkpoint captured and wrote successfully.</returns>
    public async Task<bool> CheckpointNowAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        var worldId = identity.World.Value;
        var captureTcs = new TaskCompletionSource<(bool Ok, byte[] Encoded, ulong Tick, string Outcome)>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        m_mailbox.Enqueue(item: () => {
            if (
                !Instances.TryGet(
                instance: out var row,
                name: worldId
            ) ||
                (row is null)
            ) {
                captureTcs.TrySetResult(result: (false, [], 0, "no such row"));

                return;
            }

            if (TryCaptureRow(
                encoded: out var encoded,
                outcome: out var outcome,
                row: row,
                tick: out var tick
            )) {
                captureTcs.TrySetResult(result: (true, encoded, tick, outcome));
            } else {
                if (m_rows.TryGetValue(
                    key: worldId,
                    value: out var bookkeeping
                )) {
                    bookkeeping.CheckpointDeferredCount++;
                    bookkeeping.LastCheckpointOutcome = outcome;
                }

                captureTcs.TrySetResult(result: (false, [], 0, outcome));
            }
        });

        var (ok, encoded2, tick2, captureOutcome) = await captureTcs.Task;

        if (!ok) {
            return false;
        }

        var writeOutcome = await m_store.WriteCheckpointAsync(
            cancellationToken: ct,
            encoded: encoded2,
            identity: identity,
            tick: tick2
        );
        var latest = (writeOutcome.Ok
            ? await m_store.LoadLatestAsync(
                cancellationToken: ct,
                identity: identity
            )
            : null
        );

        m_mailbox.Enqueue(item: () => {
            if (!m_rows.TryGetValue(
                key: worldId,
                value: out var bookkeeping
            )) {
                return;
            }

            bookkeeping.LastCheckpointOutcome = (writeOutcome.Ok
                ? "ok"
                : $"failed ({writeOutcome.Detail})"
            );

            if (latest is { } blob) {
                bookkeeping.LastCheckpointOrdinal = blob.Ordinal;
                bookkeeping.LastCheckpointTick = blob.Tick;
            }
        });

        return writeOutcome.Ok;
    }
    /// <inheritdoc/>
    public async Task DeactivateAsync(WorldAuthorityIdentity identity, CancellationToken ct) {
        var worldId = identity.World.Value;
        var captureTcs = new TaskCompletionSource<(bool Ok, byte[] Encoded, ulong Tick)>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        m_mailbox.Enqueue(item: () => {
            if (
                Instances.TryGet(
                instance: out var row,
                name: worldId
            ) &&
                (row is { AwaitingMirrors: false }) &&
                TryCaptureRow(
                encoded: out var encoded,
                outcome: out _,
                row: row,
                tick: out var tick
            )
            ) {
                captureTcs.TrySetResult(result: (true, encoded, tick));
            } else {
                captureTcs.TrySetResult(result: (false, [], 0));
            }
        });

        var (ok, encoded2, tick2) = await captureTcs.Task;

        if (ok) {
            var outcome = await m_store.WriteCheckpointAsync(
                cancellationToken: ct,
                encoded: encoded2,
                identity: identity,
                tick: tick2
            );

            Console.Error.WriteLine(value: $"[silo.deactivate: '{RowKey(identity: identity)}' final checkpoint {(outcome.Ok ? "ok" : $"failed ({outcome.Detail})")}]");
        } else {
            Console.Error.WriteLine(value: $"[silo.deactivate: '{RowKey(identity: identity)}' retiring with no final checkpoint]");
        }

        var removeTcs = new TaskCompletionSource<bool>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        m_mailbox.Enqueue(item: () => {
            _ = Instances.TryStop(
                name: worldId,
                reason: out _
            );
            _ = m_rows.Remove(key: worldId);
            m_routing.Unregister(worldId: worldId);
            removeTcs.TrySetResult(result: true);
        });

        await removeTcs.Task;
    }
    /// <summary>Drains queued activation/deactivation/checkpoint work built off the tick thread, then sweeps every
    /// held row for adjacency priming and recomputes the master cadence — the one thing every
    /// <see cref="Puck.Hosting.IFixedStepSimulation.Step"/> call must do before stepping.</summary>
    public void DrainActivationMailbox() {
        while (m_mailbox.TryDequeue(result: out var action)) {
            action();
        }

        SweepAwaitingMirrors();
        RecomputeMasterRateHz();
    }
    /// <summary>Reports one master step's own engine-tick width toward the checkpoint cadence, arming and honouring a
    /// silo-wide capture request at the accumulated threshold.</summary>
    /// <param name="stepTicks">The master step's own engine-tick width.</param>
    public void NoteMasterStep(ulong stepTicks) {
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
