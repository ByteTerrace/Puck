using System.Text;
using Puck.Abstractions.Machines;
using Puck.Storage;

namespace Puck.World.Server;

public sealed partial class WorldAuthorityBlobStore {
    /// <summary>Publishes a package-bound metadata transition and its checkpoint together during private
    /// activation. The exact drained root guards the write; its recovery point and existing receipts survive.
    /// A matching durable operation receipt makes retries read-only, including after candidate activation.</summary>
    /// <param name="identity">The world named in both releases and the protected group inventory.</param>
    /// <param name="operation">The expected durable release operation, in Activate with admission closed.</param>
    /// <param name="source">The exact retained source manifest.</param>
    /// <param name="target">The exact retained target manifest.</param>
    /// <param name="archive">The verified package reader for the manifests' definition files.</param>
    /// <param name="cancellationToken">Cancels storage work. Uncertain publication can be retried with the same operation.</param>
    /// <param name="machines">The installed machine vocabulary for definition validation.</param>
    /// <returns>A completed publication, an idempotent retry, or a named refusal without replacing a newer root.</returns>
    public async Task<WorldAuthorityStoreOutcome> PrepareReleaseMetadataAsync(WorldAuthorityIdentity identity,
        WorldReleaseGroupRecord operation, WorldReleaseManifest source, WorldReleaseManifest target, WorldReleaseArchive archive,
        CancellationToken cancellationToken, IMachineValidationCatalog? machines = null) {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(archive);
        if (!WorldReleaseManifest.TryValidate(source, out var reason) || !WorldReleaseManifest.TryValidate(target, out reason) ||
            !WorldReleaseCompatibility.TryCheckStructuralCompatibility(source, target, out reason) ||
            !WorldReleaseCompatibility.TryRequireMetadataCoordinator(source, target, out reason)) {
            return WorldAuthorityStoreOutcome.Failed(reason);
        }
        var key = $"{identity.Owner:D}/{identity.World}";
        if (operation.PendingOperationId is not { } operationId || identity.Owner != operation.Owner ||
            !operation.RecoveryRoots.TryGetValue(key, out var recoveryPin) || !source.Definitions.ContainsKey(key) || !target.Definitions.ContainsKey(key)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("release publication requires the protected world and operation identity");
        }
        var groups = new WorldReleaseGroupStore(m_store, m_target, operation.Owner);
        async Task<bool> OwnsActivationAsync() {
            var current = await groups.LoadAsync(operation.DeploymentGroup, cancellationToken).ConfigureAwait(false);
            return current is { } group && group.Record.PendingOperationId == operationId &&
                group.Record.PendingPhase == WorldReleaseOperationPhase.Activate && !group.Record.PendingCommitted &&
                group.Record.Admission == WorldReleaseAdmissionState.Closed && group.Record.PendingSourceRelease == source.Identity &&
                group.Record.PendingTargetRelease == target.Identity && group.Record.RecoveryRoots.TryGetValue(key, out var pin) && pin == recoveryPin;
        }
        if (!await OwnsActivationAsync().ConfigureAwait(false)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("release publication no longer owns private activation");
        }
        var saved = await LoadRecoveryRootAsync(identity, recoveryPin, operationId, cancellationToken).ConfigureAwait(false);
        if (saved is not { } protectedRoot || protectedRoot.Root.FenceToken != Guid.Empty || protectedRoot.Root.CheckpointHash is null ||
            protectedRoot.Root.JournalEntryCount != 0 || protectedRoot.Root.JournalSequence != protectedRoot.Root.CheckpointCoverageSequence) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("release publication requires a fully checkpointed, unowned drain root");
        }
        var sourceBytes = await archive.ReadFileAsync(source, source.DefinitionFiles[key], cancellationToken).ConfigureAwait(false);
        var targetBytes = await archive.ReadFileAsync(target, target.DefinitionFiles[key], cancellationToken).ConfigureAwait(false);
        var definitionHash = WorldDefinitionFileSource.ComputeContentHash(targetBytes.Span);
        if (protectedRoot.Root.DefinitionHash != WorldDefinitionFileSource.ComputeContentHash(sourceBytes.Span)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("protected published definition does not match the source package");
        }
        var publishedSource = await ReadAsync(DefinitionCandidateAddress(identity, protectedRoot.Root.DefinitionHash), cancellationToken).ConfigureAwait(false);
        if (publishedSource is not { } sourceContent || !sourceContent.Content.Span.SequenceEqual(sourceBytes.Span)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("protected published definition bytes differ from the source package");
        }
        var receipt = new WorldAuthorityOperationReceipt(operationId, "world.release.metadata",
            WorldAuthorityRecoveryRootCodec.ComputePin(Encoding.UTF8.GetBytes($"{source.Identity}\n{target.Identity}\n{recoveryPin}")),
            "release.metadata.applied", true, checked(protectedRoot.Root.Sequence + 1), null);
        var snapshot = await ReadRootSnapshotAsync(identity, cancellationToken).ConfigureAwait(false);
        if (snapshot is not { } currentRoot) { return WorldAuthorityStoreOutcome.PreconditionFailed("authority root disappeared before release publication"); }
        if (await FindReceiptFromRootAsync(identity, currentRoot.Root, operationId, cancellationToken).ConfigureAwait(false) is { } existing) {
            if (existing != receipt || currentRoot.Root.DefinitionHash != definitionHash) {
                return WorldAuthorityStoreOutcome.OperationConflict("release operation already names a different publication");
            }
            await VerifyRecoveryPayloadsAsync(identity, currentRoot.Root, cancellationToken).ConfigureAwait(false);
            return WorldAuthorityStoreOutcome.Success("release metadata publication is already durable", currentRoot);
        }
        if (currentRoot.VersionToken != protectedRoot.CapturedRootVersion || currentRoot.Root != protectedRoot.Root) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("authority root changed after the protected drain; no state was replaced");
        }
        var content = await ReadAsync(CheckpointCandidateAddress(identity, protectedRoot.Root.CheckpointOrdinal, protectedRoot.Root.CheckpointHash), cancellationToken).ConfigureAwait(false);
        if (content is not { } checkpointBytes || WorldDefinitionFileSource.ComputeContentHash(checkpointBytes.Content.Span) != protectedRoot.Root.CheckpointHash ||
            !WorldAuthorityCheckpointCodec.TryDecode(checkpointBytes.Content.Span, out var checkpoint, out reason)) {
            return WorldAuthorityStoreOutcome.Failed("protected checkpoint cannot be decoded for release publication");
        }
        if (checkpoint!.Server.LastCompletedTick != protectedRoot.Root.CheckpointTick) {
            return WorldAuthorityStoreOutcome.Failed("protected checkpoint tick does not match its authority root");
        }
        // Package definitions retain unfilled boot draws. Only the checkpoint's live documents require resolved values.
        if (!WorldDefinitionFileSource.TryParseComposed(Encoding.UTF8.GetString(sourceBytes.Span), source.DefinitionFiles[key], null, false,
                out var sourceDefinition, out reason, machines) ||
            !WorldDefinitionFileSource.TryParseComposed(Encoding.UTF8.GetString(targetBytes.Span), target.DefinitionFiles[key], null, false,
                out var targetDefinition, out reason, machines) ||
            !WorldReleaseMetadataTransition.TryApply(sourceDefinition!, targetDefinition!, checkpoint, out var transformed, out reason, machines)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(reason);
        }
        var encoded = WorldAuthorityCheckpointCodec.Encode(transformed!);
        var checkpointHash = WorldDefinitionFileSource.ComputeContentHash(encoded);
        var ordinal = checked(protectedRoot.Root.CheckpointOrdinal + 1);
        var definitionWrite = await PutImmutableAsync(DefinitionCandidateAddress(identity, definitionHash), targetBytes, cancellationToken).ConfigureAwait(false);
        if (!definitionWrite.Ok) { return definitionWrite; }
        var checkpointWrite = await PutImmutableAsync(CheckpointCandidateAddress(identity, ordinal, checkpointHash), encoded, cancellationToken).ConfigureAwait(false);
        if (!checkpointWrite.Ok) { return checkpointWrite; }
        var prepared = await PrepareReceiptAsync(identity, protectedRoot.Root, receipt, requireApplied: true, cancellationToken).ConfigureAwait(false);
        if (!prepared.Outcome.Ok) { return prepared.Outcome; }
        var next = protectedRoot.Root with {
            DefinitionHash = definitionHash, CheckpointHash = checkpointHash, CheckpointOrdinal = ordinal, DurableOrdinal = ordinal,
            ReceiptHash = prepared.ReceiptHash, ReceiptIndexHash = prepared.ReceiptIndexHash,
            Epoch = checked(protectedRoot.Root.Epoch + 1), Sequence = checked(protectedRoot.Root.Sequence + 1),
        };
        if (!await OwnsActivationAsync().ConfigureAwait(false)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed("release publication lost private activation before publishing");
        }
        try {
            var write = await WriteRootAsync(identity, next, protectedRoot.CapturedRootVersion, ObjectBlobWriteMode.Overwrite, cancellationToken).ConfigureAwait(false);
            if (write.Succeeded) { return WorldAuthorityStoreOutcome.Success(root: new(next, write.VersionToken ?? string.Empty)); }
            return WorldAuthorityStoreOutcome.PreconditionFailed("authority root moved before release publication; no state was replaced");
        } catch (Exception error) when (error is not OperationCanceledException) {
            return await ReconcileAsync(identity, next, receipt, cancellationToken).ConfigureAwait(false);
        }
    }
}
