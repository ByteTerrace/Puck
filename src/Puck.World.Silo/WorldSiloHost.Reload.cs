using System.Text;
using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    private readonly SemaphoreSlim m_reload = new(initialCount: 1, maxCount: 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<WorldAuthorityIdentity, (string Hash, Task Applied)> m_pendingReleases = new();

    /// <summary>Applies changed published content through the ordinary reload submission and checkpoints it before recording its release.</summary>
    /// <param name="identity">The declared, active world to reconcile.</param>
    /// <param name="cancellationToken">Bounds storage reads, pump admission, and persistence.</param>
    /// <returns>The canonical content hash of the published definition accepted by this world.</returns>
    public Task<string> ReloadAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        lock (m_drainLock) {
            if (m_drainTask is not null) { throw new InvalidOperationException(message: "The silo is retiring."); }
            ObserveCompleted(operations: m_persistenceOperations);
            var operation = ReloadCoreAsync(cancellationToken: cancellationToken, identity: identity);

            m_persistenceOperations.Add(item: operation);
            return operation;
        }
    }

    private async Task<string> ReloadCoreAsync(WorldAuthorityIdentity identity, CancellationToken cancellationToken) {
        await m_reload.WaitAsync(cancellationToken: cancellationToken);
        try {
            if (FindWorldRow(identity: identity) is null) { throw new InvalidOperationException(message: "World is not declared in this silo."); }
            var origin = new WorldHostedOrigin(owner: identity.Owner, world: identity.World, store: m_blobStore, target: m_storageTarget);

            var (definition, reason) = await origin.LoadAsync(identity.World.Value, cancellationToken);
            if (definition is null) { throw new InvalidDataException(reason); }
            var hash = WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition));
            var address = WorldOwnedWorldSync.HostedAddressFor(identity.Owner, identity.World, "release");
            var previous = await m_blobStore.ReadAsync(address: address, cancellationToken: cancellationToken, target: m_storageTarget);

            if ((previous is { } released) && (Encoding.UTF8.GetString(bytes: released.Content.Span) == hash)) {
                m_pendingReleases.TryRemove(key: identity, value: out _);
                return hash;
            }

            if (m_pendingReleases.TryGetValue(key: identity, value: out var pending) && (pending.Hash != hash)) {
                throw new InvalidOperationException(message: "Complete the pending release before publishing another definition.");
            }
            if (!m_pendingReleases.ContainsKey(key: identity)) {
                var completion = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

                m_mailbox.Enqueue(item: () => {
                    if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken: cancellationToken); return; }
                    try {
                        if (IsDraining || !Instances.TryGet(identity.World.Value, out var row) || (row is null)) { throw new InvalidOperationException(message: "World is inactive or retiring."); }
                        if ((row.Server.Definition.Host.Authority != definition.Host.Authority) || (row.Server.Definition.Host.Listen != definition.Host.Listen)) {
                            throw new InvalidOperationException(message: "Changing a world's network identity requires worker replacement.");
                        }
                        // A crash can leave the rebuilt checkpoint durable before its release marker.
                        // Its authored definition already matches: retain recovered simulation state.
                        if (WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: row.Server.Definition)) == hash) {
                            m_pendingReleases[identity] = (hash, completion.Task);
                            completion.TrySetResult();
                            return;
                        }
                        long correlation = -1;

                        void Observe(WorldEditEcho echo) {
                            if ((echo.Kind != WorldEditEchoKind.Rebuild) || (echo.ConnectionId != SubmissionEnvelope.LocalConnectionId) || (echo.CorrelationId != correlation)) { return; }
                            row.Server.EchoTap -= Observe;
                            if (echo.Rejected) { m_pendingReleases.TryRemove(key: identity, value: out _); completion.TrySetException(exception: new InvalidOperationException(message: echo.Message)); } else { completion.TrySetResult(); }
                        }
                        row.Server.EchoTap += Observe;
                        try {
                            m_pendingReleases[identity] = (hash, completion.Task);
                            correlation = row.Link.SubmitEnvelope(payload: new WorldSubmissionPayload.Rebuild(Value: new(WorldRebuildKind.Reload, definition, origin.Identity, false, hash)), principal: WorldPrincipal.Console);
                            if (correlation == 0) { throw new InvalidOperationException(message: "Reload was refused by the transport."); }
                        } catch { m_pendingReleases.TryRemove(key: identity, value: out _); row.Server.EchoTap -= Observe; throw; }
                    } catch (Exception error) { completion.TrySetException(exception: error); }
                });
                await completion.Task.WaitAsync(cancellationToken: cancellationToken);
            } else {
                await pending.Applied.WaitAsync(cancellationToken: cancellationToken);
            }
            if (!await CheckpointNowCoreAsync(ct: cancellationToken, identity: identity)) { throw new IOException(message: "Reload applied but its checkpoint failed; release remains uncommitted."); }
            var written = await m_blobStore.WriteAsync(m_storageTarget, address, Encoding.UTF8.GetBytes(s: hash), ObjectBlobWriteMode.Overwrite, cancellationToken: cancellationToken);

            if (!written.Succeeded) { throw new IOException(message: "Could not commit the world release marker."); }
            m_pendingReleases.TryRemove(key: identity, value: out _);
            Console.WriteLine(value: $"[silo.reload: {identity.World} released {hash}]");
            return hash;
        } finally { m_reload.Release(); }
    }
}
