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

    // What one delivered document compiles to, kept by (authority, documentId) so a seat returning from a secondary
    // world or companion seats sharing one authority reuse its baked navigation and places without rebuilding them.
    private sealed class Document {
        public IWorldSeatNavigation? LastNavigation;
        public readonly WorldDocumentProduct<IWorldSeatNavigation> Navigation = new();
        public WorldDefinition? NavigationSource;
        public WorldPlaces LastPlaces = WorldPlaces.None;
        public readonly WorldDocumentProduct<WorldPlaces> Places = new();
        public WorldDefinition? PlacesSource;
    }

    private readonly Dictionary<(string Authority, string DocumentId), Document> m_documents = [];
    private readonly Dictionary<WorldAuthorityEndpoint, WorldStateLease> m_scales = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Gets the navigation the endpoint's delivered document describes, compiled when a part its build read
    /// changed (<see cref="WorldDocumentProduct{T}"/>), carrying over what stands from an earlier compilation of the
    /// same document.</summary>
    /// <param name="endpoint">The authority.</param>
    /// <returns>The navigation.</returns>
    public IWorldSeatNavigation Navigation(WorldAuthorityEndpoint endpoint) {
        var definition = endpoint.Definition;
        var documentId = (definition.DocumentId ?? endpoint.Identity);
        var document = DocumentOf(authority: endpoint.Authority, documentId: documentId);

        if ((document.LastNavigation is null) || !ReferenceEquals(objA: document.NavigationSource, objB: definition)) {
            var previous = (document.LastNavigation ?? ((definition.DocumentId is { Length: > 0 } named) ? AnyNavigationFor(documentId: named) : null));

            document.LastNavigation = document.Navigation.Of(
                document: definition,
                build: () => navigation(arg1: definition, arg2: previous)
            );
            document.NavigationSource = definition;
        }

        return document.LastNavigation;
    }
    /// <summary>Reports whether a seat's input source registry is admitted by its routed authority.</summary>
    public bool InputAdmitted(int slot) => client.IsSeatInputAdmitted(slot: slot);
    /// <summary>Reports whether a seat's current continuous move push has been answered by its routed authority.</summary>
    public bool MoveInputAnswered(int slot, ulong bodyTick) => client.IsSeatMoveAnswered(slot: slot, bodyTick: bodyTick);
    /// <summary>Reports whether a seat's latest move release to neutral has been answered by its routed authority.</summary>
    public bool MoveStoppedAnswered(int slot, ulong bodyTick) => client.IsSeatMoveStoppedAnswered(slot: slot, bodyTick: bodyTick);
    /// <summary>Reports whether a held press on a physical input source has been answered by its routed authority.</summary>
    public bool SourcePressAnswered(int slot, string source, ulong bodyTick) => client.IsSeatSourcePressAnswered(slot: slot, source: source, bodyTick: bodyTick);
    /// <summary>Reads the authority-tick round-trip control lag observed on a seat's input stream.</summary>
    public ulong MoveLagTicks(int slot, ulong bodyTick) => client.SeatMoveLagTicks(slot: slot, bodyTick: bodyTick);
    /// <summary>Reads the body's live scale in the delivered document's state.</summary>
    public FixedQ4816 BodyScale(in Reading reading) => FixedQ4816.FromDouble(value: Scale(reading: in reading));

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
            (!route.Endpoint.ClockOwnedHere && ReferenceEquals(objA: route.Endpoint.Definition, objB: WorldProjection.Undisclosed)) ||
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
        var definition = endpoint.Definition;
        var documentId = (definition.DocumentId ?? endpoint.Identity);
        var document = DocumentOf(authority: endpoint.Authority, documentId: documentId);

        if (!ReferenceEquals(objA: document.PlacesSource, objB: definition)) {
            document.LastPlaces = document.Places.Of(
                document: definition,
                build: () => WorldPlaces.Of(definition: definition)
            );
            document.PlacesSource = definition;
        }

        return document.LastPlaces;
    }
    // The body's live scale: the register the delivered document's bodies.scale names, read through the delivered state.
    private float Scale(in Reading reading) {
        var definition = reading.Endpoint.Definition;

        if (definition.Population.Scale is not { } register) {
            return 1f;
        }

        if (!m_scales.TryGetValue(key: reading.Endpoint, value: out var reads)) {
            reads = new WorldStateLease();
            m_scales[reading.Endpoint] = reads;
        }

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
    private Document DocumentOf(string authority, string documentId) {
        var key = (Authority: authority, DocumentId: documentId);

        if (!m_documents.TryGetValue(key: key, value: out var document)) {
            document = new Document();
            m_documents[key] = document;
        }

        return document;
    }
    private IWorldSeatNavigation? AnyNavigationFor(string documentId) {
        foreach (var (key, document) in m_documents) {
            if ((document.LastNavigation is not null) && string.Equals(a: key.DocumentId, b: documentId, comparisonType: StringComparison.Ordinal)) {
                return document.LastNavigation;
            }
        }

        return null;
    }
}
