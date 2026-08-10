using Puck.Storage;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The wired owned-world sync engine, or the honest reason there is none. Wiring needs both halves the storage
/// host-section authors: an endpoint (the world doc's storage-section <c>endpoint</c> or the <c>--storage-uri</c> reflection)
/// and a resolved user identity (the container the platform provisioned for the user). Either absence leaves the
/// catalog local-only, and <see cref="Disposition"/> says which one declined — <c>storage.status</c> echoes it either
/// way. Also carries <see cref="Neighbours"/> — a second, read-only capability riding the same resolved target and
/// container: whatever wires push/pull also wires whether a border-margin cross-document proof can reach a
/// neighbour, since both need the identical (target, containerId) pair and neither exists without it.
/// </summary>
/// <remarks>
/// Wiring is a boot decision and stays one: both halves are authored values (a document field or its CLI reflection),
/// so neither can move mid-session, and the engine is never re-pointed at a second container in place. That is the
/// safe shape as well as the reachable one — the tracked cloud version tokens in <c>owned-worlds/sync-state.json</c>
/// are keyed by world id and scoped to one container, so re-pointing would carry a container's ETags into a container
/// that never issued them and turn the clobber guard into a coin flip. A different identity wants a fresh boot.
/// </remarks>
internal sealed class WorldStorageSyncHandle {
    /// <summary>Builds the handle from the effective settings: endpoint plus resolved identity wires the engine;
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

        if (settings.Endpoint is not { Length: > 0 } endpoint) {
            return new WorldStorageSyncHandle(disposition: "cloud unwired — no endpoint (local-only)", engine: null, neighbours: null);
        }
        if (!identity.TryResolve(containerId: out var containerId, reason: out var reason)) {
            return new WorldStorageSyncHandle(disposition: $"cloud unwired — {reason}", engine: null, neighbours: null);
        }

        // A service URI is the platform edge (credentialed, the /private namespace); a connection string is a
        // dev/emulator account the caller administers (raw shape, containers self-managed). The edge cannot serve
        // container LIST at all, so an edge-shaped target additionally carries the authored discovery endpoint (see
        // WorldStorageSettings.DiscoveryEndpoint) as its DirectEndpoint — WorldOwnedWorldSync.DiscoverCloudIds
        // refuses by name, before any network call, when an edge-shaped target has none. Ignored on a raw-shaped
        // target: it already lists directly, like it reads and writes.
        var target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: endpoint);

        if (target.ServiceUri is not null) {
            target = (target with { EdgeNamespace = "private" });
        }
        if (settings.DiscoveryEndpoint is { Length: > 0 } discoveryEndpoint) {
            target = (target with { DirectEndpoint = discoveryEndpoint });
        }

        var discoveryDisposition = ((target.EdgeNamespace is null)
            ? "direct (raw account shape)"
            : ((target.DirectEndpoint is null) ? "REFUSES — no discovery endpoint authored" : "direct discovery endpoint"));

        return new WorldStorageSyncHandle(
            disposition: $"cloud wired — container {containerId:D} via {((target.EdgeNamespace is null) ? "connection string (raw account shape)" : "the platform edge")}, discovery {discoveryDisposition} (identity: {reason})",
            engine: new WorldOwnedWorldSync(
                containerId: containerId,
                stateFilePath: Path.Combine(path1: worlds.FilePath, path2: "sync-state.json"),
                store: store,
                target: target,
                worlds: worlds
            ),
            neighbours: new WorldStorageNeighbourResolver(store: store, target: target, containerId: containerId)
        );
    }

    private WorldStorageSyncHandle(string disposition, WorldOwnedWorldSync? engine, IWorldNeighbourResolver? neighbours) {
        Disposition = disposition;
        Engine = engine;
        Neighbours = neighbours;
    }

    /// <summary>The sync engine, or <see langword="null"/> when the cloud is unwired.</summary>
    public WorldOwnedWorldSync? Engine { get; }
    /// <summary>The cloud-backed neighbour resolver a mapped portal facet's border-margin proof reads, or
    /// <see langword="null"/> when the cloud is unwired — the same condition <see cref="Engine"/> is null under, read
    /// only at document-load moments (see <c>WorldServer.Neighbours</c>' own remarks), never on a live per-mutation
    /// path.</summary>
    public IWorldNeighbourResolver? Neighbours { get; }
    /// <summary>One line of truth about the wiring for <c>storage.status</c>.</summary>
    public string Disposition { get; }
}
