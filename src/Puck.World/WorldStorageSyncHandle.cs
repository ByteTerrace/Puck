using Puck.Storage;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The wired owned-world sync engine, or the honest reason there is none. Wiring needs both halves: a store (the
/// provider-built target from <c>--storage-config-file</c>) and a resolved user identity (the container the platform
/// provisioned for the user). Either absence leaves the catalog local-only, and <see cref="Disposition"/> says which one declined — <c>storage.status</c> echoes it either
/// way. Also carries <see cref="Neighbours"/> — a second, read-only capability riding the same resolved target and
/// container: whatever wires push/pull also wires whether an adjacency cross-document proof can reach a
/// neighbour, since both need the identical (target, containerId) pair and neither exists without it.
/// </summary>
/// <remarks>
/// Wiring is a boot decision and stays one: both halves are boot inputs (a deployment file, a document field or its
/// CLI reflection), so neither can move mid-session, and the engine is never re-pointed at a second container in place. That is the
/// safe shape as well as the reachable one — the tracked cloud version tokens in <c>owned-worlds/sync-state.json</c>
/// are keyed by world id and scoped to one container, so re-pointing would carry a container's ETags into a container
/// that never issued them and turn the clobber guard into a coin flip. A different identity wants a fresh boot.
/// </remarks>
internal sealed class WorldStorageSyncHandle {
    /// <summary>Builds the handle from the effective settings: a store plus a resolved identity wires the engine;
    /// anything less leaves it unwired and reports why.</summary>
    /// <param name="settings">The effective storage settings.</param>
    /// <param name="identity">The identity resolver.</param>
    /// <param name="store">The blob store.</param>
    /// <param name="worlds">The owned-world catalog.</param>
    /// <returns>The handle.</returns>
    public static WorldStorageSyncHandle Create(WorldStorageSettings settings, IPlayerStorageIdentityResolver identity, IObjectBlobStore store, WorldOwnedWorlds worlds) {
        ArgumentNullException.ThrowIfNull(argument: settings);
        ArgumentNullException.ThrowIfNull(argument: identity);
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: worlds);

        if (settings.Target is not { } target) {
            return new WorldStorageSyncHandle(
                disposition: "cloud unwired — no store (local-only)",
                engine: null,
                neighbours: null
            );
        }
        if (!identity.TryResolve(
            containerId: out var containerId,
            reason: out var reason
        )) {
            return new WorldStorageSyncHandle(
                disposition: $"cloud unwired — {reason}",
                engine: null,
                neighbours: null
            );
        }

        return new WorldStorageSyncHandle(
            disposition: $"cloud wired — container {containerId:D} via {target.Description} (identity: {reason})",
            engine: new WorldOwnedWorldSync(
                containerId: containerId,
                stateFilePath: Path.Combine(
                    path1: worlds.FilePath,
                    path2: "sync-state.json"
                ),
                store: store,
                target: target,
                worlds: worlds
            ),
            neighbours: new WorldStorageNeighbourResolver(
                containerId: containerId,
                store: store,
                target: target
            )
        );
    }

    private WorldStorageSyncHandle(string disposition, WorldOwnedWorldSync? engine, IWorldNeighbourResolver? neighbours) {
        Disposition = disposition;
        Engine = engine;
        Neighbours = neighbours;
    }

    /// <summary>One line of truth about the wiring for <c>storage.status</c>.</summary>
    public string Disposition { get; }
    /// <summary>The sync engine, or <see langword="null"/> when the cloud is unwired.</summary>
    public WorldOwnedWorldSync? Engine { get; }
    /// <summary>The cloud-backed neighbour resolver an adjacency proof reads, or
    /// <see langword="null"/> when the cloud is unwired — the same condition <see cref="Engine"/> is null under, read
    /// only at document-load moments (see <c>WorldServer.Neighbours</c>' own remarks), never on a live per-mutation
    /// path.</summary>
    public IWorldNeighbourResolver? Neighbours { get; }
}
