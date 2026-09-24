using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Assets;
using Puck.Networking;
using Puck.Storage;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary><see cref="IWorldAuthorityStore"/> over <see cref="IObjectBlobStore"/>, addressed under
/// <see cref="WorldOwnedWorldSync.HostedPrivateNamespace"/> at <c>{world}/authority/</c> — fail-closed exactly like <see cref="WorldOwnedWorldSync"/>:
/// every operation is bounded by <see cref="OperationTimeout"/>, and both refusal axes an
/// <see cref="ObjectBlobWriteResult"/> can carry (a create-only loss, an if-match precondition loss) surface by name
/// rather than collapsing into one generic failure. A checkpoint blob is content-addressed and written create-only,
/// so a retry that resends identical bytes is idempotent; the private root is the sole mutable publication point and
/// moves the checkpoint, journal, definition, and receipt references under one if-match compare-and-swap, retried up
/// to <see cref="MaxCasAttempts"/> times against a concurrent writer before refusing by name.</summary>
public sealed partial class WorldAuthorityBlobStore : IWorldAuthorityStore, IWorldAuthorityRecoveryStore {
    private const int MaxCasAttempts = 5;

    private readonly TimeProvider m_clock;
    private readonly IMachineValidationCatalog? m_machines;
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;

    /// <summary>Initializes the store.</summary>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The extension-supplied storage target — persistent storage in deployment, a directory for local runs and the
    /// canary.</param>
    /// <param name="machines">The catalog a recovery read validates a published definition against, so its receipt
    /// is one the hosting activation can accept; null defers provider checks and the activation admits for itself.</param>
    /// <param name="timeProvider">The host clock every <see cref="OperationTimeout"/> runs on; <see langword="null"/> is
    /// <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    public WorldAuthorityBlobStore(IObjectBlobStore store, ObjectStorageTarget target, IMachineValidationCatalog? machines = null, TimeProvider? timeProvider = null) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);

        m_clock = (timeProvider ?? TimeProvider.System);
        m_machines = machines;
        m_store = store;
        m_target = target;
    }

    /// <summary>Gets the bound on every single store call this type makes, on the host clock, whatever the caller's
    /// own token does.</summary>
    public static TimeSpan OperationTimeout { get; } = TimeSpan.FromSeconds(seconds: 15);

    // Every store call in this type runs under the SAME bound: a linked token that cancels after OperationTimeout on
    // the host clock regardless of what the caller's own token does. Exception handling stays with each call site —
    // some convert a failure into WorldAuthorityStoreOutcome.Failed with their own wording, others let it propagate —
    // so this only wraps the timeout, never a try/catch.
    private async Task<T> UnderTimeoutAsync<T>(CancellationToken cancellationToken, Func<CancellationToken, ValueTask<T>> op) {
        using var timeout = new OperationDeadline(
            caller: cancellationToken,
            timeout: OperationTimeout,
            timeProvider: m_clock
        );

        return await op(timeout.Token);
    }
    // A checkpoint blob NAME carries only the hex half of its sha256-64 pin, since '/' cannot live inside one path
    // segment.
    private static string ExtractHex(string hash) => (AssetContentHash.TryParse(
        hash: out var parsed,
        text: hash
    )
        ? parsed.Hex
        : throw new InvalidDataException(message: $"'{hash}' is not a sha256-64 content-address pin.")
    );

    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> AppendJournalAsync(WorldAuthorityIdentity identity, WorldMutationJournalEntry entry, CancellationToken cancellationToken, WorldAuthorityFence? fence = null, WorldAuthorityOperationReceipt? receipt = null) {
        return await AppendRootAsync(
            cancellationToken: cancellationToken,
            entry: entry,
            identity: identity,
            receipt: receipt,
            suppliedFence: fence
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Reads the exact published definition bytes without filling boot draws or creating runtime state.
    /// The authority root selects and verifies its immutable definition; no root means no published definition.</summary>
    /// <param name="identity">The owner and world whose published source is inspected.</param>
    /// <param name="cancellationToken">Cancels storage reads.</param>
    /// <returns>An owned copy of the published bytes, or null when no definition is published.</returns>
    /// <exception cref="InvalidDataException">The authority root or its selected definition is missing or corrupt.</exception>
    public async Task<ReadOnlyMemory<byte>?> LoadPublishedDefinitionBytesAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var content = await WorldAuthorityRootReader.ReadDefinitionAsync(
            identity.Owner,
            identity.World,
            m_store,
            m_target,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (content is not { } found) { return null; }
        return new ReadOnlyMemory<byte>(array: found.Content.ToArray());
    }
    /// <inheritdoc/>
    public async Task<WorldDefinition?> LoadDefinitionAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var rooted = await WorldAuthorityRootReader.ReadDefinitionAsync(
            identity.Owner,
            identity.World,
            m_store,
            m_target,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (rooted is null) { return null; }
        var origin = new WorldHostedOrigin(
            owner: identity.Owner,
            store: m_store,
            target: m_target,
            timeProvider: m_clock,
            world: identity.World
        );

        return await Task.Run(
            cancellationToken: cancellationToken,
            function: () => {
                if (origin.TryLoad(
                    definition: out var definition,
                    instanceIdentity: identity.World.Value,
                    reason: out var reason
                )) {
                    return definition;
                }

                if (reason.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "no cloud copy at"
                )) {
                    return null;
                }

                throw new InvalidDataException(message: $"hosted definition for '{origin.Identity}' failed to load: {reason}");
            }
        );
    }
    /// <inheritdoc/>
    public async Task<WorldMutationJournalTail> LoadJournalTailAsync(WorldAuthorityIdentity identity, long afterOrdinal, CancellationToken cancellationToken) {
        var rooted = await LoadRecoveryAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (rooted is { } recovery) {
            if (recovery.Root.Root.CheckpointOrdinal != afterOrdinal) {
                throw new InvalidDataException(message: "requested checkpoint ordinal is not the authoritative root checkpoint");
            }
            return recovery.Journal;
        }
        return new WorldMutationJournalTail(
            CheckpointOrdinal: afterOrdinal,
            Entries: []
        );
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityCheckpointBlob?> LoadLatestAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var rooted = await LoadRecoveryAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        return rooted?.Checkpoint;
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> PublishDefinitionAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken cancellationToken, WorldAuthorityFence? fence = null) {
        ArgumentNullException.ThrowIfNull(composed);
        return await PublishDefinitionRootAsync(
            bytes: WorldDefinitionSerialization.Serialize(definition: composed),
            cancellationToken: cancellationToken,
            identity: identity,
            suppliedFence: fence
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Publishes exact definition bytes through the authority root, so
    /// <see cref="LoadPublishedDefinitionBytesAsync"/> returns them byte for byte. The bytes are not parsed here; a
    /// reader validates them when it loads the world. A fresh world gets an unowned epoch-zero root naming them.</summary>
    /// <param name="identity">The hosted world's identity.</param>
    /// <param name="definition">The exact composed definition bytes to publish.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <param name="fence">The activation fence acquired by the owning writer. <see langword="null"/> is permitted
    /// only for epoch-zero bootstrap or an already-released empty-token root; it never acquires an active lease.</param>
    /// <returns>The write outcome.</returns>
    public async Task<WorldAuthorityStoreOutcome> PublishDefinitionBytesAsync(WorldAuthorityIdentity identity, ReadOnlyMemory<byte> definition, CancellationToken cancellationToken, WorldAuthorityFence? fence = null) {
        return await PublishDefinitionRootAsync(
            bytes: definition.ToArray(),
            cancellationToken: cancellationToken,
            identity: identity,
            suppliedFence: fence
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> WriteCheckpointAsync(WorldAuthorityIdentity identity, ReadOnlyMemory<byte> encoded, ulong tick, CancellationToken cancellationToken, WorldAuthorityFence? fence = null, WorldAuthorityOperationReceipt? receipt = null, long? capturedJournalSequence = null) {
        return await WriteCheckpointRootAsync(
            cancellationToken: cancellationToken,
            capturedJournalSequence: capturedJournalSequence,
            encoded: encoded,
            identity: identity,
            receipt: receipt,
            suppliedFence: fence,
            tick: tick
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

#pragma warning disable IDE0011
    private static ObjectBlobAddress AuthorityAddress(WorldAuthorityIdentity identity, string leaf) => new(
        ObjectId: identity.Owner,
        Key: $"{WorldOwnedWorldSync.HostedPrivateNamespace}/{identity.World.Value}/authority/{leaf}"
    );

    internal static ObjectBlobAddress RootAddress(WorldAuthorityIdentity identity) => AuthorityAddress(
        identity: identity,
        leaf: "root"
    );
    internal static ObjectBlobAddress DefinitionCandidateAddress(WorldAuthorityIdentity identity, string hash) => AuthorityAddress(
        identity: identity,
        leaf: $"definitions/{ExtractHex(hash: hash)}.json"
    );

    private static ObjectBlobAddress CheckpointCandidateAddress(WorldAuthorityIdentity identity, long ordinal, string hash) => AuthorityAddress(
        identity: identity,
        leaf: $"checkpoints/{ordinal:D12}-{ExtractHex(hash: hash)}.pckp"
    );
    private static ObjectBlobAddress JournalCandidateAddress(WorldAuthorityIdentity identity, string hash) => AuthorityAddress(
        identity: identity,
        leaf: $"journal/{ExtractHex(hash: hash)}.bin"
    );
    private static ObjectBlobAddress ReceiptCandidateAddress(WorldAuthorityIdentity identity, string hash) => AuthorityAddress(
        identity: identity,
        leaf: $"receipts/{ExtractHex(hash: hash)}.rcpt"
    );
    private static ObjectBlobAddress ReceiptIndexAddress(WorldAuthorityIdentity identity, string hash) => AuthorityAddress(
        identity: identity,
        leaf: $"receipt-index/{ExtractHex(hash: hash)}.json"
    );
    private static ObjectBlobAddress RecoveryRootAddress(WorldAuthorityIdentity identity, Guid operationId) => AuthorityAddress(
        identity: identity,
        leaf: $"recovery/{operationId:D}/root.json"
    );
    private async Task<ObjectBlobContent?> ReadAsync(ObjectBlobAddress address, CancellationToken cancellationToken) => await UnderTimeoutAsync(
        cancellationToken: cancellationToken,
        op: ct => m_store.ReadAsync(
            address: address,
            cancellationToken: ct,
            target: m_target
        )
    ).ConfigureAwait(continueOnCapturedContext: false);
    private async Task<WorldAuthorityRootSnapshot?> ReadRootSnapshotAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var content = await ReadAsync(
            address: RootAddress(identity: identity),
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (content is not { } found) return null;
        if (found.VersionToken is not { Length: > 0 } token) throw new InvalidDataException(message: "authority root has no CAS version token");
        if (!WorldAuthorityRootCodec.TryDecode(
            found.Content.Span,
            out var root,
            out var reason
        )) throw new InvalidDataException(message: $"authority root is corrupt — {reason}");
        return new WorldAuthorityRootSnapshot(
            Root: root,
            VersionToken: token
        );
    }
    private async Task<ObjectBlobWriteResult> WriteRootAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, string? ifMatchVersion, ObjectBlobWriteMode mode, CancellationToken cancellationToken) => await UnderTimeoutAsync(
        cancellationToken: cancellationToken,
        op: ct => m_store.WriteAsync(
            m_target,
            RootAddress(identity: identity),
            WorldAuthorityRootCodec.Encode(root: root),
            mode,
            ifMatchVersion,
            ct
        )
    ).ConfigureAwait(continueOnCapturedContext: false);
    private async Task<WorldAuthorityStoreOutcome> PutImmutableAsync(ObjectBlobAddress address, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) {
        var result = await UnderTimeoutAsync(
            cancellationToken: cancellationToken,
            op: ct => m_store.WriteAsync(
                address: address,
                cancellationToken: ct,
                content: bytes,
                ifMatchVersion: null,
                mode: ObjectBlobWriteMode.CreateOnly,
                target: m_target
            )
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (result.Succeeded) return WorldAuthorityStoreOutcome.Success();
        var current = await ReadAsync(
            address: address,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (current is { } found) &&
            found.Content.Span.SequenceEqual(other: bytes.Span)
        ) return WorldAuthorityStoreOutcome.Success("already present");
        return WorldAuthorityStoreOutcome.AlreadyExists(detail: $"'{address.Key}' exists with different content");
    }

    private readonly record struct RootWriteSnapshot(WorldAuthorityRootSnapshot Snapshot, bool CreateOnly);

    private static bool IsUnownedFence(WorldAuthorityFence fence) => ((fence.Epoch == 0) && (fence.Token == Guid.Empty));
    private static bool FenceMatches(WorldAuthorityRootSnapshot snapshot, WorldAuthorityFence fence) => (IsUnownedFence(fence: fence)
        ? (snapshot.Root.FenceToken == Guid.Empty)
        : ((snapshot.Root.Epoch == fence.Epoch) && (snapshot.Root.FenceToken == fence.Token))
    );
    private async Task<RootWriteSnapshot?> ReadMutationSnapshotAsync(WorldAuthorityIdentity identity, WorldAuthorityFence fence, CancellationToken cancellationToken) {
        var current = await ReadRootSnapshotAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (current is { } snapshot) return new RootWriteSnapshot(
            CreateOnly: false,
            Snapshot: snapshot
        );
        // An absent root is an empty world; the first unowned publisher creates it, and the create-only root write
        // decides which concurrent initializer becomes authoritative.
        return (IsUnownedFence(fence: fence)
            ? new RootWriteSnapshot(
                CreateOnly: true,
                Snapshot: new WorldAuthorityRootSnapshot(
                    Root: WorldAuthorityRoot.Empty,
                    VersionToken: string.Empty
                )
            )
            : null
        );
    }
    private async Task<WorldAuthorityFence?> EnsureFenceAsync(WorldAuthorityIdentity identity, WorldAuthorityFence? supplied, CancellationToken cancellationToken) {
        if (supplied is { } fence) return ((IsUnownedFence(fence: fence) || ((fence.Epoch > 0) && (fence.Token != Guid.Empty)))
            ? fence
            : null
        );
        var current = await ReadRootSnapshotAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (((current is { } snapshot) && (snapshot.Root.FenceToken != Guid.Empty))
            ? null
            : WorldAuthorityFence.Unowned
        );
    }
    private async Task<WorldAuthorityStoreOutcome> ReconcileAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot expected, WorldAuthorityOperationReceipt? receipt, CancellationToken cancellationToken) {
        try {
            var current = await ReadRootSnapshotAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (current is { } found) &&
                (found.Root == expected)
            ) return WorldAuthorityStoreOutcome.Success(
                detail: "CAS outcome reconciled",
                root: found
            );
            if (
                (receipt is { } wanted) &&
                (current is { } root) &&
                (await FindReceiptFromRootAsync(
                identity,
                root.Root,
                wanted.OperationId,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false) is { } actual) &&
                (actual == wanted)
            ) return WorldAuthorityStoreOutcome.Success(
                detail: "receipt reconciled",
                root: root
            );
        } catch { }
        return WorldAuthorityStoreOutcome.RecoveryRequired(detail: "root CAS outcome is uncertain; reconcile by reading the root and receipt chain");
    }

    /// <inheritdoc/>
    public async Task<WorldAuthorityRootSnapshot?> LoadRootAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) => await ReadRootSnapshotAsync(
        cancellationToken: cancellationToken,
        identity: identity
    ).ConfigureAwait(continueOnCapturedContext: false);
    /// <inheritdoc/>
    public async Task<WorldRecoveryRootReference?> CaptureRecoveryRootAsync(WorldAuthorityIdentity identity, Guid operationId, CancellationToken cancellationToken) {
        ValidateRecoveryRequest(
            identity: identity,
            operationId: operationId
        );
        var address = RecoveryRootAddress(
            identity: identity,
            operationId: operationId
        );

        if (await ReadAsync(
            address: address,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false) is { } existing) {
            return await ReadRecoveryReferenceAsync(
                identity,
                operationId,
                existing.Content,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        var snapshot = await ReadRootSnapshotAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (snapshot is not { } captured) return null;
        await VerifyRecoveryPayloadsAsync(
            identity,
            captured.Root,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        var bytes = WorldAuthorityRecoveryRootCodec.Encode(
            identity: identity,
            operationId: operationId,
            root: captured
        );
        var written = await UnderTimeoutAsync(
            cancellationToken: cancellationToken,
            op: ct => m_store.WriteAsync(
                m_target,
                address,
                bytes,
                ObjectBlobWriteMode.CreateOnly,
                cancellationToken: ct
            )
        ).ConfigureAwait(continueOnCapturedContext: false);
        var durable = (written.Succeeded
            ? new ObjectBlobContent(
                Content: bytes,
                VersionToken: written.VersionToken
            )
            : (await ReadAsync(
                address: address,
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false) ?? throw new IOException(message: "recovery root could not be persisted"))
        );
        // One point per world and operation. A retry never replaces the first frozen source with a later candidate.
        return await ReadRecoveryReferenceAsync(
            identity,
            operationId,
            durable.Content,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Finds an already protected operation root without capturing current state. A release controller
    /// uses this after losing the source worker's drain response or restarting after that worker stopped.</summary>
    /// <param name="identity">The stable world identity.</param>
    /// <param name="operationId">The exact durable release operation.</param>
    /// <param name="cancellationToken">Cancels the storage read.</param>
    /// <returns>The validated immutable root, or null if this operation has not protected the world.</returns>
    public async Task<WorldRecoveryRootReference?> FindRecoveryRootAsync(WorldAuthorityIdentity identity, Guid operationId, CancellationToken cancellationToken) {
        ValidateRecoveryRequest(
            identity: identity,
            operationId: operationId
        );
        var existing = await ReadAsync(
            address: RecoveryRootAddress(
                identity: identity,
                operationId: operationId
            ),
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return ((existing is null)
            ? null
            : await ReadRecoveryReferenceAsync(
                identity,
                operationId,
                existing.Value.Content,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false)
        );
    }
    /// <inheritdoc/>
    public async Task<WorldRecoveryRootReference?> LoadRecoveryRootAsync(WorldAuthorityIdentity identity, string pin, Guid operationId, CancellationToken cancellationToken) {
        ValidateRecoveryRequest(
            identity: identity,
            operationId: operationId
        );
        if (!ContentPin.TryParse(pin: out _, text: pin)) throw new InvalidDataException(message: "recovery-root pin is not a full sha256 pin");
        var content = await ReadAsync(
            address: RecoveryRootAddress(
                identity: identity,
                operationId: operationId
            ),
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (content is not { } found) return null;
        if (!string.Equals(
            a: ContentPin.Compute(content: found.Content.Span).ToString(),
            b: pin,
            comparisonType: StringComparison.Ordinal
        )) throw new InvalidDataException(message: "recovery-root content does not match its pin");
        return await ReadRecoveryReferenceAsync(
            identity,
            operationId,
            found.Content,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

    private async Task<WorldRecoveryRootReference> ReadRecoveryReferenceAsync(WorldAuthorityIdentity identity, Guid operationId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) {
        if (!WorldAuthorityRecoveryRootCodec.TryDecode(
            bytes.Span,
            identity,
            operationId,
            out var reference,
            out var reason
        )) throw new InvalidDataException(message: $"recovery-root is corrupt — {reason}");
        await VerifyRecoveryPayloadsAsync(
            identity,
            reference.Root,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        return reference;
    }
    private async Task VerifyRecoveryPayloadsAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, CancellationToken cancellationToken) {
        async Task VerifyAsync(ObjectBlobAddress address, string hash) {
            var bytes = await ReadAsync(
                address: address,
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (bytes is not { } found) ||
                !string.Equals(
                a: WorldDefinitionFileSource.ComputeContentHash(content: found.Content.Span),
                b: hash,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                throw new InvalidDataException(message: $"protected recovery payload '{address.Key}' is missing or corrupt");
            }
        }
        if (root.DefinitionHash is { } definition) {
            await VerifyAsync(
            address: DefinitionCandidateAddress(
                hash: definition,
                identity: identity
            ),
            hash: definition
        ).ConfigureAwait(continueOnCapturedContext: false);
        }
        if (root.CheckpointHash is { } checkpoint) {
            await VerifyAsync(
            address: CheckpointCandidateAddress(
                identity,
                root.CheckpointOrdinal,
                checkpoint
            ),
            hash: checkpoint
        ).ConfigureAwait(continueOnCapturedContext: false);
        }
        _ = await ReadJournalForRootAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            root: root
        ).ConfigureAwait(continueOnCapturedContext: false);
        var receipts = await LoadReceiptIndexAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            root: root
        ).ConfigureAwait(continueOnCapturedContext: false);

        foreach (var receipt in receipts) {
            if ((await ReadReceiptAsync(
                identity,
                receipt.Value,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false)).OperationId != receipt.Key) {
                throw new InvalidDataException(message: "protected receipt index does not match its payload");
            }
        }
        if (
            (root.ReceiptHash is { } head) &&
            !receipts.Values.Contains(
            head,
            StringComparer.Ordinal
        )
        ) {
            throw new InvalidDataException(message: "protected receipt chain is missing from its index");
        }
    }

    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> RestoreRecoveryRootAsync(WorldAuthorityIdentity identity, string pin, Guid operationId, WorldAuthorityFence expectedCurrentFence, CancellationToken cancellationToken) {
        ValidateRecoveryRequest(
            identity: identity,
            operationId: operationId
        );
        if (
            (expectedCurrentFence.Epoch <= 0) ||
            (expectedCurrentFence.Token == Guid.Empty)
        ) return WorldAuthorityStoreOutcome.StaleFence(detail: "recovery requires a current owned activation fence");
        var reference = await LoadRecoveryRootAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            operationId: operationId,
            pin: pin
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (reference is not { } saved) return WorldAuthorityStoreOutcome.Failed(detail: "recovery-root pin is missing");
        var current = await ReadRootSnapshotAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (current is not { } present) ||
            !FenceMatches(
            fence: expectedCurrentFence,
            snapshot: present
        )
        ) return WorldAuthorityStoreOutcome.StaleFence(detail: "activation fence is no longer current");
        var restored = saved.Root with {
            Epoch = checked((Math.Max(
            val1: present.Root.Epoch,
            val2: saved.Root.Epoch
        ) + 1L)),
            FenceToken = Guid.Empty,
            Sequence = checked((Math.Max(
            val1: present.Root.Sequence,
            val2: saved.Root.Sequence
        ) + 1L)),
        };
        var write = await WriteRootAsync(
            identity,
            restored,
            present.VersionToken,
            ObjectBlobWriteMode.Overwrite,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (write.Succeeded) return WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(
            Root: restored,
            VersionToken: (write.VersionToken ?? string.Empty)
        ));
        return (write.PreconditionFailed
            ? WorldAuthorityStoreOutcome.PreconditionFailed(detail: "authority root moved before recovery restore")
            : WorldAuthorityStoreOutcome.Failed(detail: "recovery restore root write was refused")
        );
    }

    private static void ValidateRecoveryRequest(WorldAuthorityIdentity identity, Guid operationId) {
        if (identity.Owner == Guid.Empty) throw new ArgumentException(
            message: "recovery-root owner must be non-empty",
            paramName: nameof(identity)
        );
        if (string.IsNullOrWhiteSpace(value: identity.World.Value)) throw new ArgumentException(
            message: "recovery-root world must be non-empty",
            paramName: nameof(identity)
        );
        if (operationId == Guid.Empty) throw new ArgumentException(
            message: "recovery-root operation must be non-empty",
            paramName: nameof(operationId)
        );
    }

    /// <inheritdoc/>
    public async Task<WorldAuthorityFence?> AcquireActivationAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        for (var attempt = 0; (attempt < MaxCasAttempts); attempt++) {
            var current = await ReadRootSnapshotAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (current is null) {
                var token = Guid.NewGuid();
                var candidate = WorldAuthorityRoot.Empty with { Epoch = 1, FenceToken = token, Sequence = 1 };
                var created = await WriteRootAsync(
                    cancellationToken: cancellationToken,
                    identity: identity,
                    ifMatchVersion: null,
                    mode: ObjectBlobWriteMode.CreateOnly,
                    root: candidate
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (
                    created.Succeeded &&
                    (created.VersionToken is { Length: > 0 } createdVersion)
                ) return new WorldAuthorityFence(
                    candidate.Epoch,
                    token,
                    createdVersion
                );
                continue;
            }
            var nextToken = Guid.NewGuid();
            var next = current.Value.Root with { Epoch = checked((current.Value.Root.Epoch + 1)), FenceToken = nextToken, Sequence = checked((current.Value.Root.Sequence + 1)) };
            var result = await WriteRootAsync(
                identity,
                next,
                current.Value.VersionToken,
                ObjectBlobWriteMode.Overwrite,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                result.Succeeded &&
                (result.VersionToken is { Length: > 0 } version)
            ) return new WorldAuthorityFence(
                next.Epoch,
                nextToken,
                version
            );
        }
        return null;
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> ReleaseActivationAsync(WorldAuthorityIdentity identity, WorldAuthorityFence fence, CancellationToken cancellationToken) {
        var current = await ReadRootSnapshotAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (current is not { } snapshot) ||
            !FenceMatches(
            fence: fence,
            snapshot: snapshot
        )
        ) return WorldAuthorityStoreOutcome.StaleFence(detail: "activation fence is no longer current");
        var released = snapshot.Root with { Epoch = checked((snapshot.Root.Epoch + 1)), FenceToken = Guid.Empty, Sequence = checked((snapshot.Root.Sequence + 1)) };
        var result = await WriteRootAsync(
            identity,
            released,
            snapshot.VersionToken,
            ObjectBlobWriteMode.Overwrite,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (result.Succeeded
            ? WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(
                Root: released,
                VersionToken: (result.VersionToken ?? string.Empty)
            ))
            : WorldAuthorityStoreOutcome.PreconditionFailed(detail: "activation root moved before release")
        );
    }

    private async Task<WorldAuthorityOperationReceipt?> FindReceiptFromRootAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, Guid operationId, CancellationToken cancellationToken) {
        if (root.ReceiptIndexHash is { Length: > 0 }) {
            var index = await LoadReceiptIndexAsync(
                cancellationToken: cancellationToken,
                identity: identity,
                root: root
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!index.TryGetValue(
                key: operationId,
                value: out var indexedHash
            )) return null;
            var indexedReceipt = await ReadReceiptAsync(
                cancellationToken: cancellationToken,
                hash: indexedHash,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (indexedReceipt.OperationId != operationId) throw new InvalidDataException(message: "receipt index target operation does not match its dictionary key");
            return indexedReceipt;
        }
        var hash = root.ReceiptHash;

        for (var count = 0; (hash is { Length: > 0 }); count++) {
            if (count >= 100_000) throw new InvalidDataException(message: "receipt chain exceeds its traversal bound");
            var content = await ReadAsync(
                address: ReceiptCandidateAddress(
                    hash: hash,
                    identity: identity
                ),
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (content is not { } found) throw new InvalidDataException(message: "receipt chain names a missing blob");
            if (!string.Equals(
                a: WorldDefinitionFileSource.ComputeContentHash(content: found.Content.Span),
                b: hash,
                comparisonType: StringComparison.Ordinal
            )) throw new InvalidDataException(message: "receipt chain content pin mismatch");
            if (!WorldAuthorityRootCodec.TryDecodeReceipt(
                found.Content.Span,
                out var receipt,
                out var previous,
                out var reason
            )) throw new InvalidDataException(message: $"receipt chain is corrupt — {reason}");
            if (receipt.OperationId == operationId) return receipt;
            hash = previous;
        }
        return null;
    }
    private async Task<WorldAuthorityOperationReceipt> ReadReceiptAsync(WorldAuthorityIdentity identity, string hash, CancellationToken cancellationToken) {
        var indexed = await ReadAsync(
            address: ReceiptCandidateAddress(
                hash: hash,
                identity: identity
            ),
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (indexed is not { } indexedBlob) throw new InvalidDataException(message: "receipt index target is missing");
        if (!string.Equals(
            a: WorldDefinitionFileSource.ComputeContentHash(content: indexedBlob.Content.Span),
            b: hash,
            comparisonType: StringComparison.Ordinal
        )) throw new InvalidDataException(message: "receipt index target does not match its content pin");
        if (!WorldAuthorityRootCodec.TryDecodeReceipt(
            indexedBlob.Content.Span,
            out var indexedReceipt,
            out _,
            out var indexedReason
        )) throw new InvalidDataException(message: $"receipt index target is corrupt — {indexedReason}");
        return indexedReceipt;
    }

    /// <inheritdoc/>
    public async Task<WorldAuthorityOperationReceipt?> FindOperationReceiptAsync(WorldAuthorityIdentity identity, Guid operationId, CancellationToken cancellationToken) {
        var root = await ReadRootSnapshotAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        return ((root is { } snapshot)
            ? await FindReceiptFromRootAsync(
                identity,
                snapshot.Root,
                operationId,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false)
            : null
        );
    }

    private static bool SameOperation(WorldAuthorityOperationReceipt left, WorldAuthorityOperationReceipt right) => ((left.OperationId == right.OperationId) && string.Equals(
        a: left.Actor,
        b: right.Actor,
        comparisonType: StringComparison.Ordinal
    ) && string.Equals(
        a: left.PayloadDigest,
        b: right.PayloadDigest,
        comparisonType: StringComparison.Ordinal
    ));
    private async Task<Dictionary<Guid, string>> LoadReceiptIndexAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, CancellationToken cancellationToken) {
        var index = new Dictionary<Guid, string>();

        if (root.ReceiptIndexHash is not { Length: > 0 } hash) return index;
        var content = await ReadAsync(
            address: ReceiptIndexAddress(
                hash: hash,
                identity: identity
            ),
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (content is not { } found) ||
            !string.Equals(
            a: WorldDefinitionFileSource.ComputeContentHash(content: found.Content.Span),
            b: hash,
            comparisonType: StringComparison.Ordinal
        )
        ) throw new InvalidDataException(message: "receipt index does not match the root content pin");
        var parsed = (JsonSerializer.Deserialize<Dictionary<Guid, string>>(found.Content.Span) ?? throw new InvalidDataException(message: "receipt index is empty or malformed"));

        foreach (var pair in parsed) {
            if (
                (pair.Key == Guid.Empty) ||
                !AssetContentHash.TryParse(
                hash: out _,
                text: pair.Value
            )
            ) throw new InvalidDataException(message: "receipt index contains an invalid entry");
            index.Add(
                key: pair.Key,
                value: pair.Value
            );
        }
        return index;
    }
    private async Task<(WorldAuthorityStoreOutcome Outcome, string? ReceiptHash, string? ReceiptIndexHash)> PrepareReceiptAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, WorldAuthorityOperationReceipt? receipt, bool requireApplied, CancellationToken cancellationToken) {
        if (receipt is not { } value) return (WorldAuthorityStoreOutcome.Success(), root.ReceiptHash, root.ReceiptIndexHash);
        if (
            (value.OperationId == Guid.Empty) ||
            string.IsNullOrWhiteSpace(value: value.Actor) ||
            string.IsNullOrWhiteSpace(value: value.PayloadDigest) ||
            string.IsNullOrWhiteSpace(value: value.DecisionCode)
        ) return (WorldAuthorityStoreOutcome.Failed(detail: "receipt fields are incomplete"), null, null);
        var receiptIndex = await LoadReceiptIndexAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            root: root
        ).ConfigureAwait(continueOnCapturedContext: false);
        WorldAuthorityOperationReceipt? existing = null;

        if (receiptIndex.TryGetValue(
            key: value.OperationId,
            value: out var existingHash
        )) {
            existing = await ReadReceiptAsync(
                cancellationToken: cancellationToken,
                hash: existingHash,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);
            if (existing.Value.OperationId != value.OperationId) throw new InvalidDataException(message: "receipt index target operation does not match its dictionary key");
        }
        if (existing is { } found) return (SameOperation(
            left: found,
            right: value
        )
            ? (WorldAuthorityStoreOutcome.Success("operation already durable"), root.ReceiptHash, root.ReceiptIndexHash)
            : (WorldAuthorityStoreOutcome.OperationConflict(detail: "operation id is bound to a different actor or payload"), null, null)
        );
        if (requireApplied != value.Applied) return (WorldAuthorityStoreOutcome.PreconditionFailed(detail: (requireApplied
            ? "journal or checkpoint receipts must record an applied mutation"
            : "standalone receipts may record refusals only")), null, null);
        var bytes = WorldAuthorityRootCodec.EncodeReceipt(
            value,
            root.ReceiptHash
        );
        var hash = WorldDefinitionFileSource.ComputeContentHash(content: bytes);
        var write = await PutImmutableAsync(
            address: ReceiptCandidateAddress(
                hash: hash,
                identity: identity
            ),
            bytes: bytes,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!write.Ok) return (write, null, null);
        var index = receiptIndex;

        index[value.OperationId] = hash;
        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(new SortedDictionary<Guid, string>(dictionary: index));
        var indexHash = WorldDefinitionFileSource.ComputeContentHash(content: indexBytes);
        var indexWrite = await PutImmutableAsync(
            address: ReceiptIndexAddress(
                hash: indexHash,
                identity: identity
            ),
            bytes: indexBytes,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (indexWrite.Ok
            ? (indexWrite, hash, indexHash)
            : (indexWrite, null, null)
        );
    }
    private async Task<(IReadOnlyList<WorldMutationJournalEntry> Entries, string? Hash)> ReadJournalForRootAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, CancellationToken cancellationToken) {
        if (root.JournalHash is not { Length: > 0 } hash) {
            if (
                (root.JournalEntryCount != 0) ||
                (root.JournalSequence != root.CheckpointCoverageSequence)
            ) throw new InvalidDataException(message: "root has journal metadata without a journal blob");
            return ([], null);
        }
        var content = await ReadAsync(
            address: JournalCandidateAddress(
                hash: hash,
                identity: identity
            ),
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (content is not { } found) throw new InvalidDataException(message: "root names a missing journal blob");
        if (!string.Equals(
            a: WorldDefinitionFileSource.ComputeContentHash(content: found.Content.Span),
            b: hash,
            comparisonType: StringComparison.Ordinal
        )) throw new InvalidDataException(message: "journal blob does not match the root content pin");
        if (!WorldAuthorityStoreWireCodec.TryDecodeJournalPage(
            bytes: found.Content.Span,
            entries: out var entries,
            reason: out var reason
        )) throw new InvalidDataException(message: $"journal blob is corrupt — {reason}");
        if (
            (entries.Count != root.JournalEntryCount) ||
            (((long)entries.Count) != (root.JournalSequence - root.CheckpointCoverageSequence))
        ) throw new InvalidDataException(message: "root journal count and sequence disagree");
        return (entries, hash);
    }

    /// <inheritdoc/>
    public async Task<WorldAuthorityRecovery?> LoadRecoveryAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var snapshot = await ReadRootSnapshotAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (snapshot is not { } rooted) return null;
        WorldAuthorityCheckpointBlob? checkpoint = null;

        if (rooted.Root.CheckpointHash is { Length: > 0 } hash) {
            var content = await ReadAsync(
                address: CheckpointCandidateAddress(
                    identity,
                    rooted.Root.CheckpointOrdinal,
                    hash
                ),
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (content is not { } found) throw new InvalidDataException(message: "root names a missing checkpoint blob");
            if (!string.Equals(
                a: WorldDefinitionFileSource.ComputeContentHash(content: found.Content.Span),
                b: hash,
                comparisonType: StringComparison.Ordinal
            )) throw new InvalidDataException(message: "checkpoint blob does not match the root content pin");
            checkpoint = new WorldAuthorityCheckpointBlob(
                Encoded: found.Content,
                Ordinal: rooted.Root.CheckpointOrdinal,
                Tick: rooted.Root.CheckpointTick
            );
        }
        var journal = await ReadJournalForRootAsync(
            identity,
            rooted.Root,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        ObjectBlobContent? definitionBytes = null;

        if (rooted.Root.DefinitionHash is { } definitionHash) {
            var content = await ReadAsync(
                address: DefinitionCandidateAddress(
                    hash: definitionHash,
                    identity: identity
                ),
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (content is not { } found) throw new InvalidDataException(message: "root names a missing definition blob");
            if (!string.Equals(
                a: WorldDefinitionFileSource.ComputeContentHash(content: found.Content.Span),
                b: definitionHash,
                comparisonType: StringComparison.Ordinal
            )) throw new InvalidDataException(message: "definition blob does not match the root content pin");
            definitionBytes = found;
        }
        WorldDefinitionAdmission? admission = null;

        if (definitionBytes is { } bytes) {
            var resolver = new WorldStorageNeighbourResolver(
                m_store,
                m_target,
                identity.Owner,
                WorldStorageNamespace.Hosted,
                m_clock
            );
            var loaded = await WorldDefinitionLoader.LoadAsync(
                bytes.Content,
                $"authority/{identity.World.Value}/definition",
                identity.World.Value,
                resolver.ResolveHostedAsync,
                cancellationToken,
                catalog: m_machines
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (loaded.Admission is null) throw new InvalidDataException(message: $"root-qualified definition is invalid — {loaded.Reason}");
            admission = loaded.Admission;
        }
        return new WorldAuthorityRecovery(
            rooted,
            checkpoint,
            new WorldMutationJournalTail(
                CheckpointOrdinal: rooted.Root.CheckpointOrdinal,
                Entries: journal.Entries
            )
        ) { Admission = admission };
    }

    private async Task<WorldAuthorityStoreOutcome> AppendRootAsync(WorldAuthorityIdentity identity, WorldMutationJournalEntry entry, WorldAuthorityFence? suppliedFence, WorldAuthorityOperationReceipt? receipt, CancellationToken cancellationToken) {
        var fence = await EnsureFenceAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            supplied: suppliedFence
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (fence is not { } active) return WorldAuthorityStoreOutcome.Failed(detail: "could not acquire activation fence");
        for (var attempt = 0; (attempt < MaxCasAttempts); attempt++) {
            var writable = await ReadMutationSnapshotAsync(
                cancellationToken: cancellationToken,
                fence: active,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (writable is not { } state) return WorldAuthorityStoreOutcome.StaleFence(detail: "activation fence is no longer current");
            var current = state.Snapshot;

            if (!FenceMatches(
                fence: active,
                snapshot: current
            )) return WorldAuthorityStoreOutcome.StaleFence(detail: "activation fence is no longer current");
            if (current.Root.CheckpointHash is null) return WorldAuthorityStoreOutcome.Failed(detail: "no checkpoint exists yet — a journal is relative to one");
            var existing = await ReadJournalForRootAsync(
                identity,
                current.Root,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
            var prepared = await PrepareReceiptAsync(
                identity,
                current.Root,
                receipt,
                requireApplied: true,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!prepared.Outcome.Ok) return prepared.Outcome;
            if (
                (receipt is { }) &&
                (prepared.Outcome.Detail == "operation already durable")
            ) return WorldAuthorityStoreOutcome.Success(
                detail: prepared.Outcome.Detail,
                root: current
            );
            var entries = new List<WorldMutationJournalEntry>(capacity: (existing.Entries.Count + 1));

            entries.AddRange(collection: existing.Entries); entries.Add(item: entry);
            var journalBytes = WorldAuthorityStoreWireCodec.EncodeJournalPage(entries: entries);
            var journalHash = WorldDefinitionFileSource.ComputeContentHash(content: journalBytes);
            var immutable = await PutImmutableAsync(
                address: JournalCandidateAddress(
                    hash: journalHash,
                    identity: identity
                ),
                bytes: journalBytes,
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!immutable.Ok) return immutable;
            var next = current.Root with { JournalHash = journalHash, JournalEntryCount = entries.Count, JournalSequence = checked((current.Root.JournalSequence + 1)), ReceiptHash = prepared.ReceiptHash, ReceiptIndexHash = prepared.ReceiptIndexHash, Sequence = checked((current.Root.Sequence + 1)) };
            ObjectBlobWriteResult result;

            try {
                result = await WriteRootAsync(
                identity,
                next,
                (state.CreateOnly
                ? null
                : current.VersionToken),
                (state.CreateOnly
                ? ObjectBlobWriteMode.CreateOnly
                : ObjectBlobWriteMode.Overwrite),
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
            } catch {
                return await ReconcileAsync(
                cancellationToken: cancellationToken,
                expected: next,
                identity: identity,
                receipt: receipt
            ).ConfigureAwait(continueOnCapturedContext: false);
            }
            if (result.Succeeded) return WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(
                Root: next,
                VersionToken: (result.VersionToken ?? string.Empty)
            ));
        }
        return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "journal append lost the root compare-and-swap race");
    }
    private async Task<WorldAuthorityStoreOutcome> WriteCheckpointRootAsync(WorldAuthorityIdentity identity, ReadOnlyMemory<byte> encoded, ulong tick, WorldAuthorityFence? suppliedFence, WorldAuthorityOperationReceipt? receipt, long? capturedJournalSequence, CancellationToken cancellationToken) {
        var fence = await EnsureFenceAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            supplied: suppliedFence
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (fence is not { } active) return WorldAuthorityStoreOutcome.Failed(detail: "could not acquire activation fence");
        var checkpointHash = WorldDefinitionFileSource.ComputeContentHash(content: encoded.Span);

        for (var attempt = 0; (attempt < MaxCasAttempts); attempt++) {
            var writable = await ReadMutationSnapshotAsync(
                cancellationToken: cancellationToken,
                fence: active,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (writable is not { } state) return WorldAuthorityStoreOutcome.StaleFence(detail: "activation fence is no longer current");
            var current = state.Snapshot;

            if (!FenceMatches(
                fence: active,
                snapshot: current
            )) return WorldAuthorityStoreOutcome.StaleFence(detail: "activation fence is no longer current");
            if (
                (current.Root.CheckpointOrdinal >= 0) &&
                (tick < current.Root.CheckpointTick)
            ) return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "checkpoint tick regresses the authoritative checkpoint");
            var coverage = (capturedJournalSequence ?? ((current.Root.JournalSequence == current.Root.CheckpointCoverageSequence)
                ? current.Root.JournalSequence
                : long.MinValue));

            if (coverage == long.MinValue) return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "checkpoint requires an explicit captured journal sequence while a later tail exists");
            if (
                (coverage < current.Root.CheckpointCoverageSequence) ||
                (coverage > current.Root.JournalSequence)
            ) return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "checkpoint capture is stale or ahead of the authoritative journal");
            var existing = await ReadJournalForRootAsync(
                identity,
                current.Root,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
            var firstSequence = checked((current.Root.CheckpointCoverageSequence + 1));
            var suffixOffset = checked((int)Math.Max(
                val1: 0L,
                val2: ((coverage - firstSequence) + 1)
            ));

            if (suffixOffset > existing.Entries.Count) return WorldAuthorityStoreOutcome.Failed(detail: "checkpoint journal coverage is inconsistent");
            var suffix = existing.Entries.Skip(count: suffixOffset).ToArray();
            string? journalHash = null;

            if (suffix.Length > 0) {
                var journalBytes = WorldAuthorityStoreWireCodec.EncodeJournalPage(entries: suffix);

                journalHash = WorldDefinitionFileSource.ComputeContentHash(content: journalBytes);
                var journalWrite = await PutImmutableAsync(
                    address: JournalCandidateAddress(
                        hash: journalHash,
                        identity: identity
                    ),
                    bytes: journalBytes,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!journalWrite.Ok) return journalWrite;
            }
            var ordinal = checked((current.Root.CheckpointOrdinal + 1));
            var checkpointWrite = await PutImmutableAsync(
                address: CheckpointCandidateAddress(
                    hash: checkpointHash,
                    identity: identity,
                    ordinal: ordinal
                ),
                bytes: encoded,
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!checkpointWrite.Ok) return checkpointWrite;
            var prepared = await PrepareReceiptAsync(
                identity,
                current.Root,
                receipt,
                requireApplied: true,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!prepared.Outcome.Ok) return prepared.Outcome;
            if (
                (receipt is { }) &&
                (prepared.Outcome.Detail == "operation already durable")
            ) return WorldAuthorityStoreOutcome.Success(
                detail: prepared.Outcome.Detail,
                root: current
            );
            var next = current.Root with { CheckpointHash = checkpointHash, CheckpointOrdinal = ordinal, CheckpointTick = tick, JournalHash = journalHash, JournalEntryCount = suffix.Length, CheckpointCoverageSequence = coverage, ReceiptHash = prepared.ReceiptHash, ReceiptIndexHash = prepared.ReceiptIndexHash, DurableOrdinal = ordinal, DurableTick = tick, Sequence = checked((current.Root.Sequence + 1)) };

            try {
                var result = await WriteRootAsync(
                    identity,
                    next,
                    (state.CreateOnly
                    ? null
                    : current.VersionToken),
                    (state.CreateOnly
                    ? ObjectBlobWriteMode.CreateOnly
                    : ObjectBlobWriteMode.Overwrite),
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (result.Succeeded) return WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(
                    Root: next,
                    VersionToken: (result.VersionToken ?? string.Empty)
                ));
            } catch {
                return await ReconcileAsync(
                cancellationToken: cancellationToken,
                expected: next,
                identity: identity,
                receipt: receipt
            ).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
        return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "checkpoint publication lost the root compare-and-swap race");
    }
    private async Task<WorldAuthorityStoreOutcome> PublishDefinitionRootAsync(WorldAuthorityIdentity identity, byte[] bytes, WorldAuthorityFence? suppliedFence, CancellationToken cancellationToken) {
        var fence = await EnsureFenceAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            supplied: suppliedFence
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (fence is not { } active) return WorldAuthorityStoreOutcome.Failed(detail: "could not acquire activation fence");
        var hash = WorldDefinitionFileSource.ComputeContentHash(content: bytes);

        for (var attempt = 0; (attempt < MaxCasAttempts); attempt++) {
            var writable = await ReadMutationSnapshotAsync(
                cancellationToken: cancellationToken,
                fence: active,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (writable is not { } state) return WorldAuthorityStoreOutcome.StaleFence(detail: "activation fence is no longer current");
            var current = state.Snapshot;

            if (!FenceMatches(
                fence: active,
                snapshot: current
            )) return WorldAuthorityStoreOutcome.StaleFence(detail: "activation fence is no longer current");
            if (current.Root.DefinitionHash == hash) return WorldAuthorityStoreOutcome.Success(
                detail: "definition already durable",
                root: current
            );
            var immutable = await PutImmutableAsync(
                address: DefinitionCandidateAddress(
                    hash: hash,
                    identity: identity
                ),
                bytes: bytes,
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!immutable.Ok) return immutable;
            var next = current.Root with { DefinitionHash = hash, Sequence = checked((current.Root.Sequence + 1)) };

            try {
                var result = await WriteRootAsync(
                    identity,
                    next,
                    (state.CreateOnly
                    ? null
                    : current.VersionToken),
                    (state.CreateOnly
                    ? ObjectBlobWriteMode.CreateOnly
                    : ObjectBlobWriteMode.Overwrite),
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (result.Succeeded) return WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(
                    Root: next,
                    VersionToken: (result.VersionToken ?? string.Empty)
                ));
            } catch {
                return await ReconcileAsync(
                cancellationToken: cancellationToken,
                expected: next,
                identity: identity,
                receipt: null
            ).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
        return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "definition publication lost the root compare-and-swap race");
    }

    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> RecordReceiptAsync(WorldAuthorityIdentity identity, WorldAuthorityOperationReceipt receipt, CancellationToken cancellationToken, WorldAuthorityFence? suppliedFence) {
        var fence = await EnsureFenceAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            supplied: suppliedFence
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (fence is not { } active) return WorldAuthorityStoreOutcome.Failed(detail: "could not acquire activation fence");
        for (var attempt = 0; (attempt < MaxCasAttempts); attempt++) {
            var writable = await ReadMutationSnapshotAsync(
                cancellationToken: cancellationToken,
                fence: active,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (writable is not { } state) return WorldAuthorityStoreOutcome.StaleFence(detail: "activation fence is no longer current");
            var current = state.Snapshot;

            if (!FenceMatches(
                fence: active,
                snapshot: current
            )) return WorldAuthorityStoreOutcome.StaleFence(detail: "activation fence is no longer current");
            var prepared = await PrepareReceiptAsync(
                identity,
                current.Root,
                receipt,
                requireApplied: false,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!prepared.Outcome.Ok) return prepared.Outcome;
            if (prepared.Outcome.Detail == "operation already durable") return WorldAuthorityStoreOutcome.Success(
                detail: prepared.Outcome.Detail,
                root: current
            );
            var next = current.Root with { ReceiptHash = prepared.ReceiptHash, ReceiptIndexHash = prepared.ReceiptIndexHash, Sequence = checked((current.Root.Sequence + 1)) };

            try {
                var result = await WriteRootAsync(
                    identity,
                    next,
                    (state.CreateOnly
                    ? null
                    : current.VersionToken),
                    (state.CreateOnly
                    ? ObjectBlobWriteMode.CreateOnly
                    : ObjectBlobWriteMode.Overwrite),
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (result.Succeeded) return WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(
                    Root: next,
                    VersionToken: (result.VersionToken ?? string.Empty)
                ));
            } catch {
                return await ReconcileAsync(
                cancellationToken: cancellationToken,
                expected: next,
                identity: identity,
                receipt: receipt
            ).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
        return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "receipt publication lost the root compare-and-swap race");
    }
#pragma warning restore IDE0011
}
