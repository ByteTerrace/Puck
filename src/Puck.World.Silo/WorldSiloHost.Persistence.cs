using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    private async Task<WorldAuthorityStoreOutcome> PublishDefinitionCoreAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken ct) {
        var admission = new TaskCompletionSource<Task<WorldAuthorityStoreOutcome>>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        m_mailbox.Enqueue(item: () => {
            if (ct.IsCancellationRequested) { admission.TrySetCanceled(cancellationToken: ct); return; }
            if (FindWorldRow(identity: identity) is null) {
                admission.TrySetResult(result: Task.FromResult(result: WorldAuthorityStoreOutcome.Failed(detail: "The world is not declared in this silo.")));
                return;
            }
            if (m_rows.TryGetValue(
                key: identity.World.Value,
                value: out var bookkeeping
            )) {
                if (
                    bookkeeping.Released ||
                    !Instances.TryGet(
                    identity.World.Value,
                    out var row
                ) ||
                    (row is null) ||
                    row.Server.IsRetiring
                ) {
                    admission.TrySetResult(result: Task.FromResult(result: WorldAuthorityStoreOutcome.Failed(detail: "The activation is retiring.")));
                    return;
                }
                var queued = PublishAfterAsync(
                    bookkeeping: bookkeeping,
                    composed: composed,
                    ct: ct,
                    identity: identity,
                    previous: bookkeeping.JournalTail
                );

                bookkeeping.JournalTail = queued;
                admission.TrySetResult(result: queued);
            } else {
                admission.TrySetResult(result: m_store.PublishDefinitionAsync(
                    identity,
                    composed,
                    ct
                ));
            }
        });
        return await (await admission.Task.WaitAsync(cancellationToken: ct)).WaitAsync(cancellationToken: ct);
    }
    private async Task<WorldAuthorityStoreOutcome> PublishAfterAsync(Task previous, RowBookkeeping bookkeeping,
        WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken ct) {
        await ObservePersistenceAsync(
            ct: ct,
            operation: previous
        );
        if (bookkeeping.PersistenceBlocked) { return WorldAuthorityStoreOutcome.RecoveryRequired(detail: "This activation must recover before publishing again."); }
        var outcome = await m_store.PublishDefinitionAsync(
            identity,
            composed,
            ct,
            bookkeeping.Fence
        );

        ObservePublication(
            bookkeeping: bookkeeping,
            outcome: outcome
        );
        return outcome;
    }
    // Called only by the activation's serialized publication queue. A stale or uncertain writer is never
    // healed by rereading another activation's root and adopting its fence.
    private static void ObservePublication(RowBookkeeping bookkeeping, WorldAuthorityStoreOutcome outcome) {
        if (outcome.Kind is WorldAuthorityStoreOutcomeKind.StaleFence or WorldAuthorityStoreOutcomeKind.RecoveryRequired) {
            bookkeeping.PersistenceBlocked = true;
        }
        if (outcome.Ok) {
            if (
                (outcome.PublishedRoot is not { } publication) ||
                (publication.Root.Epoch != bookkeeping.Fence.Epoch) ||
                (publication.Root.FenceToken != bookkeeping.Fence.Token)
            ) {
                bookkeeping.PersistenceBlocked = true;
                throw new InvalidDataException(message: "A successful authority publication did not return this activation's root.");
            }
            bookkeeping.PublishedJournalSequence = publication.Root.JournalSequence;
        }
    }
}
