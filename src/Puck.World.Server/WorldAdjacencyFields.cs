using Puck.Maths;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The composition root's <see cref="IWorldAdjacencySource"/>: resolves an authored adjacency over
/// the same wire-shaped seam a picture-frame session screen already observes a destination through
/// (<see cref="WorldInstanceHost.TryResolveObservedDestination"/> then <see cref="WorldServer.AttachSink(Protocol.IClientSink)"/>) — data a
/// real network peer could equally have delivered, never a same-process shortcut into the neighbour's live server
/// objects. One instance-bound source is consumed by both that authority's contact resolution
/// (<see cref="WorldServer.Adjacencies"/>) and its boot-or-away render composition
/// (<c>WorldAdjacencySceneEmitter</c>), so a body's ground and what it sees cannot disagree.
/// </summary>
/// <remarks>One <see cref="Handle"/> per adjacency row, held for the life of this
/// instance — an observation lease, exactly like a session screen's own, never released until this type is disposed.
/// Each handle lazily (re)resolves the counterpart frame and (re)compiles a <see cref="WorldSolidField"/> over the
/// mirrored definition only when the mirror's own delivery revision moves, mirroring
/// <see cref="WorldServer"/>'s own revision-gated <c>SwapSolids</c>.</remarks>
public sealed class WorldAdjacencyFields : IWorldAdjacencySource, IDisposable {
    private readonly WorldInstanceHost m_instances;
    private readonly string m_sourceInstanceName;

    private bool m_tickProjectionsCurrent;

    private readonly Dictionary<string, Handle> m_handles = new(comparer: StringComparer.Ordinal);
    private WorldAdjacencyProjection[] m_tickProjections = [];

    /// <summary>Initializes the source.</summary>
    /// <param name="instances">The process's running world instances — the one observation door an adjacency's
    /// neighbour resolves through.</param>
    /// <param name="sourceInstanceName">The concrete source authority whose authored face keys this resolver.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instances"/> is <see langword="null"/>.</exception>
    public WorldAdjacencyFields(WorldInstanceHost instances, string sourceInstanceName) {
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourceInstanceName);

        m_instances = instances;
        m_sourceInstanceName = sourceInstanceName;
    }

    private WorldAdjacencyProjection[] BuildProjections() {
        if (
            !m_instances.TryGet(
            instance: out var source,
            name: m_sourceInstanceName
        ) ||
            (source is null)
        ) {
            return [];
        }

        var definition = source.Server.Definition;
        var rows = (definition.Adjacencies ?? []).Where(predicate: static row => (row is not null)).ToArray();
        var visuals = new List<WorldAdjacencyProjection>(capacity: (rows.Length + ((rows.Length * (rows.Length - 1)) / 2)));
        var direct = new Dictionary<string, IWorldAdjacencyNeighbour>(comparer: StringComparer.Ordinal);

        foreach (var row in rows) {
            if (
                TryResolve(
                adjacencyName: row!.Name.Value,
                neighbour: out var neighbour
            ) &&
                (neighbour is not null) &&
                WorldAdjacencyPolicy.TryDeriveOverlap(
                local: definition,
                neighbour: neighbour.Definition,
                depth: out var depth,
                reason: out _
            )
            ) {
                var frame = row.Boundary.CompileFrame();

                direct[row.Name.Value] = neighbour;
                visuals.Add(item: new WorldAdjacencyProjection(
                    Name: row.Name.Value,
                    Neighbour: neighbour,
                    Path: [new WorldAdjacencyFramePair(
                            Neighbour: neighbour.CounterpartFrame,
                            Source: frame,
                            OverlapDepth: depth,
                            OwnershipThreshold: OwnershipThreshold(
                                definition: definition,
                                frame: in frame
                            )
                        )],
                    OverlapDepth: depth,
                    Direct: true
                ));
            }
        }

        // A four-way corner has no diagonal ownership edge, but it DOES have a diagonal visibility/interest peer.
        // Derive it only when two different direct neighbours independently lead to the same next document. This
        // makes the topology itself the proof and prevents an arbitrary two-hop chain from widening interest.
        for (var leftIndex = 0; (leftIndex < rows.Length); leftIndex++) {
            var leftRow = rows[leftIndex]!;

            if (!direct.TryGetValue(
                key: leftRow.Name.Value,
                value: out var leftNeighbour
            )) { continue; }

            for (var rightIndex = (leftIndex + 1); (rightIndex < rows.Length); rightIndex++) {
                var rightRow = rows[rightIndex]!;

                if (
                    !direct.TryGetValue(
                    key: rightRow.Name.Value,
                    value: out var rightNeighbour
                ) ||
                    !WorldAdjacencyPolicy.TrySharedCorner(
                    left: WorldAdjacencyDocumentView.FromDefinition(definition: leftNeighbour.Definition),
                    leftBack: leftRow.Counterpart,
                    right: WorldAdjacencyDocumentView.FromDefinition(definition: rightNeighbour.Definition),
                    rightBack: rightRow.Counterpart,
                    document: out var cornerDocument,
                    leftEdge: out var leftEdge,
                    rightEdge: out _
                )
                ) {
                    continue;
                }

                var cornerDestination = WorldAdjacencyPolicy.GlobalDestinationForNeighbourKey(
                    definition: definition,
                    neighbourKey: cornerDocument
                );

                if (
                    (cornerDestination is null) ||
                    !TryResolveCorner(
                    source: source,
                    key: $"corner:{leftRow.Name.Value}+{rightRow.Name.Value}",
                    destinationName: cornerDestination,
                    counterpart: leftEdge.Counterpart,
                    intermediate: leftNeighbour,
                    intermediateEdge: leftEdge,
                    handle: out var corner
                ) ||
                    !WorldAdjacencyPolicy.TryDeriveOverlap(
                    local: leftNeighbour.Definition,
                    neighbour: corner!.Definition,
                    depth: out var cornerDepth,
                    reason: out _
                )
                ) {
                    continue;
                }

                // The second hop's source face is authored by the INTERMEDIATE document, so its ownership threshold
                // derives from that document's own envelope, never the local one.
                var intermediateFrame = leftEdge.Boundary.CompileFrame();
                var localFrame = leftRow.Boundary.CompileFrame();

                visuals.Add(item: new WorldAdjacencyProjection(
                    Name: $"corner:{leftRow.Name.Value}+{rightRow.Name.Value}",
                    Neighbour: corner,
                    Path: [
                        new WorldAdjacencyFramePair(
                            Neighbour: corner.CounterpartFrame,
                            Source: intermediateFrame,
                            OverlapDepth: cornerDepth,
                            OwnershipThreshold: OwnershipThreshold(
                                definition: leftNeighbour.Definition,
                                frame: in intermediateFrame
                            )
                        ),
                        new WorldAdjacencyFramePair(
                            Neighbour: leftNeighbour.CounterpartFrame,
                            Source: localFrame,
                            OverlapDepth: visuals.First(predicate: projection => (projection.Direct && string.Equals(
                                a: projection.Name,
                                b: leftRow.Name.Value,
                                comparisonType: StringComparison.Ordinal
                            ))).OverlapDepth,
                            OwnershipThreshold: OwnershipThreshold(
                                definition: definition,
                                frame: in localFrame
                            )
                        ),
                    ],
                    OverlapDepth: cornerDepth,
                    Direct: false
                ));
            }
        }

        return visuals.ToArray();
    }
    // The threshold the ownership scan hands a body over at, derived from the document that authors the face. A
    // document whose envelope does not derive contributes no expansion rather than an assumed one.
    private static FixedQ4816 OwnershipThreshold(WorldDefinition definition, in WorldFaceFrame frame) {
        if (
            !WorldAdjacencyPolicy.TryReciprocalHysteresis(
            definition: definition,
            depth: out var hysteresis,
            reason: out _
        ) ||
            !WorldAdjacencyPolicy.TryVerticalSettleDeadband(
            definition: definition,
            depth: out var settle,
            reason: out _
        )
        ) {
            return FixedQ4816.Zero;
        }

        return WorldAdjacencyPolicy.OwnershipThreshold(
            frame: in frame,
            reciprocalHysteresis: hysteresis,
            verticalSettleDeadband: settle
        );
    }
    private bool TryResolveCorner(WorldInstance source, string key, string destinationName, string counterpart, IWorldAdjacencyNeighbour intermediate, WorldAdjacencyEdgeView intermediateEdge, out IWorldAdjacencyNeighbour? handle) {
        handle = null;
        if (
            !m_instances.TryResolveObservedProjection(
            attach: out var attach,
            definition: out var observedDefinition,
            destinationName: destinationName,
            generationId: out var observedGenerationId,
            instanceName: out var observedInstanceName,
            reason: out _,
            source: source
        ) ||
            (observedDefinition is null) ||
            (attach is null)
        ) {
            return false;
        }

        var identity = new HandleIdentity(
            Destination: destinationName,
            InstanceName: observedInstanceName,
            GenerationId: observedGenerationId,
            Counterpart: counterpart,
            SourceFrame: intermediateEdge.Boundary.CompileFrame()
        );

        if (
            m_handles.TryGetValue(
            key: key,
            value: out var existing
        ) &&
            (existing.Identity != identity)
        ) {
            existing.Dispose();
            _ = m_handles.Remove(key: key);
            existing = null;
        }
        if (existing is null) {
            var mirror = new WorldSessionMirror(placeholder: observedDefinition);

            existing = new Handle(
                identity: identity,
                mirror: mirror,
                lease: attach(mirror),
                sourceDefinition: () => intermediate.Definition,
                sourceDescription: $"{m_sourceInstanceName}/{key}"
            );
            m_handles[key] = existing;
        }
        return existing.TryResolve(neighbour: out handle);
    }

    /// <inheritdoc/>
    public void BeginTick(ulong tick) {
        if (
            !m_instances.TryGet(
            instance: out var source,
            name: m_sourceInstanceName
        ) ||
            (source is null)
        ) {
            m_tickProjections = [];
            m_tickProjectionsCurrent = true;
            return;
        }

        // A derived corner handle only exists once the two-hop walk has found it, so discovery runs first and its
        // result is discarded: it resolved against handles not yet pinned for this tick. Pinning every handle the
        // walk left in the table and building again is what makes the frozen array's geometry and the pinned entity
        // image describe the same delivered revision. Contact may ask once per body; returning that frozen array
        // avoids rebuilding the two-hop graph for every body while keeping the render on the same image.
        _ = BuildProjections();

        foreach (var handle in m_handles.Values) {
            handle.Pin(sourceTick: tick);
        }

        m_tickProjections = BuildProjections();
        m_tickProjectionsCurrent = true;
    }
    /// <inheritdoc/>
    public void Dispose() {
        foreach (var handle in m_handles.Values) {
            handle.Dispose();
        }

        m_handles.Clear();
    }
    /// <inheritdoc/>
    public WorldBodyContactMode LocalBodyContact(int index) {
        if (
            !m_instances.TryGet(
            instance: out var source,
            name: m_sourceInstanceName
        ) ||
            (source is null)
        ) {
            return WorldBodyContactMode.Overlap;
        }

        return source.Server.Population.BodyContact(index: index);
    }
    /// <inheritdoc/>
    public Protocol.WorldEntityAddress LocalEntityAddress(int index) {
        if (
            !m_instances.TryGet(
            instance: out var source,
            name: m_sourceInstanceName
        ) ||
            (source is null)
        ) {
            return new Protocol.WorldEntityAddress(
                Authority: m_sourceInstanceName,
                Generation: 0,
                Index: index
            );
        }

        return new Protocol.WorldEntityAddress(
            Authority: source.Server.AuthorityIdentity,
            Index: index,
            Generation: source.Server.Population.Generation(index: index)
        );
    }
    /// <inheritdoc/>
    public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
        neighbour = null;

        if (
            !m_instances.TryGet(
            instance: out var source,
            name: m_sourceInstanceName
        ) ||
            (source is null)
        ) {
            return false;
        }

        var definition = source.Server.Definition;

        if (WorldDefinitionRows.FindAdjacency(
            adjacencies: definition.Adjacencies,
            name: adjacencyName
        ) is not { Boundary: { } boundary } adjacency) {
            return false;
        }

        if (
            !m_instances.TryResolveObservedProjection(
            source: source,
            destinationName: adjacency.Destination,
            instanceName: out var observedInstanceName,
            generationId: out var observedGenerationId,
            definition: out var observedDefinition,
            attach: out var attach,
            reason: out _
        ) ||
            (observedDefinition is null) ||
            (attach is null)
        ) {
            return false;
        }

        var identity = new HandleIdentity(
            Destination: adjacency.Destination,
            InstanceName: observedInstanceName,
            GenerationId: observedGenerationId,
            Counterpart: adjacency.Counterpart,
            SourceFrame: boundary.CompileFrame()
        );

        if (
            m_handles.TryGetValue(
            key: adjacencyName,
            value: out var handle
        ) &&
            (handle.Identity != identity)
        ) {
            handle.Dispose();
            _ = m_handles.Remove(key: adjacencyName);
            handle = null;
        }

        if (handle is null) {

            var mirror = new WorldSessionMirror(placeholder: observedDefinition);
            var lease = attach(mirror);

            handle = new Handle(
                identity: identity,
                mirror: mirror,
                lease: lease,
                sourceDefinition: () => source.Server.Definition,
                sourceDescription: $"{m_sourceInstanceName}/{adjacencyName}"
            );
            m_handles[adjacencyName] = handle;
        }

        return handle.TryResolve(neighbour: out neighbour);
    }
    /// <inheritdoc/>
    public IReadOnlyList<WorldAdjacencyProjection> Visuals() {
        if (m_tickProjectionsCurrent) {
            return m_tickProjections;
        }

        return BuildProjections();
    }

    // One adjacency's held observation: the mirror (kept live for the row's lifetime), the attach lease, and the
    // counterpart frame/solid field cache — refreshed only when the mirror's own delivery revision moves.
    private readonly record struct HandleIdentity(string Destination, string InstanceName, ulong GenerationId, string Counterpart, WorldFaceFrame SourceFrame);
    private sealed class Handle(HandleIdentity identity, WorldSessionMirror mirror, IDisposable lease, Func<WorldDefinition> sourceDefinition, string sourceDescription) : IWorldAdjacencyNeighbourContact, IDisposable {
        private readonly bool[] m_active = new bool[WorldBodiesLimits.CapacityCeiling];
        private readonly Protocol.WorldEntityAddress[] m_addresses = new Protocol.WorldEntityAddress[WorldBodiesLimits.CapacityCeiling];
        private readonly System.Numerics.Vector3[] m_previousPositions = new System.Numerics.Vector3[WorldBodiesLimits.CapacityCeiling];
        private readonly System.Numerics.Quaternion[] m_previousOrientations = new System.Numerics.Quaternion[WorldBodiesLimits.CapacityCeiling];
        private readonly System.Numerics.Vector3[] m_currentPositions = new System.Numerics.Vector3[WorldBodiesLimits.CapacityCeiling];
        private readonly System.Numerics.Quaternion[] m_currentOrientations = new System.Numerics.Quaternion[WorldBodiesLimits.CapacityCeiling];
        private readonly System.Numerics.Vector3[] m_colors = new System.Numerics.Vector3[WorldBodiesLimits.CapacityCeiling];
        private readonly WorldLook[] m_looks = new WorldLook[WorldBodiesLimits.CapacityCeiling];
        private readonly byte[] m_catalogRigs = new byte[WorldBodiesLimits.CapacityCeiling];
        private readonly FixedWorldCollider?[] m_colliders = new FixedWorldCollider?[WorldBodiesLimits.CapacityCeiling];
        private readonly WorldBodyContactMode[] m_bodyContacts = new WorldBodyContactMode[WorldBodiesLimits.CapacityCeiling];
        private int m_builtRevision = -1;
        private string m_fieldReason = string.Empty;
        private string m_pinnedFieldReason = string.Empty;

        public HandleIdentity Identity { get; } = identity;

        private WorldDefinition? m_builtSourceDefinition;
        private WorldSolidField? m_field;
        private WorldFaceFrame m_frame;
        private bool m_frameResolved;
        private bool m_hasPin;
        private long m_pinnedArrivalTimestamp;
        private WorldDefinition? m_pinnedDefinition;
        private WorldSolidField? m_pinnedField;
        private WorldFaceFrame m_pinnedFrame;
        private int m_pinnedSnapshotRevision;
        private ulong m_pinnedSnapshotTick;
        private float m_pinnedStepSeconds;

        public string Authority => mirror.Authority;
        public WorldFaceFrame CounterpartFrame => (m_hasPin
            ? m_pinnedFrame
            : m_frame
        );
        public WorldDefinition Definition => (m_pinnedDefinition ?? mirror.Definition);
        public int DefinitionRevision => mirror.DefinitionRevision;
        public int EntityCapacity => WorldBodiesLimits.CapacityCeiling;
        public float InterpolationAlpha => (m_hasPin
            ? WorldSessionMirror.ResolveInterpolationAlpha(
                arrivalTimestamp: m_pinnedArrivalTimestamp,
                stepSeconds: m_pinnedStepSeconds
            )
            : mirror.InterpolationAlpha
        );
        public int SnapshotRevision => (m_hasPin
            ? m_pinnedSnapshotRevision
            : mirror.SnapshotRevision
        );
        public ulong SnapshotTick => (m_hasPin
            ? m_pinnedSnapshotTick
            : mirror.Tick
        );

        // The compiled field is a function of BOTH documents: the mirrored one supplies the geometry, and the source
        // one fixes the overlap depth the selection is taken at. A document is swapped whole, so reference identity
        // is its revision.
        private void Refresh() {
            var source = sourceDefinition();

            if (
                (m_builtRevision != mirror.DefinitionRevision) ||
                !ReferenceEquals(
                objA: m_builtSourceDefinition,
                objB: source
            )
            ) {
                if (WorldDefinitionRows.FindAdjacency(
                    adjacencies: mirror.Definition.Adjacencies,
                    name: Identity.Counterpart
                ) is { Boundary: { } boundary }) {
                    m_frame = boundary.CompileFrame();
                    m_frameResolved = true;

                    var hasDepth = WorldAdjacencyPolicy.TryDeriveOverlap(
                        local: source,
                        neighbour: mirror.Definition,
                        depth: out var depth,
                        reason: out m_fieldReason
                    );
                    var selection = (hasDepth
                        ? WorldAdjacencyGeometry.Select(
                            definition: mirror.Definition,
                            frame: m_frame,
                            overlapDepth: depth
                        )
                        : new WorldAdjacencyGeometry.Selection(
                            Placements: [],
                            Truncated: false
                        )
                    );
                    var collisionDefinition = mirror.Definition with { PlacementRowsRaw = selection.Placements };

                    m_field = (WorldSolidField.TryBuild(
                        built: out var built,
                        definition: collisionDefinition,
                        reason: out m_fieldReason
                    )
                        ? built
                        : null
                    );

                    if (selection.Truncated) {
                        Console.Error.WriteLine(value: $"[world.adjacency: '{sourceDescription}' neighbour geometry truncated identically for collision and rendering at {WorldAdjacencyGeometry.MaximumPlacementsPerBand} solid placements]");
                    }
                } else {
                    m_frameResolved = false;
                    m_field = null;
                    m_fieldReason = "the authored counterpart frame no longer resolves";
                }
                m_builtRevision = mirror.DefinitionRevision;
                m_builtSourceDefinition = source;
            }

        }

        public System.Numerics.Vector3 BodyColor(int index) => (m_hasPin
            ? m_colors[index]
            : mirror.BodyColor(index: index)
        );
        public WorldBodyContactMode BodyContact(int index) => (m_hasPin
            ? m_bodyContacts[index]
            : mirror.BodyContact(index: index)
        );
        public byte CatalogRig(int index) => (m_hasPin
            ? m_catalogRigs[index]
            : mirror.CatalogRig(index: index)
        );
        public FixedWorldCollider? Collider(int index) => (m_hasPin
            ? m_colliders[index]
            : mirror.Collider(index: index)
        );
        public System.Numerics.Quaternion CurrentOrientation(int index) => (m_hasPin
            ? m_currentOrientations[index]
            : mirror.CurrentOrientation(index: index)
        );
        public System.Numerics.Vector3 CurrentPosition(int index) => (m_hasPin
            ? m_currentPositions[index]
            : mirror.CurrentPosition(index: index)
        );
        public void Dispose() => lease.Dispose();
        public Protocol.WorldEntityAddress EntityAddress(int index) => (m_hasPin
            ? m_addresses[index]
            : mirror.Address(index: index)
        );
        public bool IsEntityActive(int index) => (m_hasPin
            ? m_active[index]
            : mirror.IsActive(index: index)
        );
        public WorldLook Look(int index) => (m_hasPin
            ? m_looks[index]
            : mirror.Look(index: index)
        );
        public void Pin(ulong sourceTick) {
            Refresh();
            if (!m_frameResolved) {
                m_hasPin = false;
                return;
            }

            mirror.CopySnapshotTo(
                active: m_active,
                addresses: m_addresses,
                arrivalTimestamp: out m_pinnedArrivalTimestamp,
                bodyContacts: m_bodyContacts,
                catalogRigs: m_catalogRigs,
                colliders: m_colliders,
                colors: m_colors,
                currentOrientations: m_currentOrientations,
                currentPositions: m_currentPositions,
                looks: m_looks,
                previousOrientations: m_previousOrientations,
                previousPositions: m_previousPositions,
                revision: out m_pinnedSnapshotRevision,
                stepSeconds: out m_pinnedStepSeconds,
                tick: out m_pinnedSnapshotTick
            );
            m_pinnedDefinition = mirror.Definition;
            m_pinnedFrame = m_frame;
            m_pinnedField = m_field;
            m_pinnedFieldReason = m_fieldReason;
            m_hasPin = true;
            _ = sourceTick;
        }
        public System.Numerics.Quaternion PreviousOrientation(int index) => (m_hasPin
            ? m_previousOrientations[index]
            : mirror.PreviousOrientation(index: index)
        );
        public System.Numerics.Vector3 PreviousPosition(int index) => (m_hasPin
            ? m_previousPositions[index]
            : mirror.PreviousPosition(index: index)
        );
        /// <inheritdoc/>
        /// <remarks>The verdict is about the field this call hands out, never about a different one: a caller that
        /// trusts a true answer must receive a field, and a caller told false must be told which image refused.</remarks>
        public bool TryGetSolidField(out WorldSolidField? field, out string reason) {
            field = (m_hasPin
                ? m_pinnedField
                : m_field
            );
            reason = (m_hasPin
                ? m_pinnedFieldReason
                : m_fieldReason
            );

            return (field is not null);
        }
        public bool TryResolve(out IWorldAdjacencyNeighbour? neighbour) {
            neighbour = null;

            if (!m_hasPin) {
                Refresh();
            }

            if (!(m_hasPin || m_frameResolved)) {
                return false;
            }

            neighbour = this;

            return true;
        }

    }
}
