using System.Text;
using Puck.Abstractions.Machines;
using Puck.Assets;
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
        if (
            !WorldReleaseManifest.TryValidate(
            manifest: source,
            reason: out var reason
        ) ||
            !WorldReleaseManifest.TryValidate(
            manifest: target,
            reason: out reason
        ) ||
            !WorldReleaseCompatibility.TryCheckStructuralCompatibility(
            candidate: target,
            previous: source,
            reason: out reason
        )
        ) {
            return WorldAuthorityStoreOutcome.Failed(detail: reason);
        }
        var key = identity.RootKey;

        if (
            (operation.PendingOperationId is not { } operationId) ||
            (identity.Owner != operation.Owner) ||
            !operation.RecoveryRoots.TryGetValue(
            key: key,
            value: out var recoveryPin
        ) ||
            !source.Definitions.ContainsKey(key: key) ||
            !target.Definitions.ContainsKey(key: key)
        ) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "release publication requires the protected world and operation identity");
        }
        if (
            (operation.PendingSourceRelease != source.Identity) ||
            (operation.PendingTargetRelease != target.Identity)
        ) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "release publication requires the operation's own source and target");
        }
        Task<bool> OwnsActivationAsync() => StillOwnsActivationAsync(
            cancellationToken: cancellationToken,
            key: key,
            operation: operation,
            recoveryPin: recoveryPin
        );

        if (!await OwnsActivationAsync().ConfigureAwait(continueOnCapturedContext: false)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "release publication no longer owns private activation");
        }
        var saved = await LoadRecoveryRootAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            operationId: operationId,
            pin: recoveryPin
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (saved is not { Root.IsDrainedCheckpoint: true } protectedRoot) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "release publication requires a fully checkpointed, unowned drain root");
        }
        var sourceBytes = await archive.ReadFileAsync(
            source,
            source.DefinitionFiles[key],
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        var targetBytes = await archive.ReadFileAsync(
            target,
            target.DefinitionFiles[key],
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        var definitionHash = WorldDefinitionFileSource.ComputeContentHash(content: targetBytes.Span);

        if (protectedRoot.Root.DefinitionHash != WorldDefinitionFileSource.ComputeContentHash(content: sourceBytes.Span)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "protected published definition does not match the source package");
        }
        var publishedSource = await ReadAsync(
            address: DefinitionCandidateAddress(
                identity,
                protectedRoot.Root.DefinitionHash
            ),
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (publishedSource is not { } sourceContent) ||
            !sourceContent.Content.Span.SequenceEqual(other: sourceBytes.Span)
        ) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "protected published definition bytes differ from the source package");
        }
        var receipt = new WorldAuthorityOperationReceipt(
            operationId,
            "world.release.metadata",
            ContentPin.OfText(text: $"{source.Identity}\n{target.Identity}\n{recoveryPin}").ToString(),
            "release.metadata.applied",
            true,
            checked((protectedRoot.Root.Sequence + 1)),
            null
        );
        var snapshot = await ReadRootSnapshotAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (snapshot is not { } currentRoot) { return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "authority root disappeared before release publication"); }
        if (await FindReceiptFromRootAsync(
            identity,
            currentRoot.Root,
            operationId,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false) is { } existing) {
            if (
                (existing != receipt) ||
                (currentRoot.Root.DefinitionHash != definitionHash)
            ) {
                return WorldAuthorityStoreOutcome.OperationConflict(detail: "release operation already names a different publication");
            }
            await VerifyRecoveryPayloadsAsync(
                identity,
                currentRoot.Root,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
            return WorldAuthorityStoreOutcome.Success(
                detail: "release metadata publication is already durable",
                root: currentRoot
            );
        }
        if (
            (currentRoot.VersionToken != protectedRoot.CapturedRootVersion) ||
            (currentRoot.Root != protectedRoot.Root)
        ) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "authority root changed after the protected drain; no state was replaced");
        }
        var checkpointBytes = await ReadPinnedPayloadAsync(identity, operationId,
            CheckpointCandidateAddress(identity, protectedRoot.Root.CheckpointStorageSequence, protectedRoot.Root.CheckpointHash),
            protectedRoot.Root.CheckpointHash, cancellationToken, archiveOnly: true).ConfigureAwait(false);

        if (!WorldAuthorityCheckpointCodec.TryDecode(
            bytes: checkpointBytes.Span,
            checkpoint: out var checkpoint,
            documentDirectory: m_documentDirectory,
            reason: out reason
        )) {
            return WorldAuthorityStoreOutcome.Failed(detail: "protected checkpoint cannot be decoded for release publication");
        }
        if (checkpoint!.Server.LastCompletedTick != protectedRoot.Root.CheckpointTick) {
            return WorldAuthorityStoreOutcome.Failed(detail: "protected checkpoint tick does not match its authority root");
        }
        // Package definitions are published undrawn and only parsed here: the transition compares their metadata and
        // rewrites the checkpoint's live documents, the only ones that hold drawn cells.
        if (
            !WorldDefinitionFileSource.TryParseDocument(
            definition: out var sourceDefinition,
            json: Encoding.UTF8.GetString(bytes: sourceBytes.Span),
            reason: out reason,
            sourceName: source.DefinitionFiles[key]
        ) ||
            !WorldDefinitionFileSource.TryParseDocument(
            definition: out var targetDefinition,
            json: Encoding.UTF8.GetString(bytes: targetBytes.Span),
            reason: out reason,
            sourceName: target.DefinitionFiles[key]
        ) ||
            !WorldReleaseMetadataTransition.TryApply(
            after: targetDefinition!,
            before: sourceDefinition!,
            current: checkpoint,
            machines: machines,
            reason: out reason,
            transitioned: out var transformed
        )
        ) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: reason);
        }
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: transformed!);
        var checkpointHash = WorldDefinitionFileSource.ComputeContentHash(content: encoded);
        var ordinal = checked((protectedRoot.Root.CheckpointOrdinal + 1));
        var definitionWrite = await PutImmutableAsync(
            address: DefinitionCandidateAddress(
                hash: definitionHash,
                identity: identity
            ),
            bytes: targetBytes,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!definitionWrite.Ok) { return definitionWrite; }
        var checkpointWrite = await PutImmutableAsync(
            address: CheckpointCandidateAddress(
                hash: checkpointHash,
                identity: identity,
                sequence: checked(protectedRoot.Root.Sequence + 1)
            ),
            bytes: encoded,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!checkpointWrite.Ok) { return checkpointWrite; }
        var prepared = await PrepareReceiptAsync(
            identity,
            protectedRoot.Root,
            receipt,
            requireApplied: true,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!prepared.Outcome.Ok) { return prepared.Outcome; }
        var next = currentRoot.Root with {
            DefinitionHash = definitionHash,
            CheckpointHash = checkpointHash,
            CheckpointStorageSequence = checked(protectedRoot.Root.Sequence + 1),
            CheckpointOrdinal = ordinal,
            DurableOrdinal = ordinal,
            ReceiptHash = prepared.ReceiptHash,
            ReceiptIndexHash = prepared.ReceiptIndexHash,
            Epoch = checked((protectedRoot.Root.Epoch + 1)),
            Sequence = checked((protectedRoot.Root.Sequence + 1)),
        };

        if (!await OwnsActivationAsync().ConfigureAwait(continueOnCapturedContext: false)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "release publication lost private activation before publishing");
        }
        try {
            var write = await WriteRootAsync(
                identity,
                next,
                protectedRoot.CapturedRootVersion,
                ObjectBlobWriteMode.Overwrite,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (write.Succeeded) {
                return WorldAuthorityStoreOutcome.Success(root: new(
                Root: next,
                VersionToken: (write.VersionToken ?? string.Empty)
            ));
            }
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "authority root moved before release publication; no state was replaced");
        } catch (Exception error) when ((error is not OperationCanceledException)) {
            return await ReconcileAsync(
                cancellationToken: cancellationToken,
                expected: next,
                identity: identity,
                receipt: receipt
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    /// <summary>Replaces a world's drained source root with the target release's fresh state during the private
    /// activation of a reset operation (<see cref="WorldReleaseGroupRecord.PendingStateReset"/>): the target's published
    /// definition and no checkpoint, journal or receipts, as a first deployment starts it. The writer epoch and the
    /// publication sequence advance and the rewind boundary is kept. The source's state is never read, which is why a
    /// reset can follow state this build cannot decode, and the protected recovery root is untouched, so pre-commit
    /// recovery restores the source exactly. A retry finds the identical reset root and changes nothing.</summary>
    /// <param name="identity">The world named in the target release and the protected group inventory.</param>
    /// <param name="operation">The expected durable reset operation, in Activate with admission closed.</param>
    /// <param name="target">The exact retained target manifest.</param>
    /// <param name="archive">The verified package reader for the target's definition file.</param>
    /// <param name="cancellationToken">The token that cancels storage work.</param>
    /// <returns>The reset root, an idempotent retry, or a named refusal that replaced nothing.</returns>
    public async Task<WorldAuthorityStoreOutcome> PrepareReleaseResetAsync(WorldAuthorityIdentity identity, WorldReleaseGroupRecord operation,
        WorldReleaseManifest target, WorldReleaseArchive archive, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(archive);
        var key = identity.RootKey;

        if (
            (operation.PendingOperationId is not { } operationId) ||
            (operation.PendingStateReset is null) ||
            (operation.PendingTargetRelease != target.Identity) ||
            (identity.Owner != operation.Owner) ||
            !operation.RecoveryRoots.TryGetValue(
                key: key,
                value: out var recoveryPin
            ) ||
            !target.DefinitionFiles.TryGetValue(
                key: key,
                value: out var definitionFile
            )
        ) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "release reset requires a reset operation, its protected world and the target's definition");
        }
        if (!await StillOwnsActivationAsync(
            cancellationToken: cancellationToken,
            key: key,
            operation: operation,
            recoveryPin: recoveryPin
        ).ConfigureAwait(continueOnCapturedContext: false)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "release reset no longer owns private activation");
        }
        var saved = await LoadRecoveryRootAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            operationId: operationId,
            pin: recoveryPin
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (saved is not { } protectedRoot) ||
            (protectedRoot.Root.FenceToken != Guid.Empty)
        ) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "release reset requires an unowned drain root");
        }
        var definition = await archive.ReadFileAsync(
            target,
            definitionFile,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        var definitionHash = WorldDefinitionFileSource.ComputeContentHash(content: definition.Span);
        var next = WorldAuthorityRoot.Empty with {
            DefinitionHash = definitionHash,
            Epoch = checked((protectedRoot.Root.Epoch + 1)),
            RewindBoundary = protectedRoot.Root.RewindBoundary,
            Sequence = checked((protectedRoot.Root.Sequence + 1)),
        };
        var snapshot = await ReadRootSnapshotAsync(
            cancellationToken: cancellationToken,
            identity: identity
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (snapshot is not { } currentRoot) { return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "authority root disappeared before release reset"); }
        if (currentRoot.Root == next) {
            return WorldAuthorityStoreOutcome.Success(
                detail: "release reset is already durable",
                root: currentRoot
            );
        }
        if (
            (currentRoot.VersionToken != protectedRoot.CapturedRootVersion) ||
            (currentRoot.Root != protectedRoot.Root)
        ) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "authority root changed after the protected drain; no state was replaced");
        }
        var definitionWrite = await PutImmutableAsync(
            address: DefinitionCandidateAddress(
                hash: definitionHash,
                identity: identity
            ),
            bytes: definition,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!definitionWrite.Ok) { return definitionWrite; }
        if (!await StillOwnsActivationAsync(
            cancellationToken: cancellationToken,
            key: key,
            operation: operation,
            recoveryPin: recoveryPin
        ).ConfigureAwait(continueOnCapturedContext: false)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "release reset lost private activation before publishing");
        }
        try {
            var write = await WriteRootAsync(
                identity,
                next,
                protectedRoot.CapturedRootVersion,
                ObjectBlobWriteMode.Overwrite,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            return (write.Succeeded
                ? WorldAuthorityStoreOutcome.Success(root: new(
                    Root: next,
                    VersionToken: (write.VersionToken ?? string.Empty)
                ))
                : WorldAuthorityStoreOutcome.PreconditionFailed(detail: "authority root moved before release reset; no state was replaced"));
        } catch (Exception error) when ((error is not OperationCanceledException)) {
            // The reset root is fully determined by the protected root and the target, so a lost response is settled by
            // reading the root back rather than by a receipt.
            return ((await ReadRootSnapshotAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false) is { } reread) && (reread.Root == next))
                ? WorldAuthorityStoreOutcome.Success(
                    detail: "release reset is durable after an uncertain write",
                    root: reread
                )
                : WorldAuthorityStoreOutcome.Failed(detail: $"release reset write is uncertain: {error.Message}");
        }
    }
    // Whether the durable group still holds exactly operation in private Activate, with admission closed and key's
    // protected root unchanged. Every write a release makes over a drained root during Activate rechecks this before and
    // after its uploads, so a delayed writer of an obsolete operation or phase cannot publish.
    private async Task<bool> StillOwnsActivationAsync(WorldReleaseGroupRecord operation, string key, string recoveryPin, CancellationToken cancellationToken) {
        var current = await new WorldReleaseGroupStore(
            m_store,
            m_target,
            operation.Owner
        ).LoadAsync(
            operation.DeploymentGroup,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (
            (current is { Record: var group }) &&
            (group.PendingOperationId == operation.PendingOperationId) &&
            (group.PendingSourceRelease == operation.PendingSourceRelease) &&
            (group.PendingTargetRelease == operation.PendingTargetRelease) &&
            (group.RestorePoint == operation.RestorePoint) &&
            (group.PendingPhase == WorldReleaseOperationPhase.Activate) &&
            !group.PendingCommitted &&
            (group.Admission == WorldReleaseAdmissionState.Closed) &&
            group.RecoveryRoots.TryGetValue(
                key: key,
                value: out var pin
            ) &&
            (pin == recoveryPin)
        );
    }
}
