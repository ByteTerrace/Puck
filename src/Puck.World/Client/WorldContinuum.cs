using System.Numerics;
using Puck.World.Protocol;
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
    private readonly WorldAuthorityRoute?[] m_selectedRoutes = new WorldAuthorityRoute?[WorldSeatBindings.SeatCount];
    private readonly string?[] m_selectedProjectionNames = new string?[WorldSeatBindings.SeatCount];
    private readonly Vector3[] m_lastPositions = new Vector3[WorldSeatBindings.SeatCount];
    private readonly bool[] m_hasLastPosition = new bool[WorldSeatBindings.SeatCount];

    /// <summary>Presentation rebuild watch for the complete local seat-route table.</summary>
    public int Revision => m_routes.Revision;

    public WorldAuthorityRoute Route(int slot) => m_routes.Route(slot: slot);

    /// <summary>Whether a locally followed seat owns the primary rendering of this exact traveler.</summary>
    public bool IsFollowed(in WorldEntityAddress entity) => m_routes.Claims(entity: in entity);

    /// <summary>Resolves the claimed entity's interpolated pose into the presentation frame.</summary>
    public bool TryResolveSeatPose(int slot, float interpolationAlpha, out Vector3 position, out Quaternion orientation) =>
        TryResolve(route: Route(slot: slot), interpolationAlpha: interpolationAlpha, seatSlot: slot, position: out position, orientation: out orientation);

    /// <summary>Resolves any complete authority claim into the presentation frame.</summary>
    public bool TryResolve(WorldAuthorityRoute route, float interpolationAlpha, out Vector3 position, out Quaternion orientation) {
        return TryResolve(route: route, interpolationAlpha: interpolationAlpha, seatSlot: null, position: out position, orientation: out orientation);
    }

    private bool TryResolve(WorldAuthorityRoute route, float interpolationAlpha, int? seatSlot, out Vector3 position, out Quaternion orientation) {
        ArgumentNullException.ThrowIfNull(argument: route);
        if (seatSlot is { } slot && !ReferenceEquals(objA: m_selectedRoutes[slot], objB: route)) {
            m_selectedRoutes[slot] = route;
            m_selectedProjectionNames[slot] = null;
        }
        var authority = route.Endpoint.Authority;
        var entity = route.Entity;
        var entityIndex = entity.Index;

        if (authority.Length > 0) {
            if (string.Equals(a: authority, b: m_client.Authority, comparisonType: StringComparison.Ordinal)) {
                if (((uint)entityIndex < WorldClient.EntityCapacity) && m_client.IsActive(index: entityIndex) &&
                    (m_client.EntityAddress(index: entityIndex) == entity)) {
                    position = m_client.Position(index: entityIndex);
                    orientation = m_client.Orientation(index: entityIndex);
                    Remember(seatSlot: seatSlot, position: position, projectionName: null);
                    return true;
                }

                // A committed route seed may arrive before the boot client's next ordinary snapshot. It is already
                // expressed in the boot authority's frame, so no adjacency mapping is necessary.
                if (route.Endpoint.TryEntityPose(entity: in entity, position: out position, orientation: out orientation)) {
                    Remember(seatSlot: seatSlot, position: position, projectionName: null);
                    return true;
                }
            }

            var projections = m_adjacencies.Visuals();
            // Prefer the same pinned neighbour image that emits the visible avatar. Camera and body therefore use
            // one generation-addressed record rather than two independently arriving copies of the same authority.
            var foundProjection = false;
            var bestProjectionName = string.Empty;
            var bestPosition = default(Vector3);
            var bestOrientation = Quaternion.Identity;
            var bestScore = float.PositiveInfinity;
            foreach (var projection in projections) {
                var neighbour = projection.Neighbour;
                if (((uint)entityIndex >= (uint)neighbour.EntityCapacity) || !neighbour.IsEntityActive(index: entityIndex) ||
                    (neighbour.EntityAddress(index: entityIndex) != entity)) {
                    continue;
                }

                // A remote mirror owns its snapshot arrival clock. Reusing the boot world's fixed-step fraction here
                // makes every asynchronously arriving neighbour snapshot jump backward before advancing again.
                var alpha = neighbour.InterpolationAlpha;
                var neighbourPosition = Vector3.Lerp(value1: neighbour.PreviousPosition(index: entityIndex), value2: neighbour.CurrentPosition(index: entityIndex), amount: alpha);
                var neighbourOrientation = Quaternion.Normalize(value: Quaternion.Lerp(quaternion1: neighbour.PreviousOrientation(index: entityIndex), quaternion2: neighbour.CurrentOrientation(index: entityIndex), amount: alpha));
                var mapped = WorldAdjacencySceneEmitter.MapPoseIntoSource(position: neighbourPosition, orientation: neighbourOrientation, path: projection.Path);
                var score = ProjectionScore(seatSlot: seatSlot, projectionName: projection.Name, position: mapped.Position);
                if (!foundProjection || (score < bestScore)) {
                    foundProjection = true;
                    bestScore = score;
                    bestProjectionName = projection.Name;
                    bestPosition = mapped.Position;
                    bestOrientation = mapped.Orientation;
                }
            }
            if (foundProjection) {
                position = bestPosition;
                orientation = bestOrientation;
                Remember(seatSlot: seatSlot, position: position, projectionName: bestProjectionName);
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
                    Remember(seatSlot: seatSlot, position: position, projectionName: null);
                    return true;
                }
                foundProjection = false;
                bestProjectionName = string.Empty;
                bestScore = float.PositiveInfinity;
                foreach (var projection in projections) {
                    if (!string.Equals(a: projection.Neighbour.Authority, b: authority, comparisonType: StringComparison.Ordinal) ||
                        !string.Equals(a: projection.Neighbour.Definition.DocumentId, b: route.Endpoint.Definition.DocumentId, comparisonType: StringComparison.Ordinal)) {
                        continue;
                    }

                    var mapped = WorldAdjacencySceneEmitter.MapPoseIntoSource(
                        position: routedPosition,
                        orientation: routedOrientation,
                        path: projection.Path);
                    var score = ProjectionScore(seatSlot: seatSlot, projectionName: projection.Name, position: mapped.Position);
                    if (!foundProjection || (score < bestScore)) {
                        foundProjection = true;
                        bestScore = score;
                        bestProjectionName = projection.Name;
                        bestPosition = mapped.Position;
                        bestOrientation = mapped.Orientation;
                    }
                }
                if (foundProjection) {
                    position = bestPosition;
                    orientation = bestOrientation;
                    Remember(seatSlot: seatSlot, position: position, projectionName: bestProjectionName);
                    return true;
                }

                // An authority no adjacency relates to the presented world is not a neighbour seen from here — it is
                // what this seat is presented in, since the frame source frames the seat with that same endpoint's
                // own views and document. There is no isometry to apply, and refusing the pose for lack of one
                // leaves the seat with no anchor for as long as it stays there.
                if (!ProjectionExists(projections: projections, authority: authority)) {
                    position = routedPosition;
                    orientation = routedOrientation;
                    Remember(seatSlot: seatSlot, position: position, projectionName: null);
                    return true;
                }
            }
        }

        position = default;
        orientation = Quaternion.Identity;
        return false;
    }

    private static bool ProjectionExists(IReadOnlyList<WorldAdjacencyProjection> projections, string authority) {
        foreach (var projection in projections) {
            if (string.Equals(a: projection.Neighbour.Authority, b: authority, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    private float ProjectionScore(int? seatSlot, string projectionName, Vector3 position) {
        if (seatSlot is not { } slot) {
            return 0f;
        }
        if (string.Equals(a: m_selectedProjectionNames[slot], b: projectionName, comparisonType: StringComparison.Ordinal)) {
            return -1f;
        }
        return (m_hasLastPosition[slot] ? Vector3.DistanceSquared(value1: m_lastPositions[slot], value2: position) : 0f);
    }

    private void Remember(int? seatSlot, Vector3 position, string? projectionName) {
        if (seatSlot is not { } slot) {
            return;
        }
        m_lastPositions[slot] = position;
        m_hasLastPosition[slot] = true;
        m_selectedProjectionNames[slot] = projectionName;
    }
}
