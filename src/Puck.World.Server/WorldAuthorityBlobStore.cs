using Puck.Abstractions;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Assets;
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
    private readonly string? m_documentDirectory;
    private readonly IMachineValidationCatalog? m_machines;
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;

    /// <summary>The per-blob budget, in bytes, a directory target serving this store must admit: the bound of an encoded
    /// checkpoint (<see cref="WorldAuthorityCheckpointCodec.MaxEncodedBytes"/>).</summary>
    public const int MaximumBlobBytes = WorldAuthorityCheckpointCodec.MaxEncodedBytes;

    /// <summary>Creates a local directory target sized to hold every checkpoint an authority store writes.</summary>
    /// <param name="rootPath">The root directory the blobs live under; it is resolved to an absolute path now.</param>
    /// <returns>A target whose per-blob budget is <see cref="MaximumBlobBytes"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="rootPath"/> is <see langword="null"/> or white space.</exception>
    public static DirectoryObjectStorageTarget DirectoryTarget(string rootPath) => new(
        maximumBlobBytes: MaximumBlobBytes,
        rootPath: rootPath
    );

    /// <summary>Initializes the store.</summary>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The extension-supplied storage target — persistent storage in deployment, a directory for local runs and the
    /// canary. A directory target admits at least <see cref="MaximumBlobBytes"/> per blob.</param>
    /// <param name="machines">The catalog a recovery read validates a published definition against, so its receipt
    /// is one the hosting activation can accept; null defers provider checks and the activation admits for itself.</param>
    /// <param name="timeProvider">The host clock every <see cref="OperationTimeout"/> runs on; <see langword="null"/> is
    /// <see cref="TimeProvider.System"/>.</param>
    /// <param name="documentDirectory">The directory a recovered definition's relative paths resolve beside
    /// (<see cref="WorldDefinition.DocumentDirectory"/>): the host's layout of the hosted documents, such as a silo
    /// document's <see cref="WorldSiloDefinition.DocumentDirectory"/>; <see langword="null"/> gives them none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> is a directory target whose per-blob budget is below
    /// <see cref="MaximumBlobBytes"/>.</exception>
    public WorldAuthorityBlobStore(IObjectBlobStore store, ObjectStorageTarget target, IMachineValidationCatalog? machines = null, TimeProvider? timeProvider = null, string? documentDirectory = null) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);
        // The one door every authority store passes: a local target too small for the largest checkpoint the codec
        // admits would refuse that checkpoint at its first write, long after the host reported ready.
        if (target is DirectoryObjectStorageTarget { MaximumBlobBytes: < MaximumBlobBytes } local) {
            throw new ArgumentException(
                message: $"A world authority store's directory target admits {local.MaximumBlobBytes} bytes per blob; a checkpoint may take {MaximumBlobBytes}. Size it with {nameof(WorldAuthorityBlobStore)}.{nameof(DirectoryTarget)}.",
                paramName: nameof(target)
            );
        }

        m_clock = (timeProvider ?? TimeProvider.System);
        m_documentDirectory = documentDirectory;
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
    public Task<WorldAuthorityStoreOutcome> AppendJournalAsync(WorldAuthorityIdentity identity, WorldAuthorityJournalEntry entry, CancellationToken cancellationToken, WorldAuthorityFence? fence = null, WorldAuthorityOperationReceipt? receipt = null) =>
        AppendRootAsync(identity, [entry], fence, receipt, cancellationToken);

    /// <inheritdoc/>
    public Task<WorldAuthorityStoreOutcome> AppendJournalBatchAsync(WorldAuthorityIdentity identity, IReadOnlyList<WorldAuthorityJournalEntry> entries, CancellationToken cancellationToken, WorldAuthorityFence fence) =>
        AppendRootAsync(identity, entries, fence, null, cancellationToken);

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
        var content = await LoadPublishedDefinitionBytesAsync(identity, cancellationToken).ConfigureAwait(false);
        if (content is null) { return null; }
        if (WorldDefinitionLoader.TryReadPublishable(System.Text.Encoding.UTF8.GetString(content.Value.Span),
            identity.World.Value, out var definition, out var reason, m_machines, m_documentDirectory)) {
            return definition;
        }
        throw new InvalidDataException($"hosted definition for '{identity.World}' failed to load: {reason}");
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityJournalTail> LoadJournalTailAsync(WorldAuthorityIdentity identity, long afterOrdinal, CancellationToken cancellationToken) {
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
        return new WorldAuthorityJournalTail(
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

    private static ObjectBlobAddress CheckpointCandidateAddress(WorldAuthorityIdentity identity, long sequence, string hash) => AuthorityAddress(
        identity, $"checkpoints/{sequence:D20}-{ExtractHex(hash)}.pckp"
    );
    private static ObjectBlobAddress JournalCandidateAddress(WorldAuthorityIdentity identity, long sequence, string hash) => AuthorityAddress(
        identity, $"journal/{sequence:D20}-{ExtractHex(hash)}.bin"
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
        var content = await ReadAsync(RootAddress(identity), cancellationToken).ConfigureAwait(false);
        if (content is not { } found) { return null; }
        if (found.VersionToken is not { Length: > 0 } token) { throw new InvalidDataException("authority root has no CAS version token"); }
        if (!WorldAuthorityRootCodec.TryDecode(found.Content.Span, out var root, out var reason)) {
            throw new InvalidDataException($"authority root refused — {reason}");
        }
        return new(root, token);
    }
    private async Task<ObjectBlobWriteResult> WriteRootAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, string? ifMatchVersion, ObjectBlobWriteMode mode, CancellationToken cancellationToken) => await UnderTimeoutAsync(
        cancellationToken: cancellationToken,
        op: ct => m_store.WriteAsync(
            m_target,
            RootAddress(identity: identity),
            JsonSerializer.SerializeToUtf8Bytes(root, WorldAuthorityRootCodec.Options),
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
    public async Task<WorldAuthorityRootSnapshot?> LoadRootAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) =>
        (await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false));
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
        await ArchiveRecoveryPayloadsAsync(identity, captured.Root, operationId, cancellationToken).ConfigureAwait(false);
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
            operationId,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        return reference;
    }
    private async Task VerifyRecoveryPayloadsAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, Guid operationId, CancellationToken cancellationToken) {
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
                ArchiveAddress(identity, operationId, CheckpointCandidateAddress(identity, root.CheckpointStorageSequence, checkpoint)),
                checkpoint
            ).ConfigureAwait(false);
        }
        await foreach (var segment in ReadJournalSegmentsAsync(identity, root, cancellationToken, archiveOnly: operationId).ConfigureAwait(false)) { }
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
        var basis = saved.Root;
        var restored = basis with {
            PayloadArchive = operationId,
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

            var root = current?.Root ?? WorldAuthorityRoot.Empty;
            var nextToken = Guid.NewGuid();
            var next = root with {
                Epoch = checked(root.Epoch + 1),
                FenceToken = nextToken,
                Sequence = checked(root.Sequence + 1)
            };
            var result = await WriteRootAsync(identity, next, current?.VersionToken,
                current is null ? ObjectBlobWriteMode.CreateOnly : ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);

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
    /// <inheritdoc/>
    public async Task<WorldAuthorityRecovery?> LoadRecoveryAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken, WorldPrivateDrawKeys? drawKeys = null) {
        var snapshot = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; snapshot is { } rooted; attempt++) {
            try {
                return await ReadRecoveryAsync(identity, rooted, cancellationToken, drawKeys).ConfigureAwait(false);
            } catch (InvalidDataException) when (attempt < MaxCasAttempts) {
                // A checkpoint can retire objects under this read. Retry only if the root actually moved.
                snapshot = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
                if (snapshot is null || snapshot.Value.VersionToken == rooted.VersionToken) { throw; }
            }
        }
        return null;
    }

    private async Task<WorldAuthorityRecovery> ReadRecoveryAsync(WorldAuthorityIdentity identity, WorldAuthorityRootSnapshot rooted,
        CancellationToken cancellationToken, WorldPrivateDrawKeys? drawKeys) {
        WorldAuthorityCheckpointBlob? checkpoint = null;

        if (rooted.Root.CheckpointHash is { } hash) {
            var bytes = await ReadPinnedPayloadAsync(identity, rooted.Root.PayloadArchive,
                CheckpointCandidateAddress(identity, rooted.Root.CheckpointStorageSequence, hash), hash, cancellationToken).ConfigureAwait(false);
            checkpoint = new(bytes, rooted.Root.CheckpointOrdinal, rooted.Root.CheckpointTick);
        }
        var journal = new WorldAuthorityJournalEntry[rooted.Root.JournalEntryCount];
        await foreach (var segment in ReadJournalSegmentsAsync(identity, rooted.Root, cancellationToken).ConfigureAwait(false)) {
            for (var i = segment.Offset; i < segment.Entries.Count; i++) {
                journal[segment.Destination + i - segment.Offset] = segment.Entries[i];
            }
        }
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
                keys: drawKeys ?? WorldPrivateDrawKeys.Preview,
                catalog: m_machines,
                documentDirectory: m_documentDirectory
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (loaded.Admission is null) throw new InvalidDataException(message: $"root-qualified definition is invalid — {loaded.Reason}");
            admission = loaded.Admission;
        }
        return new WorldAuthorityRecovery(
            rooted,
            checkpoint,
            new WorldAuthorityJournalTail(
                CheckpointOrdinal: rooted.Root.CheckpointOrdinal,
                Entries: journal
            )
        ) { Admission = admission };
    }

    private async Task<WorldAuthorityStoreOutcome> AppendRootAsync(WorldAuthorityIdentity identity, IReadOnlyList<WorldAuthorityJournalEntry> entries, WorldAuthorityFence? suppliedFence, WorldAuthorityOperationReceipt? receipt, CancellationToken cancellationToken) {
        var encodedSize = 128L;
        foreach (var entry in entries) {
            if (entry.Encoded.Length > WorldAuthorityStoreWireCodec.MaxEntryBytes) {
                return WorldAuthorityStoreOutcome.Failed("journal entry exceeds its byte limit");
            }
            encodedSize += entry.Encoded.Length + 21L;
        }
        if (entries.Count == 0 || encodedSize > MaximumBlobBytes) {
            return WorldAuthorityStoreOutcome.Failed("journal batch is empty or exceeds its byte limit");
        }
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
            var sequence = checked(current.Root.Sequence + 1);
            var journalBytes = WorldAuthorityStoreWireCodec.EncodeJournalSegment(entries, current.Root.JournalStorageSequence, current.Root.JournalHash);
            var journalHash = WorldDefinitionFileSource.ComputeContentHash(journalBytes);
            var immutable = await PutImmutableAsync(JournalCandidateAddress(identity, sequence, journalHash), journalBytes, cancellationToken).ConfigureAwait(false);
            if (!immutable.Ok) { return immutable; }

            var next = current.Root with {
                JournalHash = journalHash,
                JournalStorageSequence = sequence,
                JournalEntryCount = checked(current.Root.JournalEntryCount + entries.Count),
                JournalSegmentCount = checked(current.Root.JournalSegmentCount + 1),
                JournalSequence = checked(current.Root.JournalSequence + entries.Count),
                ReceiptHash = prepared.ReceiptHash,
                ReceiptIndexHash = prepared.ReceiptIndexHash,
                Sequence = sequence,
            };
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
                var reconciled = await ReconcileAsync(identity, next, receipt, cancellationToken).ConfigureAwait(false);
                return reconciled with { JournalBytesWritten = journalBytes.Length };
            }
            if (result.Succeeded) {
                return WorldAuthorityStoreOutcome.Success(root: new(next, result.VersionToken ?? string.Empty))
                    with { JournalBytesWritten = journalBytes.Length };
            }
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
            var sequence = checked(current.Root.Sequence + 1);
            var ordinal = checked(current.Root.CheckpointOrdinal + 1);
            var entriesLeft = checked((int)(current.Root.JournalSequence - coverage));
            var retained = current.Root with {
                CheckpointCoverageSequence = coverage,
                JournalEntryCount = entriesLeft,
                JournalHash = entriesLeft == 0 ? null : current.Root.JournalHash,
                JournalStorageSequence = entriesLeft == 0 ? 0 : current.Root.JournalStorageSequence,
                JournalSegmentCount = 0,
            };
            var liveJournal = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var segment in ReadJournalSegmentsAsync(identity, retained, cancellationToken, verifySegmentCount: false).ConfigureAwait(false)) {
                liveJournal.Add(segment.Address.Key);
            }
            var checkpointWrite = await PutImmutableAsync(
                CheckpointCandidateAddress(identity, sequence, checkpointHash), encoded, cancellationToken).ConfigureAwait(false);

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
            var next = retained with {
                CheckpointHash = checkpointHash,
                CheckpointStorageSequence = sequence,
                CheckpointOrdinal = ordinal,
                CheckpointTick = tick,
                JournalSegmentCount = liveJournal.Count,
                PayloadArchive = entriesLeft == 0 ? Guid.Empty : retained.PayloadArchive,
                ReceiptHash = prepared.ReceiptHash,
                ReceiptIndexHash = prepared.ReceiptIndexHash,
                DurableOrdinal = ordinal,
                DurableTick = tick,
                Sequence = sequence,
            };

            WorldAuthorityStoreOutcome published;
            try {
                var result = await WriteRootAsync(identity, next, state.CreateOnly ? null : current.VersionToken,
                    state.CreateOnly ? ObjectBlobWriteMode.CreateOnly : ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded) { continue; }
                published = WorldAuthorityStoreOutcome.Success(root: new(next, result.VersionToken ?? string.Empty));
            } catch {
                published = await ReconcileAsync(identity, next, receipt, cancellationToken).ConfigureAwait(false);
            }
            if (!published.Ok || published.PublishedRoot?.Root != next) { return published; }
            return await RetireCheckpointHistoryAsync(identity, current.Root, liveJournal, published, cancellationToken).ConfigureAwait(false);
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
