using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>A thin Orleans adapter over <see cref="WorldSiloHost"/> — activation lifecycle only. This class never
/// touches a tick, a body, a snapshot, or a transfer; every call delegates to the host's own mailbox-guarded
/// surface.</summary>
internal sealed class WorldGrain(WorldSiloHost host) : Grain, IWorldGrain {
    private WorldAuthorityIdentity Identity() {
        var owner = this.GetPrimaryKey(keyExt: out var worldExtension);

        if (!WorldSafeName.TryParse(
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
    public Task<bool> ActivateAsync() => host.ActivateAsync(
        ct: CancellationToken.None,
        identity: Identity()
    );
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
