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
        ) ||
            !WorldReleaseCompatibility.TryRequireMetadataCoordinator(
            reason: out reason,
            source: source,
            target: target
        )
        ) {
            return WorldAuthorityStoreOutcome.Failed(detail: reason);
        }
        var key = $"{identity.Owner:D}/{identity.World}";

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
        var groups = new WorldReleaseGroupStore(
            m_store,
            m_target,
            operation.Owner
        );

        async Task<bool> OwnsActivationAsync() {
            var current = await groups.LoadAsync(
                operation.DeploymentGroup,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            return (
                (current is { } group) &&
                (group.Record.PendingOperationId == operationId) &&
                (group.Record.PendingPhase == WorldReleaseOperationPhase.Activate) &&
                !group.Record.PendingCommitted &&
                (group.Record.Admission == WorldReleaseAdmissionState.Closed) &&
                (group.Record.PendingSourceRelease == source.Identity) &&
                (group.Record.PendingTargetRelease == target.Identity) &&
                group.Record.RecoveryRoots.TryGetValue(
                key: key,
                value: out var pin
            ) &&
                (pin == recoveryPin)
            );
        }
        if (!await OwnsActivationAsync().ConfigureAwait(continueOnCapturedContext: false)) {
            return WorldAuthorityStoreOutcome.PreconditionFailed(detail: "release publication no longer owns private activation");
        }
        var saved = await LoadRecoveryRootAsync(
            cancellationToken: cancellationToken,
            identity: identity,
            operationId: operationId,
            pin: recoveryPin
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (saved is not { } protectedRoot) ||
            (protectedRoot.Root.FenceToken != Guid.Empty) ||
            (protectedRoot.Root.CheckpointHash is null) ||
            (protectedRoot.Root.JournalEntryCount != 0) ||
            (protectedRoot.Root.JournalSequence != protectedRoot.Root.CheckpointCoverageSequence)
        ) {
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
            WorldAuthorityRecoveryRootCodec.ComputePin(bytes: Encoding.UTF8.GetBytes(s: $"{source.Identity}\n{target.Identity}\n{recoveryPin}")),
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
        var content = await ReadAsync(
            address: CheckpointCandidateAddress(
                identity,
                protectedRoot.Root.CheckpointOrdinal,
                protectedRoot.Root.CheckpointHash
            ),
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            (content is not { } checkpointBytes) ||
            (WorldDefinitionFileSource.ComputeContentHash(content: checkpointBytes.Content.Span) != protectedRoot.Root.CheckpointHash) ||
            !WorldAuthorityCheckpointCodec.TryDecode(
            bytes: checkpointBytes.Content.Span,
            checkpoint: out var checkpoint,
            reason: out reason
        )
        ) {
            return WorldAuthorityStoreOutcome.Failed(detail: "protected checkpoint cannot be decoded for release publication");
        }
        if (checkpoint!.Server.LastCompletedTick != protectedRoot.Root.CheckpointTick) {
            return WorldAuthorityStoreOutcome.Failed(detail: "protected checkpoint tick does not match its authority root");
        }
        // Package definitions retain unfilled boot draws. Only the checkpoint's live documents require resolved values.
        if (
            !WorldDefinitionFileSource.TryParseComposed(
            Encoding.UTF8.GetString(bytes: sourceBytes.Span),
            source.DefinitionFiles[key],
            null,
            false,
            out var sourceDefinition,
            out reason,
            machines
        ) ||
            !WorldDefinitionFileSource.TryParseComposed(
            Encoding.UTF8.GetString(bytes: targetBytes.Span),
            target.DefinitionFiles[key],
            null,
            false,
            out var targetDefinition,
            out reason,
            machines
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
                ordinal: ordinal
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
        var next = protectedRoot.Root with {
            DefinitionHash = definitionHash,
            CheckpointHash = checkpointHash,
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

            if (write.Succeeded) { return WorldAuthorityStoreOutcome.Success(root: new(
                Root: next,
                VersionToken: (write.VersionToken ?? string.Empty)
            )); }
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
}
