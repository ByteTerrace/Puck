using Puck.Storage;

namespace Puck.World.Server;

public sealed partial class WorldAuthorityBlobStore {
    /// <summary>Publishes a selected old checkpoint against the exact current drain root. Current receipts,
    /// journal numbering and boundary proof survive; the writer epoch and checkpoint ordinal advance.</summary>
    public async Task<WorldAuthorityStoreOutcome> PrepareIntentionalRestoreAsync(WorldAuthorityIdentity identity,
        WorldReleaseGroupRecord operation, WorldReleaseFixtureManifest point, ReadOnlyMemory<byte> definition,
        ReadOnlyMemory<byte> checkpoint, CancellationToken cancellationToken = default) {
        var key = $"{identity.Owner:D}/{identity.World}";
        if (operation.PendingOperationId is not { } operationId || operation.RestorePoint != new WorldReleaseRestoreSelection(point.RequestId, point.Identity) ||
            point.Release != operation.PendingTargetRelease || point.Owner != identity.Owner || !point.Worlds.TryGetValue(identity.World.Value, out var row) ||
            !operation.RecoveryRoots.TryGetValue(key, out var recoveryPin) || WorldReleaseFixtureArchive.Hash(checkpoint.Span) != row.Hash) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("restore request does not match the pinned group point");
        }
        var groups = new WorldReleaseGroupStore(m_store, m_target, operation.Owner);
        async Task<bool> OwnsAsync() {
            var state = await groups.LoadAsync(operation.DeploymentGroup, cancellationToken).ConfigureAwait(false);
            return state is { } found && found.Record.PendingOperationId == operationId && found.Record.RestorePoint == operation.RestorePoint &&
                found.Record.PendingPhase == WorldReleaseOperationPhase.Activate && !found.Record.PendingCommitted &&
                found.Record.Admission == WorldReleaseAdmissionState.Closed && found.Record.RecoveryRoots.TryGetValue(key, out var saved) && saved == recoveryPin;
        }
        if (!await OwnsAsync().ConfigureAwait(false)) { return WorldAuthorityStoreOutcome.PreconditionFailed("restore no longer owns private activation"); }
        var history = await new WorldReleaseFixtureArchive(m_store, m_target, identity.Owner)
            .ReadReceiptsAsync(point, identity.World.Value, cancellationToken).ConfigureAwait(false);
        if (history.Source.Root.RewindBoundary != point.RewindBoundary ||
            history.Source.Root.DefinitionHash != WorldDefinitionFileSource.ComputeContentHash(definition.Span) ||
            !WorldAuthorityCheckpointCodec.TryDecode(checkpoint.Span, out var decoded, out _) || decoded!.Server.LastCompletedTick != row.Tick) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("restore payload does not match the selected capture provenance");
        }
        var protectedRoot = await LoadRecoveryRootAsync(identity, recoveryPin, operationId, cancellationToken).ConfigureAwait(false);
        if (protectedRoot is not { } savedRoot || savedRoot.Root.FenceToken != Guid.Empty || point.RewindBoundary is null ||
            savedRoot.Root.RewindBoundary != point.RewindBoundary || savedRoot.Root.JournalEntryCount != 0 || savedRoot.Root.DurableTick < row.Tick) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("restore requires a complete later drain under the same boundary");
        }
        var current = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
        if (current is not { } present) { return WorldAuthorityStoreOutcome.PreconditionFailed("restore authority disappeared"); }
        var receipt = new WorldAuthorityOperationReceipt(operationId, "world.release.restore", point.Identity,
            "release.restore.applied", true, checked(savedRoot.Root.Sequence + 1), null);
        if (await FindReceiptFromRootAsync(identity, present.Root, operationId, cancellationToken).ConfigureAwait(false) is { } existing) {
            return existing == receipt ? WorldAuthorityStoreOutcome.Success("restore is already durable", present)
                : WorldAuthorityStoreOutcome.OperationConflict("operation already names another publication");
        }
        if (present.Root != savedRoot.Root || present.VersionToken != savedRoot.CapturedRootVersion) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("authority moved after the protected drain; nothing was replaced");
        }
        var definitionHash = WorldDefinitionFileSource.ComputeContentHash(definition.Span);
        var checkpointHash = WorldDefinitionFileSource.ComputeContentHash(checkpoint.Span);
        var ordinal = checked(present.Root.CheckpointOrdinal + 1);
        var definitionWrite = await PutImmutableAsync(DefinitionCandidateAddress(identity, definitionHash), definition, cancellationToken).ConfigureAwait(false);
        if (!definitionWrite.Ok) { return definitionWrite; }
        var checkpointWrite = await PutImmutableAsync(CheckpointCandidateAddress(identity, ordinal, checkpointHash), checkpoint, cancellationToken).ConfigureAwait(false);
        if (!checkpointWrite.Ok) { return checkpointWrite; }
        var prepared = await PrepareReceiptAsync(identity, present.Root, receipt, true, cancellationToken).ConfigureAwait(false);
        if (!prepared.Outcome.Ok) { return prepared.Outcome; }
        var next = present.Root with {
            DefinitionHash = definitionHash, CheckpointHash = checkpointHash, CheckpointOrdinal = ordinal, DurableOrdinal = ordinal,
            CheckpointTick = row.Tick, DurableTick = row.Tick, JournalHash = null, JournalEntryCount = 0,
            CheckpointCoverageSequence = present.Root.JournalSequence, ReceiptHash = prepared.ReceiptHash, ReceiptIndexHash = prepared.ReceiptIndexHash,
            Epoch = checked(present.Root.Epoch + 1), Sequence = checked(present.Root.Sequence + 1),
        };
        if (!await OwnsAsync().ConfigureAwait(false)) { return WorldAuthorityStoreOutcome.PreconditionFailed("restore lost private activation before publication"); }
        try {
            var write = await WriteRootAsync(identity, next, present.VersionToken, ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);
            return write.Succeeded ? WorldAuthorityStoreOutcome.Success(root: new(next, write.VersionToken ?? string.Empty))
                : WorldAuthorityStoreOutcome.PreconditionFailed("authority moved before restore publication");
        } catch (Exception error) when (error is not OperationCanceledException) {
            return await ReconcileAsync(identity, next, receipt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Records an enforced closed-group boundary under the current activation fence. The host calls
    /// this in its publication queue at first capture; subsequent activations may never silently remove it.</summary>
    public async Task<WorldAuthorityRootSnapshot> EstablishRewindBoundaryAsync(WorldAuthorityIdentity identity,
        WorldAuthorityFence fence, string boundary, CancellationToken cancellationToken = default) {
        if (!WorldAuthorityRecoveryRootCodec.IsPin(boundary)) { throw new ArgumentException("invalid rewind boundary", nameof(boundary)); }
        var current = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("rewind authority root is missing");
        if (!FenceMatches(current, fence)) { throw new InvalidOperationException("rewind capture lost its activation fence"); }
        if (current.Root.RewindBoundary is { } existing) {
            if (existing != boundary) { throw new InvalidOperationException("rewind boundary cannot change across an activation"); }
            return current;
        }
        var next = current.Root with { RewindBoundary = boundary, Sequence = checked(current.Root.Sequence + 1) };
        var written = await WriteRootAsync(identity, next, current.VersionToken, ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);
        if (!written.Succeeded) { throw new InvalidOperationException("authority changed while recording its rewind boundary; retry capture"); }
        return new(next, written.VersionToken ?? throw new IOException("rewind boundary has no version token"));
    }
}
