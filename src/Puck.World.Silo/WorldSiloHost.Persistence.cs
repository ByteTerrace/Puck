using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    private async Task<WorldAuthorityStoreOutcome> PublishDefinitionCoreAsync(WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken ct) {
        var admission = new TaskCompletionSource<Task<WorldAuthorityStoreOutcome>>(TaskCreationOptions.RunContinuationsAsynchronously);
        m_mailbox.Enqueue(() => {
            if (ct.IsCancellationRequested) { admission.TrySetCanceled(ct); return; }
            if (FindWorldRow(identity) is null) {
                admission.TrySetResult(Task.FromResult(WorldAuthorityStoreOutcome.Failed("The world is not declared in this silo.")));
                return;
            }
            if (m_rows.TryGetValue(identity.World.Value, out var bookkeeping)) {
                if (bookkeeping.Released || !Instances.TryGet(identity.World.Value, out var row) || row is null || row.Server.IsRetiring) {
                    admission.TrySetResult(Task.FromResult(WorldAuthorityStoreOutcome.Failed("The activation is retiring.")));
                    return;
                }
                var queued = PublishAfterAsync(bookkeeping.JournalTail, bookkeeping, identity, composed, ct);
                bookkeeping.JournalTail = queued;
                admission.TrySetResult(queued);
            } else {
                admission.TrySetResult(m_store.PublishDefinitionAsync(identity, composed, ct));
            }
        });
        return await (await admission.Task.WaitAsync(ct)).WaitAsync(ct);
    }

    private async Task<WorldAuthorityStoreOutcome> PublishAfterAsync(Task previous, RowBookkeeping bookkeeping,
        WorldAuthorityIdentity identity, WorldDefinition composed, CancellationToken ct) {
        await ObservePersistenceAsync(previous, ct);
        if (bookkeeping.PersistenceBlocked) { return WorldAuthorityStoreOutcome.RecoveryRequired("This activation must recover before publishing again."); }
        var outcome = await m_store.PublishDefinitionAsync(identity, composed, ct, bookkeeping.Fence);
        ObservePublication(bookkeeping, outcome);
        return outcome;
    }

    // Called only by the activation's serialized publication queue. A stale or uncertain writer is never
    // healed by rereading another activation's root and adopting its fence.
    private static void ObservePublication(RowBookkeeping bookkeeping, WorldAuthorityStoreOutcome outcome) {
        if (outcome.Kind is WorldAuthorityStoreOutcomeKind.StaleFence or WorldAuthorityStoreOutcomeKind.RecoveryRequired) {
            bookkeeping.PersistenceBlocked = true;
        }
        if (outcome.Ok) {
            if (outcome.PublishedRoot is not { } publication ||
                publication.Root.Epoch != bookkeeping.Fence.Epoch || publication.Root.FenceToken != bookkeeping.Fence.Token) {
                bookkeeping.PersistenceBlocked = true;
                throw new InvalidDataException("A successful authority publication did not return this activation's root.");
            }
            bookkeeping.PublishedJournalSequence = publication.Root.JournalSequence;
        }
    }
}
