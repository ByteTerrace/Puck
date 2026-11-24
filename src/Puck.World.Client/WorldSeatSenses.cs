using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Client;

/// <summary>
/// What a seat's client knows of the world its seat stands in, read only from what that world's authority delivered
/// through the seat's route (<see cref="WorldAuthorityRoute.Endpoint"/>): the body from the latest complete delivery,
/// the places from the delivered document's region placements, and paths from the navigation that document describes.
/// It is the same reading whether the authority runs in this process or another, since both deliver through one
/// mirror; nothing here reaches an authority's own objects, and this project cannot name one.
/// </summary>
/// <remarks>
/// <para>A place is a region placement of the delivered document. A body stands in it when the region's volume holds
/// the body's delivered foot point, the test the authority's occupancy latches make on the same document, here against
/// the single-precision position a delivery carries. A region attached to a body stands at that body's delivered
/// pose.</para>
/// <para>Every member runs on the thread that pumps and presents.</para>
/// </remarks>
/// <param name="routes">The seats' authority routes.</param>
/// <param name="client">The presentation client whose seat views hold the cameras' facings.</param>
/// <param name="navigation">Compiles the navigation one delivered document describes, given the navigation compiled
/// from the document's earlier revision or <see langword="null"/>.</param>
public sealed class WorldSeatSenses(WorldSeatAuthorityRouter routes, WorldClient client, Func<WorldDefinition, IWorldSeatNavigation?, IWorldSeatNavigation> navigation) {
    /// <summary>One seat's body this tick, as its client was delivered it.</summary>
    /// <param name="Endpoint">The authority the seat is routed to.</param>
    /// <param name="Index">The body's 0-based index in that authority.</param>
    /// <param name="Body">The body's latest complete delivery.</param>
    /// <param name="Position">The body's delivered foot point.</param>
    /// <param name="Places">The places the body stands in, innermost first.</param>
    /// <param name="Yaw">The seat camera's logical yaw, in radians.</param>
    /// <param name="YawPerTick">The yaw one tick of full look deflection turns the camera by, in radians.</param>
    public readonly record struct Reading(WorldAuthorityEndpoint Endpoint, int Index, WorldDeliveredBody Body, FixedVector3 Position, IReadOnlyList<string> Places, FixedQ4816 Yaw, FixedQ4816 YawPerTick) {
        /// <summary>Gets the innermost place the body stands in, or <c>none</c>.</summary>
        public string Place => ((Places.Count == 0) ? "none" : Places[0]);
        /// <summary>Gets the body's delivered planar speed, in metres per second.</summary>
        public FixedQ4816 PlanarSpeed {
            get {
                var velocity = Body.Velocity;

                return FixedQ4816.FromDouble(value: MathF.Sqrt(x: ((velocity.X * velocity.X) + (velocity.Z * velocity.Z))));
            }
        }
        /// <summary>Gets the identity of the world the seat stands in.</summary>
        public string World => Endpoint.Identity;
    }

    // What one endpoint's delivered document compiles to, kept until the mirror says its scene changed.
    private sealed class World {
        public WorldStateLease? Scale;
        public IWorldSeatNavigation? Navigation;
        public int NavigationRevision = -1;
        public WorldPlaces Places = WorldPlaces.None;
        public int PlacesRevision = -1;
    }

    private readonly Dictionary<WorldAuthorityEndpoint, World> m_worlds = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Gets the navigation the endpoint's delivered document describes, compiled when the document's scene
    /// last changed (<see cref="WorldSessionMirror.DefinitionRevision"/>), carrying over what stands from the
    /// compilation before.</summary>
    /// <param name="endpoint">The authority.</param>
    /// <returns>The navigation.</returns>
    public IWorldSeatNavigation Navigation(WorldAuthorityEndpoint endpoint) {
        var world = WorldOf(endpoint: endpoint);
        var revision = endpoint.Mirror.DefinitionRevision;

        if ((world.Navigation is null) || (world.NavigationRevision != revision)) {
            world.Navigation = navigation(arg1: endpoint.Definition, arg2: world.Navigation);
            world.NavigationRevision = revision;
        }

        return world.Navigation;
    }
    /// <summary>Lists the places of the endpoint's delivered document whose volume holds a point, innermost first.</summary>
    /// <param name="endpoint">The authority.</param>
    /// <param name="point">The world point.</param>
    /// <returns>The carrying placements' ids.</returns>
    public IReadOnlyList<string> PlacesAt(WorldAuthorityEndpoint endpoint, FixedVector3 point) =>
        PlacesOf(endpoint: endpoint).Holding(holds: (row, _) => (TryShape(endpoint: endpoint, row: in row, shape: out var shape) && shape.Contains(point: point)));
    /// <summary>Reads a seat's body from its route.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="reading">The reading, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the seat is routed to an authority whose latest delivery holds its body: a
    /// seat between authorities has no body to read until its new route's first delivery.</returns>
    public bool TryRead(int slot, out Reading reading) {
        reading = default;
        if (!TryBody(body: out var body, route: out var route, slot: slot) ||
            !client.TrySeatLook(slot: slot, yaw: out var yaw, yawPerTick: out var perTick)) {
            return false;
        }

        var position = FixedVector3.FromVector3(value: body.Position);

        reading = new Reading(
            Body: body,
            Endpoint: route.Endpoint,
            Index: route.EntityIndex,
            Places: PlacesAt(endpoint: route.Endpoint, point: position),
            Position: position,
            Yaw: FixedQ4816.FromDouble(value: yaw),
            YawPerTick: FixedQ4816.FromDouble(value: perTick)
        );
        return true;
    }
    /// <summary>Reads a seat's body from its route, without the places it stands in or its camera's look.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="body">The body's latest complete delivery, when this returns <see langword="true"/>.</param>
    /// <param name="route">The seat's route, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the seat is routed to an authority whose latest delivery holds its body.</returns>
    public bool TryBody(int slot, out WorldDeliveredBody body, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldAuthorityRoute? route) {
        body = default;
        route = routes.TryRoute(slot: slot);
        return ((route is not null) && route.Endpoint.Mirror.TryReadBody(body: out body, index: route.EntityIndex) && (body.Generation == route.Entity.Generation));
    }
    /// <summary>Gets a place's volume in the endpoint's delivered document.</summary>
    /// <param name="endpoint">The authority.</param>
    /// <param name="placementId">The placement carrying the region facet.</param>
    /// <param name="shape">The region's volume, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the delivered document declares the place and, for one attached to a body,
    /// the latest delivery holds that body.</returns>
    public bool TryPlaceShape(WorldAuthorityEndpoint endpoint, string placementId, out FixedSpatialShape shape) {
        foreach (ref readonly var row in PlacesOf(endpoint: endpoint).Rows) {
            if (string.Equals(a: row.Placement.Id, b: placementId, comparisonType: StringComparison.Ordinal)) {
                return TryShape(endpoint: endpoint, row: in row, shape: out shape);
            }
        }

        shape = default;
        return false;
    }
    /// <summary>Describes a read body as a path is planned for it: its delivered pose, and its kit's collider and step
    /// height in the delivered document at the scale the delivered state gives the body.</summary>
    /// <param name="reading">The reading.</param>
    /// <returns>The walker; one with no volumes when the delivered document gives the body's kit no collider.</returns>
    public WorldSeatWalker Walker(in Reading reading) {
        var definition = reading.Endpoint.Definition;
        var scale = FixedQ4816.FromDouble(value: Scale(reading: in reading));
        var step = ((reading.Body.Kit < definition.Kits.Count) ? FixedQ4816.FromDouble(value: definition.Kits[reading.Body.Kit].Motion.StepHeight) : FixedQ4816.Zero);
        var volumes = ((reading.Endpoint.Mirror.Collider(index: reading.Index) is { } collider) ? collider.Volumes.ToArray() : []);

        if (scale != FixedQ4816.One) {
            for (var index = 0; (index < volumes.Length); index++) {
                volumes[index] = volumes[index].Scaled(scale: scale);
            }
        }

        return new WorldSeatWalker(
            Orientation: FixedQuaternion.FromQuaternion(value: reading.Body.Orientation),
            Position: reading.Position,
            StepHeight: (step * scale),
            Volumes: volumes
        );
    }

    private WorldPlaces PlacesOf(WorldAuthorityEndpoint endpoint) {
        var world = WorldOf(endpoint: endpoint);
        var revision = endpoint.Mirror.DefinitionRevision;

        if (world.PlacesRevision != revision) {
            world.Places = WorldPlaces.Of(definition: endpoint.Definition);
            world.PlacesRevision = revision;
        }

        return world.Places;
    }
    // The body's live scale: the register the delivered document's bodies.scale names, read through the delivered state.
    private float Scale(in Reading reading) {
        var definition = reading.Endpoint.Definition;

        if (definition.Population.Scale is not { } register) {
            return 1f;
        }

        var reads = (WorldOf(endpoint: reading.Endpoint).Scale ??= new WorldStateLease());

        reads.Bind(bodyIndex: reading.Index, mirror: reading.Endpoint.FollowState());
        reads.Arrive(first: definition, second: null);
        return WorldGaitDrivers.LiveBodyScale(reads: reads, scaleRow: WorldBodyStateReads.BindingRow(slot: register));
    }
    // A place's volume: its static volume, or for a region attached to a body the volume at that body's delivered pose.
    private static bool TryShape(WorldAuthorityEndpoint endpoint, in WorldPlaces.Row row, out FixedSpatialShape shape) {
        if (row.Placement.Attach is not { } attach) {
            shape = row.StaticShape;
            return true;
        }
        if (!endpoint.Mirror.TryReadBody(body: out var carrier, index: attach.BodyIndex)) {
            shape = default;
            return false;
        }

        var orientation = FixedQuaternion.FromQuaternion(value: carrier.Orientation);
        // Facing is -Z at yaw zero, the convention FixedVector3.Yaw reads back.
        var forward = orientation.Rotate(vector: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: -FixedQ4816.One));

        shape = row.Region.ShapeAt(
            origin: (FixedVector3.FromVector3(value: carrier.Position) + orientation.Rotate(vector: FixedVector3.FromVector3(value: attach.LocalOffset))),
            yawRadians: (new FixedVector3(X: forward.X, Y: FixedQ4816.Zero, Z: forward.Z).Yaw() + FixedQ4816.FromDegrees(degrees: attach.LocalYawDegrees))
        );
        return true;
    }
    private World WorldOf(WorldAuthorityEndpoint endpoint) {
        if (!m_worlds.TryGetValue(key: endpoint, value: out var world)) {
            world = new World();
            m_worlds[endpoint] = world;
        }

        return world;
    }
}
