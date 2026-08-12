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
/// (<see cref="Client.WorldAdjacencySceneEmitter"/>), so a body's ground and what it sees cannot disagree.
/// </summary>
/// <remarks>One <see cref="Handle"/> per adjacency row, held for the life of this
/// instance — an observation lease, exactly like a session screen's own, never released until this type is disposed.
/// Each handle lazily (re)resolves the counterpart frame and (re)compiles a <see cref="WorldSolidField"/> over the
/// mirrored definition only when the mirror's own delivery revision moves, mirroring
/// <see cref="WorldServer"/>'s own revision-gated <c>SwapSolids</c>.</remarks>
internal sealed class WorldAdjacencyFields : IWorldAdjacencySource, IDisposable {
    private readonly WorldInstanceHost m_instances;
    private readonly string m_sourceInstanceName;
    private readonly Dictionary<string, Handle> m_handles = new(comparer: StringComparer.Ordinal);
    private WorldAdjacencyProjection[] m_tickProjections = [];
    private bool m_tickProjectionsCurrent;

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

    /// <inheritdoc/>
    public Protocol.WorldEntityAddress LocalEntityAddress(int index) {
        if (!m_instances.TryGet(name: m_sourceInstanceName, instance: out var source) || (source is null)) {
            return new Protocol.WorldEntityAddress(Authority: m_sourceInstanceName, Index: index, Generation: 0);
        }

        return new Protocol.WorldEntityAddress(Authority: source.Server.AuthorityIdentity, Index: index, Generation: source.Server.Population.Generation(index: index));
    }

    /// <inheritdoc/>
    public WorldBodyContactMode LocalBodyContact(int index) {
        if (!m_instances.TryGet(name: m_sourceInstanceName, instance: out var source) || (source is null)) {
            return WorldBodyContactMode.Overlap;
        }

        return source.Server.Population.BodyContact(index: index);
    }

    /// <inheritdoc/>
    public void BeginTick(ulong tick) {
        if (!m_instances.TryGet(name: m_sourceInstanceName, instance: out var source) || (source is null)) {
            m_tickProjections = [];
            m_tickProjectionsCurrent = true;
            return;
        }

        foreach (var row in source.Server.Definition.Adjacencies ?? []) {
            if ((row is not null) && TryResolve(adjacencyName: row.Name.Value, neighbour: out var neighbour) && (neighbour is Handle handle)) {
                handle.Pin(sourceTick: tick);
            }
        }

        // Resolve the topology once per authority tick, then pin any derived corner handles the direct-row walk did
        // not encounter. Contact may ask once per body; returning this frozen array avoids rebuilding the two-hop
        // graph (and allocating a list) for every body while keeping the render on the same delivered image.
        var projections = BuildProjections();
        foreach (var handle in projections.Select(selector: projection => projection.Neighbour).OfType<Handle>().Distinct()) {
            handle.Pin(sourceTick: tick);
        }
        m_tickProjections = BuildProjections();
        m_tickProjectionsCurrent = true;
    }

    /// <inheritdoc/>
    public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) {
        neighbour = null;

        if (!m_instances.TryGet(name: m_sourceInstanceName, instance: out var source) || (source is null)) {
            return false;
        }

        var definition = source.Server.Definition;

        if (WorldDefinitionRows.FindAdjacency(adjacencies: definition.Adjacencies, name: adjacencyName) is not { Boundary: { } boundary } adjacency) {
            return false;
        }

        if (!m_instances.TryResolveObservedProjection(source: source, destinationName: adjacency.Destination, instanceName: out var observedInstanceName, generationId: out var observedGenerationId, definition: out var observedDefinition, attach: out var attach, reason: out _) || (observedDefinition is null) || (attach is null)) {
            return false;
        }

        var identity = new HandleIdentity(Destination: adjacency.Destination, InstanceName: observedInstanceName, GenerationId: observedGenerationId, Counterpart: adjacency.Counterpart, SourceFrame: boundary.CompileFrame());

        if (m_handles.TryGetValue(key: adjacencyName, value: out var handle) && (handle.Identity != identity)) {
            handle.Dispose();
            _ = m_handles.Remove(key: adjacencyName);
            handle = null;
        }

        if (handle is null) {

            var mirror = new WorldSessionMirror(placeholder: observedDefinition);
            var lease = attach(mirror);

            handle = new Handle(identity: identity, mirror: mirror, lease: lease, sourceDefinition: () => source.Server.Definition, sourceDescription: $"{m_sourceInstanceName}/{adjacencyName}");
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

    private WorldAdjacencyProjection[] BuildProjections() {
        if (!m_instances.TryGet(name: m_sourceInstanceName, instance: out var source) || (source is null)) {
            return [];
        }

        var definition = source.Server.Definition;
        var rows = (definition.Adjacencies ?? []).Where(predicate: static row => row is not null).ToArray();
        var visuals = new List<WorldAdjacencyProjection>(capacity: (rows.Length + ((rows.Length * (rows.Length - 1)) / 2)));
        var direct = new Dictionary<string, IWorldAdjacencyNeighbour>(comparer: StringComparer.Ordinal);

        foreach (var row in rows) {
            if (TryResolve(adjacencyName: row!.Name.Value, neighbour: out var neighbour) && (neighbour is not null) &&
                WorldAdjacencyPolicy.TryDeriveOverlap(local: definition, neighbour: neighbour.Definition, depth: out var depth, reason: out _)) {
                direct[row.Name.Value] = neighbour;
                visuals.Add(item: new WorldAdjacencyProjection(
                    Name: row.Name.Value,
                    Neighbour: neighbour,
                    Path: [new WorldAdjacencyFramePair(Neighbour: neighbour.CounterpartFrame, Source: row.Boundary.CompileFrame(), OverlapDepth: depth)],
                    OverlapDepth: depth,
                    Direct: true));
            }
        }

        // A four-way corner has no diagonal ownership edge, but it DOES have a diagonal visibility/interest peer.
        // Derive it only when two different direct neighbours independently lead to the same next document. This
        // makes the topology itself the proof and prevents an arbitrary two-hop chain from widening interest.
        for (var leftIndex = 0; leftIndex < rows.Length; leftIndex++) {
            var leftRow = rows[leftIndex]!;
            if (!direct.TryGetValue(key: leftRow.Name.Value, value: out var leftNeighbour)) { continue; }

            for (var rightIndex = (leftIndex + 1); rightIndex < rows.Length; rightIndex++) {
                var rightRow = rows[rightIndex]!;
                if (!direct.TryGetValue(key: rightRow.Name.Value, value: out var rightNeighbour) ||
                    !WorldAdjacencyPolicy.TrySharedCorner(left: leftNeighbour.Definition, leftBack: leftRow.Counterpart, right: rightNeighbour.Definition, rightBack: rightRow.Counterpart, document: out var cornerDocument, leftEdge: out var leftEdge, rightEdge: out _)) {
                    continue;
                }

                var cornerDestination = WorldAdjacencyPolicy.GlobalDestinationForDocument(definition: definition, document: cornerDocument);
                if ((cornerDestination is null) || !TryResolveCorner(source: source, key: $"corner:{leftRow.Name.Value}+{rightRow.Name.Value}", destinationName: cornerDestination, counterpart: leftEdge!.Counterpart, intermediate: leftNeighbour, intermediateEdge: leftEdge, handle: out var corner) ||
                    !WorldAdjacencyPolicy.TryDeriveOverlap(local: leftNeighbour.Definition, neighbour: corner!.Definition, depth: out var cornerDepth, reason: out _)) {
                    continue;
                }

                visuals.Add(item: new WorldAdjacencyProjection(
                    Name: $"corner:{leftRow.Name.Value}+{rightRow.Name.Value}",
                    Neighbour: corner,
                    Path: [
                        new WorldAdjacencyFramePair(Neighbour: corner.CounterpartFrame, Source: leftEdge.Boundary.CompileFrame(), OverlapDepth: cornerDepth),
                        new WorldAdjacencyFramePair(Neighbour: leftNeighbour.CounterpartFrame, Source: leftRow.Boundary.CompileFrame(), OverlapDepth: visuals.First(projection => projection.Direct && string.Equals(projection.Name, leftRow.Name.Value, StringComparison.Ordinal)).OverlapDepth),
                    ],
                    OverlapDepth: cornerDepth,
                    Direct: false));
            }
        }

        return visuals.ToArray();
    }

    private bool TryResolveCorner(WorldInstance source, string key, string destinationName, string counterpart, IWorldAdjacencyNeighbour intermediate, WorldAdjacency intermediateEdge, out IWorldAdjacencyNeighbour? handle) {
        handle = null;
        if (!m_instances.TryResolveObservedProjection(source: source, destinationName: destinationName, instanceName: out var observedInstanceName, generationId: out var observedGenerationId, definition: out var observedDefinition, attach: out var attach, reason: out _) || (observedDefinition is null) || (attach is null)) {
            return false;
        }

        var identity = new HandleIdentity(Destination: destinationName, InstanceName: observedInstanceName, GenerationId: observedGenerationId, Counterpart: counterpart, SourceFrame: intermediateEdge.Boundary.CompileFrame());
        if (m_handles.TryGetValue(key: key, value: out var existing) && (existing.Identity != identity)) {
            existing.Dispose();
            _ = m_handles.Remove(key: key);
            existing = null;
        }
        if (existing is null) {
            var mirror = new WorldSessionMirror(placeholder: observedDefinition);
            existing = new Handle(identity: identity, mirror: mirror, lease: attach(mirror), sourceDefinition: () => intermediate.Definition, sourceDescription: $"{m_sourceInstanceName}/{key}");
            m_handles[key] = existing;
        }
        return existing.TryResolve(neighbour: out handle);
    }

    /// <inheritdoc/>
    public void Dispose() {
        foreach (var handle in m_handles.Values) {
            handle.Dispose();
        }

        m_handles.Clear();
    }

    // One adjacency's held observation: the mirror (kept live for the row's lifetime), the attach lease, and the
    // counterpart frame/solid field cache — refreshed only when the mirror's own delivery revision moves.
    private readonly record struct HandleIdentity(string Destination, string InstanceName, ulong GenerationId, string Counterpart, WorldFaceFrame SourceFrame);

    private sealed class Handle(HandleIdentity identity, WorldSessionMirror mirror, IDisposable lease, Func<WorldDefinition> sourceDefinition, string sourceDescription) : IWorldAdjacencyNeighbour, IDisposable {
        private readonly bool[] m_active = new bool[WorldAvatarCatalog.Capacity];
        private readonly Protocol.WorldEntityAddress[] m_addresses = new Protocol.WorldEntityAddress[WorldAvatarCatalog.Capacity];
        private readonly System.Numerics.Vector3[] m_previousPositions = new System.Numerics.Vector3[WorldAvatarCatalog.Capacity];
        private readonly System.Numerics.Quaternion[] m_previousOrientations = new System.Numerics.Quaternion[WorldAvatarCatalog.Capacity];
        private readonly System.Numerics.Vector3[] m_currentPositions = new System.Numerics.Vector3[WorldAvatarCatalog.Capacity];
        private readonly System.Numerics.Quaternion[] m_currentOrientations = new System.Numerics.Quaternion[WorldAvatarCatalog.Capacity];
        private readonly System.Numerics.Vector3[] m_colors = new System.Numerics.Vector3[WorldAvatarCatalog.Capacity];
        private readonly WorldLook[] m_looks = new WorldLook[WorldAvatarCatalog.Capacity];
        private readonly byte[] m_catalogRigs = new byte[WorldAvatarCatalog.Capacity];
        private readonly FixedWorldCollider?[] m_colliders = new FixedWorldCollider?[WorldAvatarCatalog.Capacity];
        private readonly WorldBodyContactMode[] m_bodyContacts = new WorldBodyContactMode[WorldAvatarCatalog.Capacity];
        private int m_builtRevision = -1;
        private WorldFaceFrame m_frame;
        private bool m_frameResolved;
        private WorldSolidField? m_field;
        private string m_fieldReason = string.Empty;
        private WorldDefinition? m_pinnedDefinition;
        private WorldFaceFrame m_pinnedFrame;
        private WorldSolidField? m_pinnedField;
        private string m_pinnedFieldReason = string.Empty;
        private ulong m_pinnedSnapshotTick;
        private int m_pinnedSnapshotRevision;
        private float m_pinnedStepSeconds;
        private long m_pinnedArrivalTimestamp;
        private bool m_hasPin;

        public HandleIdentity Identity { get; } = identity;

        public string Authority => mirror.Authority;

        public WorldDefinition Definition => (m_pinnedDefinition ?? mirror.Definition);

        public int DefinitionRevision => mirror.DefinitionRevision;

        public WorldFaceFrame CounterpartFrame => (m_hasPin ? m_pinnedFrame : m_frame);

        public ulong SnapshotTick => (m_hasPin ? m_pinnedSnapshotTick : mirror.Tick);

        public int SnapshotRevision => (m_hasPin ? m_pinnedSnapshotRevision : mirror.SnapshotRevision);

        public float InterpolationAlpha => (m_hasPin
            ? WorldSessionMirror.ResolveInterpolationAlpha(stepSeconds: m_pinnedStepSeconds, arrivalTimestamp: m_pinnedArrivalTimestamp)
            : mirror.InterpolationAlpha);

        public int EntityCapacity => WorldAvatarCatalog.Capacity;

        public bool IsEntityActive(int index) => (m_hasPin ? m_active[index] : mirror.IsActive(index: index));

        public Protocol.WorldEntityAddress EntityAddress(int index) => (m_hasPin ? m_addresses[index] : mirror.Address(index: index));

        public System.Numerics.Vector3 PreviousPosition(int index) => (m_hasPin ? m_previousPositions[index] : mirror.PreviousPosition(index: index));

        public System.Numerics.Quaternion PreviousOrientation(int index) => (m_hasPin ? m_previousOrientations[index] : mirror.PreviousOrientation(index: index));

        public System.Numerics.Vector3 CurrentPosition(int index) => (m_hasPin ? m_currentPositions[index] : mirror.CurrentPosition(index: index));

        public System.Numerics.Quaternion CurrentOrientation(int index) => (m_hasPin ? m_currentOrientations[index] : mirror.CurrentOrientation(index: index));

        public System.Numerics.Vector3 BodyColor(int index) => (m_hasPin ? m_colors[index] : mirror.BodyColor(index: index));

        public WorldLook Look(int index) => (m_hasPin ? m_looks[index] : mirror.Look(index: index));

        public byte CatalogRig(int index) => (m_hasPin ? m_catalogRigs[index] : mirror.CatalogRig(index: index));

        public FixedWorldCollider? Collider(int index) => (m_hasPin ? m_colliders[index] : mirror.Collider(index: index));

        public WorldBodyContactMode BodyContact(int index) => (m_hasPin ? m_bodyContacts[index] : mirror.BodyContact(index: index));

        public void Pin(ulong sourceTick) {
            Refresh();
            if (!m_frameResolved) {
                m_hasPin = false;
                return;
            }

            mirror.CopySnapshotTo(
                active: m_active,
                addresses: m_addresses,
                previousPositions: m_previousPositions,
                previousOrientations: m_previousOrientations,
                currentPositions: m_currentPositions,
                currentOrientations: m_currentOrientations,
                colors: m_colors,
                looks: m_looks,
                catalogRigs: m_catalogRigs,
                colliders: m_colliders,
                bodyContacts: m_bodyContacts,
                tick: out m_pinnedSnapshotTick,
                revision: out m_pinnedSnapshotRevision,
                stepSeconds: out m_pinnedStepSeconds,
                arrivalTimestamp: out m_pinnedArrivalTimestamp);
            m_pinnedDefinition = mirror.Definition;
            m_pinnedFrame = m_frame;
            m_pinnedField = m_field;
            m_pinnedFieldReason = m_fieldReason;
            m_hasPin = true;
            _ = sourceTick;
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

        private void Refresh() {

            if (m_builtRevision != mirror.DefinitionRevision) {
                if (WorldDefinitionRows.FindAdjacency(adjacencies: mirror.Definition.Adjacencies, name: Identity.Counterpart) is { Boundary: { } boundary }) {
                    m_frame = boundary.CompileFrame();
                    m_frameResolved = true;

                    var hasDepth = WorldAdjacencyPolicy.TryDeriveOverlap(local: sourceDefinition(), neighbour: mirror.Definition, depth: out var depth, reason: out m_fieldReason);
                    var selection = (hasDepth ? WorldAdjacencyGeometry.Select(definition: mirror.Definition, frame: m_frame, overlapDepth: depth) : new WorldAdjacencyGeometry.Selection(Placements: [], Truncated: false));
                    var collisionDefinition = mirror.Definition with { Placements = selection.Placements };

                    m_field = (WorldSolidField.TryBuild(definition: collisionDefinition, built: out var built, reason: out m_fieldReason) ? built : null);

                    if (selection.Truncated) {
                        Console.Error.WriteLine(value: $"[world.adjacency: '{sourceDescription}' neighbour geometry truncated identically for collision and rendering at {WorldAdjacencyGeometry.MaximumPlacementsPerBand} solid placements]");
                    }
                } else {
                    m_frameResolved = false;
                    m_field = null;
                    m_fieldReason = "the authored counterpart frame no longer resolves";
                }
                m_builtRevision = mirror.DefinitionRevision;
            }

        }

        public bool TryGetSolidField(out WorldSolidField? field, out string reason) {
            field = (m_hasPin ? m_pinnedField : m_field);
            reason = (m_hasPin ? m_pinnedFieldReason : m_fieldReason);

            return (m_field is not null);
        }

        public void Dispose() => lease.Dispose();

    }
}
