using System.Collections.Immutable;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Maths;
using Puck.Physics;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Resolves adjacency observations on the host and publishes their frozen projections.</summary>
/// <remarks>
/// The two halves run at different times. <see cref="ResolveNeighbours"/> and <see cref="MirrorsPrimed"/> ask the
/// host which authority each neighbour is and attach a mirror to it: they read another authority's live definition
/// and observation door, so the host calls them only at a boundary where no authority it steps holds a prepared or
/// executing tick. <see cref="Prepare"/> and the presentation publish run while a tick is being prepared, when another
/// authority of the same wave may already hold a prepared tick; they read only the mirrors a boundary attached, which
/// hold what each neighbour last delivered. The one place that enforces the split is <c>ResolveHandle</c>: outside a
/// boundary read it answers from the held handles and never reaches the host.
/// </remarks>
public sealed class WorldAdjacencyFields : IWorldAdjacencySource, IDisposable {
    private readonly WorldInstanceHost m_instances;
    private readonly string m_sourceInstanceName;
    // Whether the host's boundary is resolving: only then may a handle be looked up through the host and attached.
    private bool m_resolving;

    private readonly Dictionary<string, Handle> m_handles = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<(string Name, string Destination, string Counterpart), LinkAuthority> m_linkAuthorities = [];
    internal bool HasTickImage { get; private set; }
    private IReadOnlyList<WorldAdjacencyProjection> m_published = ImmutableArray<WorldAdjacencyProjection>.Empty;

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

    private ImmutableArray<WorldAdjacencyProjection> BuildProjections(WorldInstance source, WorldDefinition definition,
        ImmutableHashSet<Protocol.WorldEntityAddress> departed, bool pin) {
        var rows = definition.Adjacencies!;
        var visuals = ImmutableArray.CreateBuilder<WorldAdjacencyProjection>();
        var direct = new Dictionary<string, WorldAdjacencyProjection>(StringComparer.Ordinal);

        foreach (var row in rows) {
            if (row is not { Boundary: { } boundary }) { continue; }
            var frame = boundary.CompileFrame();
            if (Resolve(source, row.Name.Value, row.Destination, row.Counterpart, frame,
                definition, departed, pin) is not { } neighbour ||
                !WorldAdjacencyPolicy.TryDeriveOverlap(local: definition, neighbour: neighbour.Definition, depth: out var depth, reason: out _)) {
                continue;
            }

            depth = ProjectionOverlapDepth(adjacency: row, derived: depth);
            var projection = new WorldAdjacencyProjection(
                Name: row.Name.Value,
                Neighbour: neighbour,
                Path: ImmutableArray.Create(new WorldAdjacencyFramePair(
                    Neighbour: neighbour.CounterpartFrame,
                    Source: frame,
                    OverlapDepth: depth,
                    OwnershipThreshold: WorldAdjacencyPolicy.OwnershipFace(adjacency: row, definition: definition).Threshold
                )),
                OverlapDepth: depth,
                Direct: true
            );
            direct.Add(row.Name.Value, projection);
            visuals.Add(projection);
        }

        // A four-way corner has no diagonal ownership edge, but it DOES have a diagonal visibility/interest peer.
        // Derive it only when two different direct neighbours independently lead to the same next document. This
        // makes the topology itself the proof and prevents an arbitrary two-hop chain from widening interest.
        for (var leftIndex = 0; (leftIndex < rows.Count); leftIndex++) {
            var leftRow = rows[leftIndex]!;

            if (leftRow is null || !direct.TryGetValue(leftRow.Name.Value, out var left)) { continue; }
            var leftNeighbour = left.Neighbour;

            for (var rightIndex = (leftIndex + 1); (rightIndex < rows.Count); rightIndex++) {
                var rightRow = rows[rightIndex]!;

                if (
                    rightRow is null || !direct.TryGetValue(rightRow.Name.Value, out var right) ||
                    !WorldAdjacencyPolicy.TrySharedCorner(
                    left: WorldAdjacencyDocumentView.FromDefinition(definition: leftNeighbour.Definition),
                    leftBack: leftRow.Counterpart,
                    right: WorldAdjacencyDocumentView.FromDefinition(definition: right.Neighbour.Definition),
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
                    Resolve(source, $"corner:{leftRow.Name.Value}+{rightRow.Name.Value}", cornerDestination,
                    leftEdge.Counterpart, leftEdge.Boundary.CompileFrame(), leftNeighbour.Definition, departed, pin) is not { } corner ||
                    !WorldAdjacencyPolicy.TryDeriveOverlap(
                    local: leftNeighbour.Definition,
                    neighbour: corner.Definition,
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
                var intermediateRow = leftNeighbour.Definition.Adjacencies!.First(predicate: candidate => (candidate.Name == leftEdge.Name));

                cornerDepth = ProjectionOverlapDepth(adjacency: intermediateRow, derived: cornerDepth);

                visuals.Add(item: new WorldAdjacencyProjection(
                    Name: $"corner:{leftRow.Name.Value}+{rightRow.Name.Value}",
                    Neighbour: corner,
                    Path: ImmutableArray.Create(
                        new WorldAdjacencyFramePair(
                            Neighbour: corner.CounterpartFrame,
                            Source: intermediateFrame,
                            OverlapDepth: cornerDepth,
                            OwnershipThreshold: WorldAdjacencyPolicy.OwnershipFace(adjacency: intermediateRow, definition: leftNeighbour.Definition).Threshold
                        ),
                        new WorldAdjacencyFramePair(
                            Neighbour: leftNeighbour.CounterpartFrame,
                            Source: localFrame,
                            OverlapDepth: left.OverlapDepth,
                            OwnershipThreshold: WorldAdjacencyPolicy.OwnershipFace(adjacency: leftRow, definition: definition).Threshold
                        )
                    ),
                    OverlapDepth: cornerDepth,
                    Direct: false
                ));
            }
        }

        return visuals.ToImmutable();
    }
    private static FixedQ4816 ProjectionOverlapDepth(FixedQ4816 derived, WorldAdjacency adjacency) => FixedQ4816.Max(
        x: derived,
        y: FixedQ4816.FromDouble(value: adjacency.Hysteresis)
    );
    private PinnedNeighbour? Resolve(WorldInstance source, string key, string destination, string counterpart,
        WorldFaceFrame sourceFrame, WorldDefinition definition, ImmutableHashSet<Protocol.WorldEntityAddress> departed, bool pin) {
        var handle = ResolveHandle(source, key, destination, counterpart, sourceFrame);
        return (handle is null) ? null : handle.Read(definition, departed, pin);
    }

    private Handle? ResolveHandle(WorldInstance source, string key, string destination, string counterpart, WorldFaceFrame sourceFrame) {
        if (!m_resolving) {
            return m_handles.GetValueOrDefault(key: key);
        }
        if (!m_instances.TryResolveObservedProjection(source, destination, out var instanceName, out var generation,
            out var definition, out var attach, out _) || definition is null || attach is null) {
            return null;
        }

        var identity = new HandleIdentity(destination, instanceName, generation, counterpart, sourceFrame);
        if (m_handles.TryGetValue(key, out var handle)) {
            if (handle.Identity == identity) { return handle; }
            handle.Dispose();
            m_handles.Remove(key);
        }

        var mirror = new WorldSessionMirror(placeholder: definition);
        handle = new Handle(identity, LinkAuthorityFor(key, destination, counterpart), mirror, attach(mirror), m_instances.Narration, $"{m_sourceInstanceName}/{key}");
        m_handles.Add(key, handle);
        return handle;
    }

    /// <summary>Checks whether every reachable direct neighbour has delivered its first snapshot to a held row. It
    /// resolves each neighbour first, so it is a boundary read as <see cref="ResolveNeighbours"/> is: the host calls it
    /// for a row it holds back from stepping, while no authority it steps holds a tick.</summary>
    public bool MirrorsPrimed() {
        if (!m_instances.TryGet(instance: out var source, name: m_sourceInstanceName) || source is null) { return true; }

        m_resolving = true;
        try {
            foreach (var row in source.Server.Configuration.Adjacencies ?? []) {
                if (ResolveHandle(source, row.Name.Value, row.Destination, row.Counterpart, row.Boundary.CompileFrame()) is { } handle &&
                    handle.Read(source.Server.Configuration, ImmutableHashSet<Protocol.WorldEntityAddress>.Empty, pin: true) is { SnapshotRevision: < 1 }) {
                    return false;
                }
            }
        } finally {
            m_resolving = false;
        }
        return true;
    }

    /// <summary>Resolves which authority each adjacency's neighbour is, direct edges and the corners two of them share,
    /// and attaches a mirror to each one not yet held. The host calls it at a boundary where no authority it steps
    /// holds a prepared or executing tick: it reads the neighbours' live definitions and observation doors.</summary>
    public void ResolveNeighbours() {
        if (!m_instances.TryGet(instance: out var source, name: m_sourceInstanceName) || (source?.Server.Configuration is not { Adjacencies.Count: > 0 } definition)) {
            return;
        }

        m_resolving = true;
        try {
            // Discovery reads each held image as it stands, which keeps the resolver's generation order across live
            // edits; a corner first reachable through an image this pass attached is found at the next boundary.
            _ = BuildProjections(source, definition, ImmutableHashSet<Protocol.WorldEntityAddress>.Empty, pin: false);
        } finally {
            m_resolving = false;
        }
    }

    /// <summary>Closes the adjacency input at a host boundary and publishes the same record for presentation.</summary>
    public ImmutableArray<WorldAdjacencyProjection> Prepare() {
        HasTickImage = true;
        return Publish();
    }

    internal void PreparePresentation() => Publish();

    private ImmutableArray<WorldAdjacencyProjection> Publish() {
        var projections = ImmutableArray<WorldAdjacencyProjection>.Empty;
        if (m_instances.TryGet(instance: out var source, name: m_sourceInstanceName) && source?.Server.Configuration is { Adjacencies.Count: > 0 } definition) {
            var population = source.Server.Population;
            ImmutableHashSet<Protocol.WorldEntityAddress>.Builder? departed = null;
            for (var index = 0; index < population.Capacity; index++) {
                if (population.TryDepartedFrom(index, out var address)) {
                    (departed ??= ImmutableHashSet.CreateBuilder<Protocol.WorldEntityAddress>()).Add(address);
                }
            }

            // Samples the images of the neighbours a boundary resolved (ResolveNeighbours); nothing here reaches another
            // authority.
            projections = BuildProjections(source, definition,
                departed?.ToImmutable() ?? ImmutableHashSet<Protocol.WorldEntityAddress>.Empty, pin: true);
        }

        Volatile.Write(ref m_published, projections);
        return projections;
    }

    /// <inheritdoc/>
    public IReadOnlyList<WorldAdjacencyProjection> Visuals() => Volatile.Read(ref m_published);

    /// <inheritdoc/>
    public void Dispose() {
        foreach (var handle in m_handles.Values) {
            handle.Dispose();
        }
        m_handles.Clear();
        m_linkAuthorities.Clear();
    }

    private readonly record struct HandleIdentity(string Destination, string InstanceName, ulong GenerationId, string Counterpart, WorldFaceFrame SourceFrame);

    private LinkAuthority LinkAuthorityFor(string name, string destination, string counterpart) {
        var key = (name, destination, counterpart);
        if (!m_linkAuthorities.TryGetValue(key, out var authority)) {
            authority = new LinkAuthority();
            m_linkAuthorities.Add(key, authority);
        }
        return authority;
    }

    // Observation generations and transient resolution failures do not replace the authored link's identity.
    private sealed class LinkAuthority {
        public string Value { get; private set; } = string.Empty;
        public bool Settle(string delivered) {
            if ((Value.Length == 0) && (delivered.Length > 0)) { Value = delivered; }
            return Value.Length > 0;
        }
    }
    private sealed class Handle(HandleIdentity identity, LinkAuthority authority, WorldSessionMirror mirror, IDisposable lease, WorldOutputHub narrationHub, string sourceDescription) : IDisposable {
        public HandleIdentity Identity { get; } = identity;
        private WorldSessionMirror.BodyImage? m_image;
        private PinnedNeighbour? m_pinned;
        private WorldDefinition? m_builtSource;
        private WorldDefinition? m_builtDefinition;
        private WorldFaceFrame m_frame;
        private bool m_frameResolved;
        private WorldSolidField? m_field;
        private string m_fieldReason = string.Empty;
        // The link's entity journal across the images it pins (IWorldAdjacencyNeighbour.CollectEntityChanges), which the
        // host publishes as it pins and a presentation collects on its own thread, and the departed set the last pinned
        // image was read under.
        private readonly GpuSharedChangedRows m_entities = new(stableRows: true);
        private ImmutableHashSet<Protocol.WorldEntityAddress> m_departed = ImmutableHashSet<Protocol.WorldEntityAddress>.Empty;

        public PinnedNeighbour? Read(WorldDefinition source, ImmutableHashSet<Protocol.WorldEntityAddress> departed, bool pin) {
            if (!pin && m_pinned is { } previous) { return previous; }

            var held = m_image;
            var image = mirror.CaptureBodyImage(held);
            if (!ReferenceEquals(source, m_builtSource) || !ReferenceEquals(image.Definition, m_builtDefinition)) {
                Refresh(source, image.Definition);
                m_builtSource = source;
                m_builtDefinition = image.Definition;
            }

            var serial = m_entities.Serial;
            var departures = !departed.SetEquals(other: m_departed);

            if (!ReferenceEquals(image, held) || departures) {
                // An image captured over the one held names the slots it changed; any other, or a departure, which moves
                // an entity's activity outside its entry, owes every slot.
                var whole = (departures || (held is null) || image.Changed.IsDefault || (!image.Changed.IsEmpty && (image.Changed[^1] >= image.Entries.Length)));

                serial = m_entities.Publish(everything: whole, owed: (whole ? [] : image.Changed.AsSpan()), rows: image.Entries.Length);
            }
            m_departed = departed;

            m_image = image;
            m_pinned = (m_frameResolved && authority.Settle(image.Authority))
                ? new PinnedNeighbour(authority.Value, image, m_frame, m_field, m_fieldReason, departed, m_entities, serial)
                : null;
            return m_pinned;
        }

        private void Refresh(WorldDefinition source, WorldDefinition definition) {
            if (WorldDefinitionRows.FindAdjacency(
                adjacencies: definition.Adjacencies,
                name: Identity.Counterpart
            ) is { Boundary: { } boundary }) {
                m_frame = boundary.CompileFrame();
                m_frameResolved = true;

                var hasDepth = WorldAdjacencyPolicy.TryDeriveOverlap(
                    local: source,
                    neighbour: definition,
                    depth: out var depth,
                    reason: out m_fieldReason
                );
                var selection = (hasDepth
                    ? WorldAdjacencyGeometry.Select(
                        definition: definition,
                        frame: m_frame,
                        overlapDepth: depth
                    )
                    : new WorldAdjacencyGeometry.Selection(
                        Placements: [],
                        Truncated: false
                    )
                );
                var collisionDefinition = definition with { PlacementRowsRaw = selection.Placements };

                m_field = (WorldSolidField.TryBuild(
                    built: out var built,
                    definition: collisionDefinition,
                    reason: out m_fieldReason
                )
                    ? built
                    : null
                );

                if (selection.Truncated && narrationHub.HasNarrationSink) {
                    narrationHub.Narrate(
                        channel: "world.adjacency",
                        text: $"[world.adjacency: '{sourceDescription}' neighbour geometry truncated identically for collision and rendering at {WorldAdjacencyGeometry.MaximumPlacementsPerBand} solid placements]"
                    );
                }
            } else {
                m_frameResolved = false;
                m_field = null;
                m_fieldReason = "the authored counterpart frame no longer resolves";
            }
        }

        public void Dispose() => lease.Dispose();
    }

    private sealed class PinnedNeighbour(string authority, WorldSessionMirror.BodyImage image, WorldFaceFrame frame,
        WorldSolidField? solids, string fieldReason, ImmutableHashSet<Protocol.WorldEntityAddress> departed,
        GpuSharedChangedRows entities, long entitiesSerial) : IWorldAdjacencyNeighbourContact {
        private IReadOnlyList<int>? m_active;

        public IReadOnlyList<int> ActiveEntities => (m_active ??= (departed.IsEmpty
            ? image.Active
            : image.Active.Where(predicate: index => !departed.Contains(image.Entries.ItemRef(index).Address)).ToArray()));
        public bool CollectEntityChanges(GpuRowCursor cursor, GpuUploadRuns into) =>
            entities.Collect(cursor: cursor, into: into, rows: EntityCapacity, through: entitiesSerial);
        public string Authority => authority;
        public WorldDefinition Definition => image.Definition;
        public int DefinitionRevision => image.DefinitionRevision;
        public WorldFaceFrame CounterpartFrame => frame;
        public ulong SnapshotTick => image.Tick;
        public int SnapshotRevision => image.Revision;
        public float InterpolationAlpha => WorldSessionMirror.ResolveInterpolationAlpha(stepSeconds: image.StepSeconds, arrivalTimestamp: image.ArrivalTimestamp);
        public int EntityCapacity => image.Entries.Length;

        public bool IsEntityActive(int index) {
            ref readonly var entry = ref image.Entries.ItemRef(index);
            return entry.Active && (departed.IsEmpty || !departed.Contains(entry.Address));
        }
        public Protocol.WorldEntityAddress EntityAddress(int index) => image.Entries.ItemRef(index).Address;
        public int PoseEpoch(int index) => image.Entries.ItemRef(index).PoseEpoch;
        public string? PlacementId(int index) => image.Entries.ItemRef(index).PlacementId;
        public Vector3 PreviousPosition(int index) => image.Entries.ItemRef(index).PreviousPosition;
        public Quaternion PreviousOrientation(int index) => image.Entries.ItemRef(index).PreviousOrientation;
        public Vector3 CurrentPosition(int index) => image.Entries.ItemRef(index).CurrentPosition;
        public Quaternion CurrentOrientation(int index) => image.Entries.ItemRef(index).CurrentOrientation;
        public Vector3 BodyColor(int index) => image.Entries.ItemRef(index).Color;
        public WorldLook Look(int index) => image.Entries.ItemRef(index).Look;
        public byte CatalogRig(int index) => image.Entries.ItemRef(index).CatalogRig;
        public FixedWorldCollider? Collider(int index) => image.Entries.ItemRef(index).Collider;
        public WorldBodyContactMode BodyContact(int index) => image.Entries.ItemRef(index).Contact;
        public bool TryGetSolidField(out IContactField? field, out string reason) {
            field = solids;
            reason = fieldReason;
            return field is not null;
        }
    }
}
