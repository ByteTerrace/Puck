using System.Text.Json;
using Puck.Storage;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary><see cref="IWorldAuthorityStore"/> over <see cref="IObjectBlobStore"/>, addressed through
/// <see cref="WorldOwnedWorldSync.HostedAddressFor"/> — fail-closed exactly like <see cref="WorldOwnedWorldSync"/>:
/// every operation is bounded by <see cref="OperationTimeout"/>, and both refusal axes an
/// <see cref="ObjectBlobWriteResult"/> can carry (a create-only loss, an if-match precondition loss) surface by name
/// rather than collapsing into one generic failure. A checkpoint blob is content-addressed and written create-only,
/// so a retry that resends identical bytes is idempotent; the private root is the sole mutable publication point and
/// moves the checkpoint, journal, definition, and receipt references under one if-match compare-and-swap, retried up
/// to <see cref="MaxCasAttempts"/> times against a concurrent writer before refusing by name.</summary>
public sealed class WorldAuthorityBlobStore : IWorldAuthorityStore {
    private const int MaxCasAttempts = 5;

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(seconds: 15);

    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;

    /// <summary>Initializes the store.</summary>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The extension-supplied storage target — persistent storage in deployment, a directory for local runs and the
    /// canary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    public WorldAuthorityBlobStore(IObjectBlobStore store, ObjectStorageTarget target) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);

        m_store = store;
        m_target = target;
    }

    // Every store call in this type runs under the SAME bound: a linked token that cancels after OperationTimeout
    // regardless of what the caller's own token does. Exception handling stays with each call site — some convert a
    // failure into WorldAuthorityStoreOutcome.Failed with their own wording, others let it propagate — so this only
    // wraps the timeout, never a try/catch.
    private static async Task<T> UnderTimeoutAsync<T>(CancellationToken cancellationToken, Func<CancellationToken, ValueTask<T>> op) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

        timeout.CancelAfter(delay: OperationTimeout);

        return await op(timeout.Token);
    }
    // sha256-64/{hex} is the canonical pin form (WorldDefinitionFileSource.ComputeContentHash); the checkpoint blob
    // NAME carries only the hex half, since '/' cannot live inside one path segment. This is the one place that
    // splits the pin, and CheckpointAddress is the one place that rejoins it.
    private static string ExtractHex(string hash) {
        const string Prefix = "sha256-64/";

        if (!hash.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Prefix
        )) {
            throw new InvalidDataException(message: $"'{hash}' is not a sha256-64 content-address pin.");
        }

        return hash[Prefix.Length..];
    }
    private static ObjectBlobAddress CheckpointAddress(Guid containerId, SafeName world, long ordinal, string hash) => WorldOwnedWorldSync.HostedAddressFor(
        containerId: containerId,
        leaf: $"checkpoints/{ordinal:D12}-{ExtractHex(hash: hash)}.pckp",
        world: world
    );
    private static ObjectBlobAddress JournalAddress(Guid containerId, SafeName world, long ordinal) => WorldOwnedWorldSync.HostedAddressFor(
        containerId: containerId,
        leaf: $"journal/{ordinal:D12}.bin",
        world: world
    );
    private static ObjectBlobAddress LatestPointerAddress(Guid containerId, SafeName world) => WorldOwnedWorldSync.HostedAddressFor(
        containerId: containerId,
        leaf: "checkpoints/latest",
        world: world
    );

    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> AppendJournalAsync(WorldAuthorityIdentity identity, WorldMutationJournalEntry entry, CancellationToken cancellationToken, WorldAuthorityFence? fence = null, WorldAuthorityOperationReceipt? receipt = null) {
        return await AppendRootAsync(identity, entry, fence, receipt, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc/>
    public async Task<WorldDefinition?> LoadDefinitionAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var rooted = await WorldAuthorityRootReader.ReadDefinitionAsync(identity.Owner, identity.World, m_store, m_target, cancellationToken).ConfigureAwait(false);
        if (rooted is null) { return null; }
        var origin = new WorldHostedOrigin(
            owner: identity.Owner,
            store: m_store,
            target: m_target,
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
        var rooted = await LoadRecoveryAsync(identity, cancellationToken).ConfigureAwait(false);
        if (rooted is { } recovery) {
            if (recovery.Root.Root.CheckpointOrdinal != afterOrdinal) {
                throw new InvalidDataException("requested checkpoint ordinal is not the authoritative root checkpoint");
            }
            return recovery.Journal;
        }
        var address = JournalAddress(
            containerId: identity.Owner,
            ordinal: afterOrdinal,
            world: identity.World
        );

        var content = await UnderTimeoutAsync(
            cancellationToken: cancellationToken,
            op: ct => m_store.ReadAsync(
                address: address,
                cancellationToken: ct,
                target: m_target
            )
        );

        if (content is not { } found) {
            return new WorldMutationJournalTail(
                CheckpointOrdinal: afterOrdinal,
                Entries: []
            );
        }

        if (!WorldAuthorityStoreWireCodec.TryDecodeJournalPage(
            bytes: found.Content.Span,
            entries: out var entries,
            reason: out var reason
        )) {
            throw new InvalidDataException(message: $"'{address.Key}' is corrupt — {reason}");
        }

        return new WorldMutationJournalTail(
            CheckpointOrdinal: afterOrdinal,
            Entries: entries
        );
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityCheckpointBlob?> LoadLatestAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var rooted = await LoadRecoveryAsync(identity, cancellationToken).ConfigureAwait(false);
        if (rooted is { } recovery) { return recovery.Checkpoint; }
        var pointerAddress = LatestPointerAddress(
            containerId: identity.Owner,
            world: identity.World
        );

        var pointerContent = await UnderTimeoutAsync(
            cancellationToken: cancellationToken,
            op: ct => m_store.ReadAsync(
                address: pointerAddress,
                cancellationToken: ct,
                target: m_target
            )
        );

        if (pointerContent is not { } pointer) {
            return null;
        }

        if (!WorldAuthorityStoreWireCodec.TryDecodeLatestPointer(
            bytes: pointer.Content.Span,
            hash: out var hash,
            ordinal: out var ordinal,
            reason: out var pointerReason,
            tick: out var tick
        )) {
            throw new InvalidDataException(message: $"'{pointerAddress.Key}' is corrupt — {pointerReason}");
        }

        var checkpointAddress = CheckpointAddress(
            containerId: identity.Owner,
            hash: hash,
            ordinal: ordinal,
            world: identity.World
        );

        var checkpointContent = await UnderTimeoutAsync(
            cancellationToken: cancellationToken,
            op: ct => m_store.ReadAsync(
                address: checkpointAddress,
                cancellationToken: ct,
                target: m_target
            )
        );

        if (checkpointContent is not { } checkpoint) {
            throw new InvalidDataException(message: $"'{pointerAddress.Key}' names '{checkpointAddress.Key}', which does not exist.");
        }

        var computedHash = WorldDefinitionFileSource.ComputeContentHash(content: checkpoint.Content.Span);

        if (!string.Equals(
            a: computedHash,
            b: hash,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new InvalidDataException(message: $"'{checkpointAddress.Key}' hashes to {computedHash}, not the pointer's recorded {hash}.");
        }

        return new WorldAuthorityCheckpointBlob(
            Encoded: checkpoint.Content,
            Ordinal: ordinal,
            Tick: tick
        );
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> PublishDefinitionAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken cancellationToken, WorldAuthorityFence? fence = null) {
        return await PublishDefinitionRootAsync(identity, composed, fence, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> WriteCheckpointAsync(WorldAuthorityIdentity identity, ReadOnlyMemory<byte> encoded, ulong tick, CancellationToken cancellationToken, WorldAuthorityFence? fence = null, WorldAuthorityOperationReceipt? receipt = null, long? capturedJournalSequence = null) {
        return await WriteCheckpointRootAsync(identity, encoded, tick, fence, receipt, capturedJournalSequence, cancellationToken).ConfigureAwait(false);
    }

    #pragma warning disable IDE0011
    private static ObjectBlobAddress AuthorityAddress(WorldAuthorityIdentity identity, string leaf) => new(
        ObjectId: identity.Owner,
        Key: $"{WorldOwnedWorldSync.HostedPrivateNamespace}/{identity.World.Value}/authority/{leaf}"
    );
    private static ObjectBlobAddress RootAddress(WorldAuthorityIdentity identity) => AuthorityAddress(identity, "root");
    private static ObjectBlobAddress DefinitionCandidateAddress(WorldAuthorityIdentity identity, string hash) => AuthorityAddress(identity, $"definitions/{ExtractHex(hash)}.json");
    private static ObjectBlobAddress CheckpointCandidateAddress(WorldAuthorityIdentity identity, long ordinal, string hash) => AuthorityAddress(identity, $"checkpoints/{ordinal:D12}-{ExtractHex(hash)}.pckp");
    private static ObjectBlobAddress JournalCandidateAddress(WorldAuthorityIdentity identity, string hash) => AuthorityAddress(identity, $"journal/{ExtractHex(hash)}.bin");
    private static ObjectBlobAddress ReceiptCandidateAddress(WorldAuthorityIdentity identity, string hash) => AuthorityAddress(identity, $"receipts/{ExtractHex(hash)}.rcpt");
    private static ObjectBlobAddress ReceiptIndexAddress(WorldAuthorityIdentity identity, string hash) => AuthorityAddress(identity, $"receipt-index/{ExtractHex(hash)}.json");

    private async Task<ObjectBlobContent?> ReadAsync(ObjectBlobAddress address, CancellationToken cancellationToken) => await UnderTimeoutAsync(cancellationToken, ct => m_store.ReadAsync(m_target, address, ct)).ConfigureAwait(false);
    private async Task<WorldAuthorityRootSnapshot?> ReadRootSnapshotAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var content = await ReadAsync(RootAddress(identity), cancellationToken).ConfigureAwait(false);
        if (content is not { } found) return null;
        if (found.VersionToken is not { Length: > 0 } token) throw new InvalidDataException("authority root has no CAS version token");
        if (!WorldAuthorityRootCodec.TryDecode(found.Content.Span, out var root, out var reason)) throw new InvalidDataException($"authority root is corrupt — {reason}");
        return new WorldAuthorityRootSnapshot(root, token);
    }
    private async Task<WorldAuthorityRootSnapshot?> EnsureInitialRootAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var root = WorldAuthorityRoot.Empty;
        var legacyPointer = await ReadAsync(LatestPointerAddress(identity.Owner, identity.World), cancellationToken).ConfigureAwait(false);
        if (legacyPointer is { } pointer) {
            if (!WorldAuthorityStoreWireCodec.TryDecodeLatestPointer(pointer.Content.Span, ordinal: out var ordinal, tick: out var tick, hash: out var hash, reason: out var legacyReason)) throw new InvalidDataException($"legacy checkpoint pointer is corrupt — {legacyReason}");
            var legacyCheckpoint = await ReadAsync(CheckpointAddress(identity.Owner, identity.World, ordinal, hash), cancellationToken).ConfigureAwait(false);
            if (legacyCheckpoint is not { } checkpoint) throw new InvalidDataException("legacy checkpoint pointer names a missing blob");
            if (!string.Equals(WorldDefinitionFileSource.ComputeContentHash(checkpoint.Content.Span), hash, StringComparison.Ordinal)) throw new InvalidDataException("legacy checkpoint pointer hash does not match its blob");
            var checkpointHash = WorldDefinitionFileSource.ComputeContentHash(checkpoint.Content.Span);
            var checkpointCandidate = await PutImmutableAsync(CheckpointCandidateAddress(identity, ordinal, checkpointHash), checkpoint.Content, cancellationToken).ConfigureAwait(false);
            if (!checkpointCandidate.Ok) throw new InvalidDataException(checkpointCandidate.Detail);
            var journal = await ReadAsync(JournalAddress(identity.Owner, identity.World, ordinal), cancellationToken).ConfigureAwait(false);
            var count = 0;
            string? journalHash = null;
            if (journal is { } page) {
                if (!WorldAuthorityStoreWireCodec.TryDecodeJournalPage(page.Content.Span, out var entries, out var reason)) throw new InvalidDataException($"legacy journal is corrupt — {reason}");
                count = entries.Count;
                if (count > 0) { journalHash = WorldDefinitionFileSource.ComputeContentHash(page.Content.Span); var journalCandidate = await PutImmutableAsync(JournalCandidateAddress(identity, journalHash), page.Content, cancellationToken).ConfigureAwait(false); if (!journalCandidate.Ok) throw new InvalidDataException(journalCandidate.Detail); }
            }
            root = root with { CheckpointHash = checkpointHash, CheckpointOrdinal = ordinal, CheckpointTick = tick, JournalHash = journalHash, JournalEntryCount = count, JournalSequence = count - 1L, CheckpointCoverageSequence = -1L };
        }
        var legacyDefinition = await ReadAsync(WorldOwnedWorldSync.HostedAddressFor(identity.Owner, identity.World, "definition.json"), cancellationToken).ConfigureAwait(false);
        if (legacyDefinition is { } definition) {
            var definitionHash = WorldDefinitionFileSource.ComputeContentHash(definition.Content.Span);
            var definitionCandidate = await PutImmutableAsync(DefinitionCandidateAddress(identity, definitionHash), definition.Content, cancellationToken).ConfigureAwait(false);
            if (!definitionCandidate.Ok) throw new InvalidDataException(definitionCandidate.Detail);
            root = root with { DefinitionHash = definitionHash };
        }
        return new WorldAuthorityRootSnapshot(root, string.Empty);
    }
    private async Task<ObjectBlobWriteResult> WriteRootAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, string? ifMatchVersion, ObjectBlobWriteMode mode, CancellationToken cancellationToken) => await UnderTimeoutAsync(cancellationToken, ct => m_store.WriteAsync(m_target, RootAddress(identity), WorldAuthorityRootCodec.Encode(root), mode, ifMatchVersion, ct)).ConfigureAwait(false);
    private async Task<WorldAuthorityStoreOutcome> PutImmutableAsync(ObjectBlobAddress address, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) {
        var result = await UnderTimeoutAsync(cancellationToken, ct => m_store.WriteAsync(m_target, address, bytes, ObjectBlobWriteMode.CreateOnly, null, ct)).ConfigureAwait(false);
        if (result.Succeeded) return WorldAuthorityStoreOutcome.Success();
        var current = await ReadAsync(address, cancellationToken).ConfigureAwait(false);
        if (current is { } found && found.Content.Span.SequenceEqual(bytes.Span)) return WorldAuthorityStoreOutcome.Success("already present");
        return WorldAuthorityStoreOutcome.AlreadyExists($"'{address.Key}' exists with different content");
    }
    private readonly record struct RootWriteSnapshot(WorldAuthorityRootSnapshot Snapshot, bool CreateOnly);
    private static bool IsUnownedFence(WorldAuthorityFence fence) => fence.Epoch == 0 && fence.Token == Guid.Empty;
    private static bool FenceMatches(WorldAuthorityRootSnapshot snapshot, WorldAuthorityFence fence) => IsUnownedFence(fence) ? snapshot.Root.FenceToken == Guid.Empty : snapshot.Root.Epoch == fence.Epoch && snapshot.Root.FenceToken == fence.Token;
    private async Task<RootWriteSnapshot?> ReadMutationSnapshotAsync(WorldAuthorityIdentity identity, WorldAuthorityFence fence, CancellationToken cancellationToken) {
        var current = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
        if (current is { } snapshot) return new RootWriteSnapshot(snapshot, false);
        if (!IsUnownedFence(fence)) return null;
        // Initialize from the legacy private/public pointers before the epoch-zero root CAS. This keeps a
        // pre-root checkpoint and its later journal tail visible to the first unowned publisher; the root CAS
        // still decides which concurrent initializer becomes authoritative.
        var initial = await EnsureInitialRootAsync(identity, cancellationToken).ConfigureAwait(false);
        return initial is { } migrated ? new RootWriteSnapshot(migrated, true) : null;
    }
    private async Task<WorldAuthorityFence?> EnsureFenceAsync(WorldAuthorityIdentity identity, WorldAuthorityFence? supplied, CancellationToken cancellationToken) {
        if (supplied is { } fence) return IsUnownedFence(fence) || (fence.Epoch > 0 && fence.Token != Guid.Empty) ? fence : null;
        var current = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
        return current is { } snapshot && snapshot.Root.FenceToken != Guid.Empty ? null : WorldAuthorityFence.Unowned;
    }
    private async Task<WorldAuthorityStoreOutcome> ReconcileAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot expected, WorldAuthorityOperationReceipt? receipt, CancellationToken cancellationToken) {
        try {
            var current = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
            if (current is { } found && found.Root == expected) return WorldAuthorityStoreOutcome.Success("CAS outcome reconciled", found);
            if (receipt is { } wanted && current is { } root && await FindReceiptFromRootAsync(identity, root.Root, wanted.OperationId, cancellationToken).ConfigureAwait(false) is { } actual && actual == wanted) return WorldAuthorityStoreOutcome.Success("receipt reconciled", root);
        } catch { }
        return WorldAuthorityStoreOutcome.RecoveryRequired("root CAS outcome is uncertain; reconcile by reading the root and receipt chain");
    }

    /// <inheritdoc/>
    public async Task<WorldAuthorityRootSnapshot?> LoadRootAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) => await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
    /// <inheritdoc/>
    public async Task<WorldAuthorityFence?> AcquireActivationAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        for (var attempt = 0; attempt < MaxCasAttempts; attempt++) {
            var current = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
            if (current is null) {
                var initial = await EnsureInitialRootAsync(identity, cancellationToken).ConfigureAwait(false);
                var token = Guid.NewGuid();
                var candidate = initial!.Value.Root with { Epoch = 1, FenceToken = token, Sequence = 1 };
                var created = await WriteRootAsync(identity, candidate, null, ObjectBlobWriteMode.CreateOnly, cancellationToken).ConfigureAwait(false);
                if (created.Succeeded && created.VersionToken is { Length: > 0 } createdVersion) return new WorldAuthorityFence(candidate.Epoch, token, createdVersion);
                continue;
            }
            var nextToken = Guid.NewGuid();
            var next = current.Value.Root with { Epoch = checked(current.Value.Root.Epoch + 1), FenceToken = nextToken, Sequence = checked(current.Value.Root.Sequence + 1) };
            var result = await WriteRootAsync(identity, next, current.Value.VersionToken, ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.VersionToken is { Length: > 0 } version) return new WorldAuthorityFence(next.Epoch, nextToken, version);
        }
        return null;
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> ReleaseActivationAsync(WorldAuthorityIdentity identity, WorldAuthorityFence fence, CancellationToken cancellationToken) {
        var current = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
        if (current is not { } snapshot || !FenceMatches(snapshot, fence)) return WorldAuthorityStoreOutcome.StaleFence("activation fence is no longer current");
        var released = snapshot.Root with { Epoch = checked(snapshot.Root.Epoch + 1), FenceToken = Guid.Empty, Sequence = checked(snapshot.Root.Sequence + 1) };
        var result = await WriteRootAsync(identity, released, snapshot.VersionToken, ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(released, result.VersionToken ?? string.Empty)) : WorldAuthorityStoreOutcome.PreconditionFailed("activation root moved before release");
    }

    private async Task<WorldAuthorityOperationReceipt?> FindReceiptFromRootAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, Guid operationId, CancellationToken cancellationToken) {
        if (root.ReceiptIndexHash is { Length: > 0 }) {
            var index = await LoadReceiptIndexAsync(identity, root, cancellationToken).ConfigureAwait(false);
            if (!index.TryGetValue(operationId, out var indexedHash)) return null;
            var indexedReceipt = await ReadReceiptAsync(identity, indexedHash, cancellationToken).ConfigureAwait(false);
            if (indexedReceipt.OperationId != operationId) throw new InvalidDataException("receipt index target operation does not match its dictionary key");
            return indexedReceipt;
        }
        var hash = root.ReceiptHash;
        for (var count = 0; hash is { Length: > 0 }; count++) {
            if (count >= 100_000) throw new InvalidDataException("receipt chain exceeds its traversal bound");
            var content = await ReadAsync(ReceiptCandidateAddress(identity, hash), cancellationToken).ConfigureAwait(false);
            if (content is not { } found) throw new InvalidDataException("receipt chain names a missing blob");
            if (!string.Equals(WorldDefinitionFileSource.ComputeContentHash(found.Content.Span), hash, StringComparison.Ordinal)) throw new InvalidDataException("receipt chain content pin mismatch");
            if (!WorldAuthorityRootCodec.TryDecodeReceipt(found.Content.Span, out var receipt, out var previous, out var reason)) throw new InvalidDataException($"receipt chain is corrupt — {reason}");
            if (receipt.OperationId == operationId) return receipt;
            hash = previous;
        }
        return null;
    }
    private async Task<WorldAuthorityOperationReceipt> ReadReceiptAsync(WorldAuthorityIdentity identity, string hash, CancellationToken cancellationToken) {
        var indexed = await ReadAsync(ReceiptCandidateAddress(identity, hash), cancellationToken).ConfigureAwait(false);
        if (indexed is not { } indexedBlob) throw new InvalidDataException("receipt index target is missing");
        if (!string.Equals(WorldDefinitionFileSource.ComputeContentHash(indexedBlob.Content.Span), hash, StringComparison.Ordinal)) throw new InvalidDataException("receipt index target does not match its content pin");
        if (!WorldAuthorityRootCodec.TryDecodeReceipt(indexedBlob.Content.Span, out var indexedReceipt, out _, out var indexedReason)) throw new InvalidDataException($"receipt index target is corrupt — {indexedReason}");
        return indexedReceipt;
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityOperationReceipt?> FindOperationReceiptAsync(WorldAuthorityIdentity identity, Guid operationId, CancellationToken cancellationToken) {
        var root = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
        return root is { } snapshot ? await FindReceiptFromRootAsync(identity, snapshot.Root, operationId, cancellationToken).ConfigureAwait(false) : null;
    }
    private static bool SameOperation(WorldAuthorityOperationReceipt left, WorldAuthorityOperationReceipt right) => left.OperationId == right.OperationId && string.Equals(left.Actor, right.Actor, StringComparison.Ordinal) && string.Equals(left.PayloadDigest, right.PayloadDigest, StringComparison.Ordinal);
    private async Task<Dictionary<Guid, string>> LoadReceiptIndexAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, CancellationToken cancellationToken) {
        var index = new Dictionary<Guid, string>();
        if (root.ReceiptIndexHash is not { Length: > 0 } hash) return index;
        var content = await ReadAsync(ReceiptIndexAddress(identity, hash), cancellationToken).ConfigureAwait(false);
        if (content is not { } found || !string.Equals(WorldDefinitionFileSource.ComputeContentHash(found.Content.Span), hash, StringComparison.Ordinal)) throw new InvalidDataException("receipt index does not match the root content pin");
        var parsed = JsonSerializer.Deserialize<Dictionary<Guid, string>>(found.Content.Span) ?? throw new InvalidDataException("receipt index is empty or malformed");
        foreach (var pair in parsed) {
            if (pair.Key == Guid.Empty || !IsContentPin(pair.Value)) throw new InvalidDataException("receipt index contains an invalid entry");
            index.Add(pair.Key, pair.Value);
        }
        return index;
    }
    private static bool IsContentPin(string value) {
        const string prefix = "sha256-64/";
        if (value.Length != prefix.Length + 16 || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        for (var index = prefix.Length; index < value.Length; index++) if (!Uri.IsHexDigit(value[index])) return false;
        return true;
    }
    private async Task<(WorldAuthorityStoreOutcome Outcome, string? ReceiptHash, string? ReceiptIndexHash)> PrepareReceiptAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, WorldAuthorityOperationReceipt? receipt, bool requireApplied, CancellationToken cancellationToken) {
        if (receipt is not { } value) return (WorldAuthorityStoreOutcome.Success(), root.ReceiptHash, root.ReceiptIndexHash);
        if (value.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(value.Actor) || string.IsNullOrWhiteSpace(value.PayloadDigest) || string.IsNullOrWhiteSpace(value.DecisionCode)) return (WorldAuthorityStoreOutcome.Failed("receipt fields are incomplete"), null, null);
        var receiptIndex = await LoadReceiptIndexAsync(identity, root, cancellationToken).ConfigureAwait(false);
        WorldAuthorityOperationReceipt? existing = null;
        if (receiptIndex.TryGetValue(value.OperationId, out var existingHash)) {
            existing = await ReadReceiptAsync(identity, existingHash, cancellationToken).ConfigureAwait(false);
            if (existing.Value.OperationId != value.OperationId) throw new InvalidDataException("receipt index target operation does not match its dictionary key");
        }
        if (existing is { } found) return (SameOperation(found, value) ? (WorldAuthorityStoreOutcome.Success("operation already durable"), root.ReceiptHash, root.ReceiptIndexHash) : (WorldAuthorityStoreOutcome.OperationConflict("operation id is bound to a different actor or payload"), null, null));
        if (requireApplied != value.Applied) return (WorldAuthorityStoreOutcome.PreconditionFailed(requireApplied ? "journal or checkpoint receipts must record an applied mutation" : "standalone receipts may record refusals only"), null, null);
        var bytes = WorldAuthorityRootCodec.EncodeReceipt(value, root.ReceiptHash);
        var hash = WorldDefinitionFileSource.ComputeContentHash(bytes);
        var write = await PutImmutableAsync(ReceiptCandidateAddress(identity, hash), bytes, cancellationToken).ConfigureAwait(false);
        if (!write.Ok) return (write, null, null);
        var index = receiptIndex;
        index[value.OperationId] = hash;
        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(new SortedDictionary<Guid, string>(index));
        var indexHash = WorldDefinitionFileSource.ComputeContentHash(indexBytes);
        var indexWrite = await PutImmutableAsync(ReceiptIndexAddress(identity, indexHash), indexBytes, cancellationToken).ConfigureAwait(false);
        return (indexWrite.Ok ? (indexWrite, hash, indexHash) : (indexWrite, null, null));
    }

    private async Task<(IReadOnlyList<WorldMutationJournalEntry> Entries, string? Hash)> ReadJournalForRootAsync(WorldAuthorityIdentity identity, WorldAuthorityRoot root, CancellationToken cancellationToken) {
        if (root.JournalHash is not { Length: > 0 } hash) {
            if (root.JournalEntryCount != 0 || root.JournalSequence != root.CheckpointCoverageSequence) throw new InvalidDataException("root has journal metadata without a journal blob");
            return ([], null);
        }
        var content = await ReadAsync(JournalCandidateAddress(identity, hash), cancellationToken).ConfigureAwait(false);
        if (content is not { } found) throw new InvalidDataException("root names a missing journal blob");
        if (!string.Equals(WorldDefinitionFileSource.ComputeContentHash(found.Content.Span), hash, StringComparison.Ordinal)) throw new InvalidDataException("journal blob does not match the root content pin");
        if (!WorldAuthorityStoreWireCodec.TryDecodeJournalPage(found.Content.Span, out var entries, out var reason)) throw new InvalidDataException($"journal blob is corrupt — {reason}");
        if (entries.Count != root.JournalEntryCount || ((long)entries.Count != root.JournalSequence - root.CheckpointCoverageSequence)) throw new InvalidDataException("root journal count and sequence disagree");
        return (entries, hash);
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityRecovery?> LoadRecoveryAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        var snapshot = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
        if (snapshot is not { } rooted) return null;
        WorldAuthorityCheckpointBlob? checkpoint = null;
        if (rooted.Root.CheckpointHash is { Length: > 0 } hash) {
            var content = await ReadAsync(CheckpointCandidateAddress(identity, rooted.Root.CheckpointOrdinal, hash), cancellationToken).ConfigureAwait(false);
            if (content is not { } found) throw new InvalidDataException("root names a missing checkpoint blob");
            if (!string.Equals(WorldDefinitionFileSource.ComputeContentHash(found.Content.Span), hash, StringComparison.Ordinal)) throw new InvalidDataException("checkpoint blob does not match the root content pin");
            checkpoint = new WorldAuthorityCheckpointBlob(found.Content, rooted.Root.CheckpointOrdinal, rooted.Root.CheckpointTick);
        }
        var journal = await ReadJournalForRootAsync(identity, rooted.Root, cancellationToken).ConfigureAwait(false);
        ObjectBlobContent? definitionBytes = null;
        if (rooted.Root.DefinitionHash is { } definitionHash) {
            var content = await ReadAsync(DefinitionCandidateAddress(identity, definitionHash), cancellationToken).ConfigureAwait(false);
            if (content is not { } found) throw new InvalidDataException("root names a missing definition blob");
            if (!string.Equals(WorldDefinitionFileSource.ComputeContentHash(found.Content.Span), definitionHash, StringComparison.Ordinal)) throw new InvalidDataException("definition blob does not match the root content pin");
            definitionBytes = found;
        }
        WorldDefinition? definition = null;
        if (definitionBytes is { } bytes) {
            var resolver = new WorldStorageNeighbourResolver(m_store, m_target, identity.Owner, WorldStorageNamespace.Hosted);
            var loaded = await WorldDefinitionLoader.LoadAsync(bytes.Content, $"authority/{identity.World.Value}/definition", identity.World.Value, resolver.ResolveHostedAsync, cancellationToken).ConfigureAwait(false);
            if (loaded.Definition is null) throw new InvalidDataException($"root-qualified definition is invalid — {loaded.Reason}");
            definition = loaded.Definition;
        }
        return new WorldAuthorityRecovery(rooted, checkpoint, new WorldMutationJournalTail(rooted.Root.CheckpointOrdinal, journal.Entries)) { Definition = definition };
    }
    private async Task<WorldAuthorityStoreOutcome> AppendRootAsync(WorldAuthorityIdentity identity, WorldMutationJournalEntry entry, WorldAuthorityFence? suppliedFence, WorldAuthorityOperationReceipt? receipt, CancellationToken cancellationToken) {
        var fence = await EnsureFenceAsync(identity, suppliedFence, cancellationToken).ConfigureAwait(false);
        if (fence is not { } active) return WorldAuthorityStoreOutcome.Failed("could not acquire activation fence");
        for (var attempt = 0; attempt < MaxCasAttempts; attempt++) {
            var writable = await ReadMutationSnapshotAsync(identity, active, cancellationToken).ConfigureAwait(false);
            if (writable is not { } state) return WorldAuthorityStoreOutcome.StaleFence("activation fence is no longer current");
            var current = state.Snapshot;
            if (!FenceMatches(current, active)) return WorldAuthorityStoreOutcome.StaleFence("activation fence is no longer current");
            if (current.Root.CheckpointHash is null) return WorldAuthorityStoreOutcome.Failed("no checkpoint exists yet — a journal is relative to one");
            var existing = await ReadJournalForRootAsync(identity, current.Root, cancellationToken).ConfigureAwait(false);
            var prepared = await PrepareReceiptAsync(identity, current.Root, receipt, requireApplied: true, cancellationToken).ConfigureAwait(false);
            if (!prepared.Outcome.Ok) return prepared.Outcome;
            if (receipt is { } && prepared.Outcome.Detail == "operation already durable") return WorldAuthorityStoreOutcome.Success(prepared.Outcome.Detail, current);
            var entries = new List<WorldMutationJournalEntry>(existing.Entries.Count + 1);
            entries.AddRange(existing.Entries); entries.Add(entry);
            var journalBytes = WorldAuthorityStoreWireCodec.EncodeJournalPage(entries);
            var journalHash = WorldDefinitionFileSource.ComputeContentHash(journalBytes);
            var immutable = await PutImmutableAsync(JournalCandidateAddress(identity, journalHash), journalBytes, cancellationToken).ConfigureAwait(false);
            if (!immutable.Ok) return immutable;
            var next = current.Root with { JournalHash = journalHash, JournalEntryCount = entries.Count, JournalSequence = checked(current.Root.JournalSequence + 1), ReceiptHash = prepared.ReceiptHash, ReceiptIndexHash = prepared.ReceiptIndexHash, Sequence = checked(current.Root.Sequence + 1) };
            ObjectBlobWriteResult result;
            try { result = await WriteRootAsync(identity, next, state.CreateOnly ? null : current.VersionToken, state.CreateOnly ? ObjectBlobWriteMode.CreateOnly : ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false); }
            catch { return await ReconcileAsync(identity, next, receipt, cancellationToken).ConfigureAwait(false); }
            if (result.Succeeded) return WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(next, result.VersionToken ?? string.Empty));
        }
        return WorldAuthorityStoreOutcome.PreconditionFailed("journal append lost the root compare-and-swap race");
    }
    private async Task<WorldAuthorityStoreOutcome> WriteCheckpointRootAsync(WorldAuthorityIdentity identity, ReadOnlyMemory<byte> encoded, ulong tick, WorldAuthorityFence? suppliedFence, WorldAuthorityOperationReceipt? receipt, long? capturedJournalSequence, CancellationToken cancellationToken) {
        var fence = await EnsureFenceAsync(identity, suppliedFence, cancellationToken).ConfigureAwait(false);
        if (fence is not { } active) return WorldAuthorityStoreOutcome.Failed("could not acquire activation fence");
        var checkpointHash = WorldDefinitionFileSource.ComputeContentHash(encoded.Span);
        for (var attempt = 0; attempt < MaxCasAttempts; attempt++) {
            var writable = await ReadMutationSnapshotAsync(identity, active, cancellationToken).ConfigureAwait(false);
            if (writable is not { } state) return WorldAuthorityStoreOutcome.StaleFence("activation fence is no longer current");
            var current = state.Snapshot;
            if (!FenceMatches(current, active)) return WorldAuthorityStoreOutcome.StaleFence("activation fence is no longer current");
            if (current.Root.CheckpointOrdinal >= 0 && tick < current.Root.CheckpointTick) return WorldAuthorityStoreOutcome.PreconditionFailed("checkpoint tick regresses the authoritative checkpoint");
            var coverage = capturedJournalSequence ?? ((current.Root.JournalSequence == current.Root.CheckpointCoverageSequence) ? current.Root.JournalSequence : long.MinValue);
            if (coverage == long.MinValue) return WorldAuthorityStoreOutcome.PreconditionFailed("checkpoint requires an explicit captured journal sequence while a later tail exists");
            if (coverage < current.Root.CheckpointCoverageSequence || coverage > current.Root.JournalSequence) return WorldAuthorityStoreOutcome.PreconditionFailed("checkpoint capture is stale or ahead of the authoritative journal");
            var existing = await ReadJournalForRootAsync(identity, current.Root, cancellationToken).ConfigureAwait(false);
            var firstSequence = checked(current.Root.CheckpointCoverageSequence + 1);
            var suffixOffset = checked((int)Math.Max(0L, coverage - firstSequence + 1));
            if (suffixOffset > existing.Entries.Count) return WorldAuthorityStoreOutcome.Failed("checkpoint journal coverage is inconsistent");
            var suffix = existing.Entries.Skip(suffixOffset).ToArray();
            string? journalHash = null;
            if (suffix.Length > 0) {
                var journalBytes = WorldAuthorityStoreWireCodec.EncodeJournalPage(suffix);
                journalHash = WorldDefinitionFileSource.ComputeContentHash(journalBytes);
                var journalWrite = await PutImmutableAsync(JournalCandidateAddress(identity, journalHash), journalBytes, cancellationToken).ConfigureAwait(false);
                if (!journalWrite.Ok) return journalWrite;
            }
            var ordinal = checked(current.Root.CheckpointOrdinal + 1);
            var checkpointWrite = await PutImmutableAsync(CheckpointCandidateAddress(identity, ordinal, checkpointHash), encoded, cancellationToken).ConfigureAwait(false);
            if (!checkpointWrite.Ok) return checkpointWrite;
            var prepared = await PrepareReceiptAsync(identity, current.Root, receipt, requireApplied: true, cancellationToken).ConfigureAwait(false);
            if (!prepared.Outcome.Ok) return prepared.Outcome;
            if (receipt is { } && prepared.Outcome.Detail == "operation already durable") return WorldAuthorityStoreOutcome.Success(prepared.Outcome.Detail, current);
            var next = current.Root with { CheckpointHash = checkpointHash, CheckpointOrdinal = ordinal, CheckpointTick = tick, JournalHash = journalHash, JournalEntryCount = suffix.Length, CheckpointCoverageSequence = coverage, ReceiptHash = prepared.ReceiptHash, ReceiptIndexHash = prepared.ReceiptIndexHash, DurableOrdinal = ordinal, DurableTick = tick, Sequence = checked(current.Root.Sequence + 1) };
            try {
                var result = await WriteRootAsync(identity, next, state.CreateOnly ? null : current.VersionToken, state.CreateOnly ? ObjectBlobWriteMode.CreateOnly : ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded) return WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(next, result.VersionToken ?? string.Empty));
            } catch { return await ReconcileAsync(identity, next, receipt, cancellationToken).ConfigureAwait(false); }
        }
        return WorldAuthorityStoreOutcome.PreconditionFailed("checkpoint publication lost the root compare-and-swap race");
    }
    private async Task<WorldAuthorityStoreOutcome> PublishDefinitionRootAsync(WorldAuthorityIdentity identity, WorldDefinition composed, WorldAuthorityFence? suppliedFence, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(composed);
        var fence = await EnsureFenceAsync(identity, suppliedFence, cancellationToken).ConfigureAwait(false);
        if (fence is not { } active) return WorldAuthorityStoreOutcome.Failed("could not acquire activation fence");
        var bytes = WorldDefinitionSerialization.Serialize(composed);
        var hash = WorldDefinitionFileSource.ComputeContentHash(bytes);
        for (var attempt = 0; attempt < MaxCasAttempts; attempt++) {
            var writable = await ReadMutationSnapshotAsync(identity, active, cancellationToken).ConfigureAwait(false);
            if (writable is not { } state) return WorldAuthorityStoreOutcome.StaleFence("activation fence is no longer current");
            var current = state.Snapshot;
            if (!FenceMatches(current, active)) return WorldAuthorityStoreOutcome.StaleFence("activation fence is no longer current");
            if (current.Root.DefinitionHash == hash) return WorldAuthorityStoreOutcome.Success("definition already durable", current);
            var immutable = await PutImmutableAsync(DefinitionCandidateAddress(identity, hash), bytes, cancellationToken).ConfigureAwait(false);
            if (!immutable.Ok) return immutable;
            var next = current.Root with { DefinitionHash = hash, Sequence = checked(current.Root.Sequence + 1) };
            try {
                var result = await WriteRootAsync(identity, next, state.CreateOnly ? null : current.VersionToken, state.CreateOnly ? ObjectBlobWriteMode.CreateOnly : ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded) return WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(next, result.VersionToken ?? string.Empty));
            } catch { return await ReconcileAsync(identity, next, null, cancellationToken).ConfigureAwait(false); }
        }
        return WorldAuthorityStoreOutcome.PreconditionFailed("definition publication lost the root compare-and-swap race");
    }
    /// <inheritdoc/>
    public async Task<WorldAuthorityStoreOutcome> RecordReceiptAsync(WorldAuthorityIdentity identity, WorldAuthorityOperationReceipt receipt, CancellationToken cancellationToken, WorldAuthorityFence? suppliedFence = null) {
        var fence = await EnsureFenceAsync(identity, suppliedFence, cancellationToken).ConfigureAwait(false);
        if (fence is not { } active) return WorldAuthorityStoreOutcome.Failed("could not acquire activation fence");
        for (var attempt = 0; attempt < MaxCasAttempts; attempt++) {
            var writable = await ReadMutationSnapshotAsync(identity, active, cancellationToken).ConfigureAwait(false);
            if (writable is not { } state) return WorldAuthorityStoreOutcome.StaleFence("activation fence is no longer current");
            var current = state.Snapshot;
            if (!FenceMatches(current, active)) return WorldAuthorityStoreOutcome.StaleFence("activation fence is no longer current");
            var prepared = await PrepareReceiptAsync(identity, current.Root, receipt, requireApplied: false, cancellationToken).ConfigureAwait(false);
            if (!prepared.Outcome.Ok) return prepared.Outcome;
            if (prepared.Outcome.Detail == "operation already durable") return WorldAuthorityStoreOutcome.Success(prepared.Outcome.Detail, current);
            var next = current.Root with { ReceiptHash = prepared.ReceiptHash, ReceiptIndexHash = prepared.ReceiptIndexHash, Sequence = checked(current.Root.Sequence + 1) };
            try {
                var result = await WriteRootAsync(identity, next, state.CreateOnly ? null : current.VersionToken, state.CreateOnly ? ObjectBlobWriteMode.CreateOnly : ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded) return WorldAuthorityStoreOutcome.Success(root: new WorldAuthorityRootSnapshot(next, result.VersionToken ?? string.Empty));
            } catch { return await ReconcileAsync(identity, next, receipt, cancellationToken).ConfigureAwait(false); }
        }
        return WorldAuthorityStoreOutcome.PreconditionFailed("receipt publication lost the root compare-and-swap race");
    }
    #pragma warning restore IDE0011
}
