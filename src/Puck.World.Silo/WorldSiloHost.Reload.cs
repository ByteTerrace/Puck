using Puck.Commands;
using System.Text;
using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    private const string ReloadVerb = "silo.reload";

    private readonly SemaphoreSlim m_reload = new(
        initialCount: 1,
        maxCount: 1
    );
    // The release each row has taken but not yet made durable: the published definition's pin, and the completion of
    // the rebuild that applies it. An entry stands until the row's checkpoint and release marker land.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<WorldAuthorityIdentity, (string Hash, Task Applied)> m_pendingReleases = new();
    // The silo's own reloads register here, under each row's name, and never in the console's table: they are not
    // console lines, so their verdicts print and count nowhere.
    private readonly WorldDeferredVerbEchoes m_reloadEchoes = new();

    /// <summary>Applies changed published content through the reload door and checkpoints it before recording its release.</summary>
    /// <param name="identity">The declared, active world to reconcile.</param>
    /// <param name="cancellationToken">Bounds storage reads, pump admission, and persistence.</param>
    /// <returns>The content pin of the published definition this world accepted: the hash of its bytes as
    /// published, which the world's source revision carries.</returns>
    public Task<string> ReloadAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        lock (m_drainLock) {
            if (m_drainTask is not null) { throw new InvalidOperationException(message: "The silo is retiring."); }
            ObserveCompleted(operations: m_persistenceOperations);
            var operation = ReloadCoreAsync(
                cancellationToken: cancellationToken,
                identity: identity
            );

            m_persistenceOperations.Add(item: operation);
            return operation;
        }
    }

    private async Task<string> ReloadCoreAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        await m_reload.WaitAsync(cancellationToken: cancellationToken);
        try {
            if (FindWorldRow(identity: identity) is null) { throw new InvalidOperationException(message: "World is not declared in this silo."); }
            var origin = new WorldHostedOrigin(
                owner: identity.Owner,
                world: identity.World,
                store: m_blobStore,
                target: m_storageTarget,
                timeProvider: m_clock,
                documentDirectory: m_documentDirectory
            );
            var (published, hash, reason) = await origin.ReadPublishedAsync(cancellationToken: cancellationToken);

            if (published is not { } bytes) { throw new InvalidDataException(message: reason); }
            var address = WorldOwnedWorldSync.HostedAddressFor(
                identity.Owner,
                identity.World,
                "release"
            );
            var previous = await m_blobStore.ReadAsync(
                address: address,
                cancellationToken: cancellationToken,
                target: m_storageTarget
            );

            if (
                (previous is { } released) &&
                (Encoding.UTF8.GetString(bytes: released.Content.Span) == hash)
            ) {
                m_pendingReleases.TryRemove(
                    key: identity,
                    value: out _
                );
                return hash;
            }
            // An earlier reload's rebuild settles first: what it applied is the revision this one is cut from.
            if (m_pendingReleases.TryGetValue(key: identity, value: out var earlier)) {
                await ObservePersistenceAsync(ct: cancellationToken, operation: earlier.Applied);
            }
            // The next revision, read as an edit off the pump: the row's authority validates what it installs once.
            if (!origin.TryPrepareEdit(definition: out var definition, instanceIdentity: identity.World.Value, keys: WorldPrivateDrawKeys.Preview, published: bytes, reason: out reason)) {
                throw new InvalidDataException(message: reason);
            }

            var applied = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            Post(action: () => {
                if (cancellationToken.IsCancellationRequested) { applied.TrySetCanceled(cancellationToken: cancellationToken); return; }
                try {
                    if (
                        IsDraining ||
                        !Instances.TryGet(
                        identity.World.Value,
                        out var row
                    ) ||
                        (row is null) ||
                        row.Server.IsRetiring ||
                        !m_rows.TryGetValue(key: row.Name, value: out var bookkeeping)
                    ) { throw new InvalidOperationException(message: "World is inactive or retiring."); }
                    // Compare the activation's binding: a recovered checkpoint may advertise the prior deployment.
                    if (
                        (row.Federation.Subject != FederationSubject(definition: definition!, world: identity.World)) ||
                        (row.ListenEndpoint != definition!.Host.Listen) ||
                        (row.AdvertisedEndpoint != definition.Host.Authority)
                    ) {
                        throw new InvalidOperationException(message: "Changing a world's network binding requires a fresh activation.");
                    }
                    // A source that still composes to the revision the row holds sends nothing: a crash can leave the
                    // rebuilt checkpoint durable before its release marker, and the recovered state stands.
                    var pending = bookkeeping.Reloads.Begin(
                        origin: origin.Store,
                        principal: Principal.Console,
                        read: (revision, _) => ValueTask.FromResult(result: new WorldSourceRead(ContentHash: hash, Definition: ((revision.ContentHash == hash) ? null : definition))),
                        verb: ReloadVerb
                    );

                    m_pendingReleases[identity] = (hash, applied.Task);
                    pending.Verdict.OnSettled(continuation: verdict => {
                        if (verdict.IsError) {
                            m_pendingReleases.TryRemove(key: identity, value: out _);
                            applied.TrySetException(exception: new InvalidOperationException(message: verdict.Output));
                        } else {
                            applied.TrySetResult();
                        }
                    });
                    // The mailbox runs only when posted to, so the finished reading posts its own submission.
                    _ = pending.Reading.ContinueWith(continuationAction: _ => Post(action: bookkeeping.Reloads.Poll), scheduler: TaskScheduler.Default);
                } catch (Exception error) { applied.TrySetException(exception: error); }
            });
            await applied.Task.WaitAsync(cancellationToken: cancellationToken);
            if (!await CheckpointNowCoreAsync(
                ct: cancellationToken,
                identity: identity
            )) { throw new IOException(message: "Reload applied but its checkpoint failed; release remains uncommitted."); }
            var written = await m_blobStore.WriteAsync(
                m_storageTarget,
                address,
                Encoding.UTF8.GetBytes(s: hash),
                ObjectBlobWriteMode.Overwrite,
                cancellationToken: cancellationToken
            );

            if (!written.Succeeded) { throw new IOException(message: "Could not commit the world release marker."); }
            m_pendingReleases.TryRemove(
                key: identity,
                value: out _
            );
            Console.WriteLine(value: $"[silo.reload: {identity.World} released {hash}]");
            return hash;
        } finally { m_reload.Release(); }
    }
}
