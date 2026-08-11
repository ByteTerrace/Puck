using System.Numerics;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The read side of the seamless world continuum. It resolves a seat's CAS-published authority claim into the boot
/// presentation frame, whether the claimed entity is local, directly adjacent, or a compiler-derived corner peer.
/// Every seat-facing consumer uses this one mapping instead of inventing local/remote presentation rules.
/// </summary>
internal sealed class WorldContinuum(WorldClient client, WorldSeatAuthorityRouter routes, IWorldAdjacencySource adjacencies) {
    private readonly WorldClient m_client = client ?? throw new ArgumentNullException(paramName: nameof(client));
    private readonly WorldSeatAuthorityRouter m_routes = routes ?? throw new ArgumentNullException(paramName: nameof(routes));
    private readonly IWorldAdjacencySource m_adjacencies = adjacencies ?? throw new ArgumentNullException(paramName: nameof(adjacencies));

    public WorldAuthorityRoute Route(int slot) => m_routes.Route(slot: slot);

    /// <summary>Resolves the claimed entity's interpolated pose into the presentation frame.</summary>
    public bool TryResolveSeatPose(int slot, float interpolationAlpha, out Vector3 position, out Quaternion orientation) =>
        TryResolve(route: Route(slot: slot), interpolationAlpha: interpolationAlpha, position: out position, orientation: out orientation);

    /// <summary>Resolves any complete authority claim into the presentation frame.</summary>
    public bool TryResolve(WorldAuthorityRoute route, float interpolationAlpha, out Vector3 position, out Quaternion orientation) {
        ArgumentNullException.ThrowIfNull(argument: route);
        var authority = route.Endpoint.Authority;
        var entity = route.Entity;
        var entityIndex = entity.Index;

        if (authority.Length > 0) {
            if (string.Equals(a: authority, b: m_client.Authority, comparisonType: StringComparison.Ordinal)) {
                if (((uint)entityIndex < WorldClient.EntityCapacity) && m_client.IsActive(index: entityIndex) &&
                    (m_client.EntityAddress(index: entityIndex) == entity)) {
                    position = m_client.Position(index: entityIndex);
                    orientation = m_client.Orientation(index: entityIndex);
                    return true;
                }

                // A committed route seed may arrive before the boot client's next ordinary snapshot. It is already
                // expressed in the boot authority's frame, so no adjacency mapping is necessary.
                if (route.Endpoint.TryEntityPose(entity: in entity, position: out position, orientation: out orientation)) {
                    return true;
                }
            }

            var projections = m_adjacencies.Visuals();
            // Prefer the same pinned neighbour image that emits the visible avatar. Camera and body therefore use
            // one generation-addressed record rather than two independently arriving copies of the same authority.
            foreach (var projection in projections) {
                var neighbour = projection.Neighbour;
                if (((uint)entityIndex >= (uint)neighbour.EntityCapacity) || !neighbour.IsEntityActive(index: entityIndex) ||
                    (neighbour.EntityAddress(index: entityIndex) != entity)) {
                    continue;
                }

                var alpha = Math.Clamp(value: interpolationAlpha, min: 0f, max: 1f);
                var neighbourPosition = Vector3.Lerp(value1: neighbour.PreviousPosition(index: entityIndex), value2: neighbour.CurrentPosition(index: entityIndex), amount: alpha);
                var neighbourOrientation = Quaternion.Normalize(value: Quaternion.Lerp(quaternion1: neighbour.PreviousOrientation(index: entityIndex), quaternion2: neighbour.CurrentOrientation(index: entityIndex), amount: alpha));
                (position, orientation) = WorldAdjacencySceneEmitter.MapPoseIntoSource(position: neighbourPosition, orientation: neighbourOrientation, path: projection.Path);
                return true;
            }

            // Until that shared neighbour record catches up, the route endpoint carries the final writer's exact
            // commit-time pose. This closes the handoff interval without inventing or extrapolating state.
            if (route.Endpoint.TryEntityPose(entity: in entity, position: out var routedPosition, orientation: out var routedOrientation)) {
                // A directly connected authority running this same document already speaks the presentation frame;
                // requiring an adjacency path here would discard a valid live pose merely because no coordinate
                // conversion is necessary. Different documents still require the authored adjacency isometry below.
                if (string.Equals(a: route.Endpoint.Definition.DocumentId, b: m_client.Definition.DocumentId, comparisonType: StringComparison.Ordinal)) {
                    position = routedPosition;
                    orientation = routedOrientation;
                    return true;
                }
                foreach (var projection in projections) {
                    if (!string.Equals(a: projection.Neighbour.Authority, b: authority, comparisonType: StringComparison.Ordinal) ||
                        !string.Equals(a: projection.Neighbour.Definition.DocumentId, b: route.Endpoint.Definition.DocumentId, comparisonType: StringComparison.Ordinal)) {
                        continue;
                    }

                    (position, orientation) = WorldAdjacencySceneEmitter.MapPoseIntoSource(
                        position: routedPosition,
                        orientation: routedOrientation,
                        path: projection.Path);
                    return true;
                }
            }
        }

        position = default;
        orientation = Quaternion.Identity;
        return false;
    }
}
