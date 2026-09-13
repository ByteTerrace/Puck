namespace Puck.World.Server;

public sealed partial class WorldAuthorityBlobStore {
    /// <summary>Creates a disposable authority from a coherent capture and its exact receipt history. It
    /// preserves source sequence/ordinal meaning, covers the captured journal with the new checkpoint, and
    /// publishes an unowned root last. The caller owns a fresh disposable destination; existing rooted or
    /// legacy state refuses, and a competing root publication wins over this create-only operation.</summary>
    /// <param name="identity">The source identity, also used in the disposable fixture.</param>
    /// <param name="definition">Exact published source definition bytes.</param>
    /// <param name="checkpoint">Complete checkpoint captured at the receipt root's publication boundary.</param>
    /// <param name="history">Detached source receipt graph and root provenance.</param>
    /// <param name="cancellationToken">Cancels writes; incomplete objects have no authority root.</param>
    /// <returns>The created root, or a named refusal without replacing existing authority.</returns>
    /// <exception cref="InvalidDataException">The receipt graph is corrupt, incomplete or oversized.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> is null.</exception>
    public async Task<WorldAuthorityStoreOutcome> CreateReleaseFixtureAsync(WorldAuthorityIdentity identity,
        ReadOnlyMemory<byte> definition, ReadOnlyMemory<byte> checkpoint, WorldAuthorityReceiptSnapshot history,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(history);
        _ = history.Validate();
        // Own all caller buffers before the first await.
        var definitionBytes = definition.ToArray();
        var checkpointBytes = checkpoint.ToArray();
        var detached = history with { Index = history.Index.ToArray(), Nodes = history.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal) };
        _ = detached.Validate();
        if (detached.Owner != identity.Owner || detached.World != identity.World.Value ||
            WorldDefinitionFileSource.ComputeContentHash(definitionBytes) != detached.Source.Root.DefinitionHash) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("fixture identity or published definition differs from the captured receipt root");
        }
        if (!WorldAuthorityCheckpointCodec.TryDecode(checkpointBytes, out var captured, out var reason)) {
            return WorldAuthorityStoreOutcome.Failed("fixture checkpoint is invalid: " + reason);
        }
        var tick = captured!.Server.LastCompletedTick;
        if (tick < detached.Source.Root.CheckpointTick || tick < detached.Source.Root.DurableTick) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("fixture checkpoint predates its captured authority root");
        }
        if (await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false) is not null ||
            await ReadAsync(LatestPointerAddress(identity.Owner, identity.World), cancellationToken).ConfigureAwait(false) is not null ||
            await ReadAsync(WorldOwnedWorldSync.HostedAddressFor(identity.Owner, identity.World, "definition.json"), cancellationToken).ConfigureAwait(false) is not null) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("fixture creation requires a new world with no existing authority or legacy state");
        }
        var checkpointHash = WorldDefinitionFileSource.ComputeContentHash(checkpointBytes);
        var ordinal = checked(detached.Source.Root.CheckpointOrdinal + 1);
        var root = detached.Source.Root with {
            Epoch = checked(detached.Source.Root.Epoch + 1), FenceToken = Guid.Empty, Sequence = checked(detached.Source.Root.Sequence + 1),
            CheckpointHash = checkpointHash, CheckpointOrdinal = ordinal, CheckpointTick = tick,
            JournalHash = null, JournalEntryCount = 0, CheckpointCoverageSequence = detached.Source.Root.JournalSequence,
            DurableOrdinal = ordinal, DurableTick = tick,
        };
        var written = await PutImmutableAsync(DefinitionCandidateAddress(identity, root.DefinitionHash!), definitionBytes, cancellationToken).ConfigureAwait(false);
        if (!written.Ok) { return written; }
        written = await PutImmutableAsync(CheckpointCandidateAddress(identity, ordinal, checkpointHash), checkpointBytes, cancellationToken).ConfigureAwait(false);
        if (!written.Ok) { return written; }
        foreach (var node in detached.Nodes) {
            written = await PutImmutableAsync(ReceiptCandidateAddress(identity, node.Key), node.Value, cancellationToken).ConfigureAwait(false);
            if (!written.Ok) { return written; }
        }
        if (root.ReceiptIndexHash is { } indexPin) {
            written = await PutImmutableAsync(ReceiptIndexAddress(identity, indexPin), detached.Index, cancellationToken).ConfigureAwait(false);
            if (!written.Ok) { return written; }
        }
        var published = await WriteRootAsync(identity, root, null, Puck.Storage.ObjectBlobWriteMode.CreateOnly, cancellationToken).ConfigureAwait(false);
        return published.Succeeded ? WorldAuthorityStoreOutcome.Success(root: new(root, published.VersionToken ?? string.Empty))
            : WorldAuthorityStoreOutcome.PreconditionFailed("fixture root was created by another writer; no authority was replaced");
    }

    /// <summary>Exports the exact receipt graph of a previously selected root. Later publications do not enter
    /// the export. The caller must select the root in its checkpoint capture/publication queue before calling.</summary>
    /// <param name="identity">The authority whose immutable receipt objects are read.</param>
    /// <param name="source">The root selected at the capture boundary; this method never resamples it.</param>
    /// <param name="cancellationToken">Cancels bounded storage reads.</param>
    /// <returns>A detached, validated copy of the complete original index and chain.</returns>
    /// <exception cref="InvalidDataException">A referenced object is missing, corrupt, inconsistent or oversized.</exception>
    public async Task<WorldAuthorityReceiptSnapshot> CaptureReceiptSnapshotAsync(WorldAuthorityIdentity identity,
        WorldAuthorityRootSnapshot source, CancellationToken cancellationToken = default) {
        var index = Array.Empty<byte>();
        long bytes = 0;
        var nodes = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        async Task<byte[]> ReadPayloadAsync(Puck.Storage.ObjectBlobAddress address, string pin) {
            var found = await ReadAsync(address, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("receipt snapshot names a missing immutable object");
            bytes += found.Content.Length;
            if (bytes > WorldAuthorityReceiptSnapshot.MaximumBytes || WorldDefinitionFileSource.ComputeContentHash(found.Content.Span) != pin) {
                throw new InvalidDataException("receipt snapshot object is corrupt or exceeds its byte budget");
            }
            return found.Content.ToArray();
        }
        if (source.Root.ReceiptIndexHash is { } indexPin) {
            index = await ReadPayloadAsync(ReceiptIndexAddress(identity, indexPin), indexPin).ConfigureAwait(false);
        }
        var head = source.Root.ReceiptHash;
        while (head is not null) {
            if (nodes.Count >= WorldAuthorityReceiptSnapshot.MaximumReceipts || nodes.ContainsKey(head)) {
                throw new InvalidDataException("receipt snapshot chain is cyclic or exceeds its traversal bound");
            }
            var node = await ReadPayloadAsync(ReceiptCandidateAddress(identity, head), head).ConfigureAwait(false);
            nodes.Add(head, node);
            if (!WorldAuthorityRootCodec.TryDecodeReceipt(node, out _, out head, out var reason)) {
                throw new InvalidDataException("receipt snapshot node is invalid: " + reason);
            }
        }
        var snapshot = new WorldAuthorityReceiptSnapshot(identity.Owner, identity.World.Value, source, index, nodes);
        _ = snapshot.Validate();
        return snapshot;
    }
}
