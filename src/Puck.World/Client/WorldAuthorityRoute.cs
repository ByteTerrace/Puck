using Puck.World.Protocol;
using Puck.World.Server;
using System.Numerics;

namespace Puck.World.Client;

/// <summary>
/// One reachable authority as capabilities rather than topology: the live definition it runs, the submission door,
/// the observation door, its adjacency field, and its next input coordinate. Local-process, federated, relayed, and
/// direct-player authorities all use this exact shape; placement is not part of a consumer's decision.
/// </summary>
internal sealed class WorldAuthorityEndpoint : IDisposable {
    private readonly Func<IWorldAdjacencySource?> m_adjacencies;
    private readonly WorldSessionMirror m_mirror;
    private readonly Func<ulong> m_nextInputTick;
    private readonly IDisposable m_observationLease;
    private readonly Func<IClientSink, IDisposable> m_observe;

    public WorldAuthorityEndpoint(
        string identity,
        Func<WorldDefinition> definition,
        IServerLink submissions,
        Func<IClientSink, IDisposable> observe,
        Func<IWorldAdjacencySource?> adjacencies,
        Func<ulong> nextInputTick,
        bool clockOwnedHere,
        WorldAuthorityRouteDescription? seed = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: identity);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: submissions);
        ArgumentNullException.ThrowIfNull(argument: observe);
        ArgumentNullException.ThrowIfNull(argument: adjacencies);
        ArgumentNullException.ThrowIfNull(argument: nextInputTick);

        Identity = identity;
        Submissions = submissions;
        m_observe = observe;
        m_adjacencies = adjacencies;
        m_nextInputTick = nextInputTick;
        ClockOwnedHere = clockOwnedHere;
        m_mirror = new WorldSessionMirror(placeholder: definition());
        if (seed is { } route) {
            m_mirror.SeedRoute(route: in route);
        }
        m_observationLease = observe(m_mirror);
    }

    /// <summary>The live adjacency source belonging to this endpoint, when reachable.</summary>
    public IWorldAdjacencySource? Adjacencies => m_adjacencies();
    /// <summary>The identity stamped on this authority's delivered entity addresses.</summary>
    public string Authority => m_mirror.Authority;
    /// <summary>Whether this process advances the endpoint's authoritative clock.</summary>
    public bool ClockOwnedHere { get; }
    /// <summary>The endpoint's current delivered definition.</summary>
    public WorldDefinition Definition => m_mirror.Definition;
    /// <summary>The stable runtime identity of this authority endpoint. It is never inferred from its transport.</summary>
    public string Identity { get; }
    /// <summary>The endpoint's next authoritative input coordinate.</summary>
    public ulong NextInputTick => m_nextInputTick();
    /// <summary>The endpoint's ordinary submission door.</summary>
    public IServerLink Submissions { get; }

    public void Dispose() => m_observationLease.Dispose();
    /// <summary>Attaches an ordinary observation sink.</summary>
    public IDisposable Observe(IClientSink sink) => m_observe(sink);
    /// <summary>Atomically seeds a newly discovered committed route before publishing its new seat epoch.</summary>
    public void SeedRoute(in WorldAuthorityRouteDescription route) => m_mirror.SeedRoute(route: in route);
    /// <summary>Reads the render identity belonging to the same complete entity claim as <see cref="TryEntityPose(in WorldEntityAddress, out Vector3, out Quaternion)"/>.</summary>
    public bool TryEntityAppearance(in WorldEntityAddress entity, out Vector3 bodyColor, out WorldLook look, out byte catalogRig) {
        if (
            (((uint)entity.Index) < WorldClient.EntityCapacity) &&
            m_mirror.IsActive(index: entity.Index) &&
            (m_mirror.Address(index: entity.Index) == entity)
        ) {
            bodyColor = m_mirror.BodyColor(index: entity.Index);
            look = m_mirror.Look(index: entity.Index);
            catalogRig = m_mirror.CatalogRig(index: entity.Index);
            return true;
        }

        bodyColor = default;
        look = WorldLook.Implicit;
        catalogRig = 0;
        return false;
    }
    /// <summary>Reads one entity directly in this authority's coordinate frame. Movement composition uses this
    /// instead of consulting the boot client's table, so a body-relative camera keeps the same semantics after an
    /// authority handoff.</summary>
    public bool TryEntityPose(int index, out Vector3 position, out Quaternion orientation) {
        if (
            (((uint)index) < WorldClient.EntityCapacity) &&
            m_mirror.IsActive(index: index)
        ) {
            position = m_mirror.CurrentPosition(index: index);
            orientation = m_mirror.CurrentOrientation(index: index);
            return true;
        }

        position = default;
        orientation = Quaternion.Identity;
        return false;
    }
    /// <summary>Reads a pose only when the complete generation-addressed identity is still the active occupant.</summary>
    public bool TryEntityPose(in WorldEntityAddress entity, out Vector3 position, out Quaternion orientation) {
        if (
            (((uint)entity.Index) < WorldClient.EntityCapacity) &&
            m_mirror.IsActive(index: entity.Index) &&
            (m_mirror.Address(index: entity.Index) == entity)
        ) {
            position = m_mirror.CurrentPosition(index: entity.Index);
            orientation = m_mirror.CurrentOrientation(index: entity.Index);
            return true;
        }

        position = default;
        orientation = Quaternion.Identity;
        return false;
    }
}
/// <summary>
/// One seat's immutable authority claim: endpoint, entity index within that authority, and monotonically increasing
/// epoch. <see cref="WorldSeatAuthorityRouter"/> publishes the whole value with one CAS, so every consumer observes
/// either the complete old claim or the complete new one.
/// </summary>
internal sealed record WorldAuthorityRoute(WorldAuthorityEndpoint Endpoint, WorldEntityAddress Entity, ulong Epoch) {
    public int EntityIndex => Entity.Index;
}
