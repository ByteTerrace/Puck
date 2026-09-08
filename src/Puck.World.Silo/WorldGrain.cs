using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>A thin Orleans adapter over <see cref="WorldSiloHost"/> — activation lifecycle only. This class never
/// touches a tick, a body, a snapshot, or a transfer; every call delegates to the host's own mailbox-guarded
/// surface.</summary>
internal sealed class WorldGrain(WorldSiloHost host) : Grain, IWorldGrain {
    private WorldAuthorityIdentity Identity() {
        var owner = this.GetPrimaryKey(keyExt: out var worldExtension);

        if (!SafeName.TryParse(
            candidate: worldExtension,
            name: out var world,
            reason: out var reason
        )) {
            throw new InvalidOperationException(message: $"this grain's key extension '{worldExtension}' is not a valid world id — {reason}");
        }

        return new WorldAuthorityIdentity(
            Owner: owner,
            World: world
        );
    }

    /// <inheritdoc/>
    public Task<bool> ActivateAsync() {
        var identity = Identity();
        // Definition composition and neighbour reads precede the host's async mailbox work; keep them off
        // Orleans' cooperative scheduler. The host still admits the world on its own simulation thread.
        return Task.Run(() => host.ActivateAsync(ct: CancellationToken.None, identity: identity));
    }
    /// <inheritdoc/>
    public Task<bool> CheckpointNowAsync() => host.CheckpointNowAsync(
        ct: CancellationToken.None,
        identity: Identity()
    );
    /// <inheritdoc/>
    public Task DeactivateAsync() => host.DeactivateAsync(
        ct: CancellationToken.None,
        identity: Identity()
    );
    /// <inheritdoc/>
    public Task<WorldGrainStatus?> StatusAsync() => Task.FromResult(result: host.TryDescribeRow(worldId: Identity().World.Value));
}
