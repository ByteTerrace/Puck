using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>The checkpointed state of one net (<see cref="WorldNets"/>), by the placement that carries it.</summary>
/// <param name="Placement">The placement's id.</param>
/// <param name="State">The kernel's state.</param>
public sealed record WorldNetCheckpoint(string Placement, FixedNetState State);

/// <summary>
/// The authority's nets: one <see cref="FixedNet"/> for each placement carrying a net facet
/// (<see cref="WorldPlacementNet"/>), laid where the row's resolved frame stands it, with the bodies the latest step found
/// holding and leaning on it. The population steps them (<see cref="WorldPopulation.StepNets"/>) after its bodies, the
/// analytic contact provider poses each net's cell colliders from its nodes (<see cref="Panel.Colliders"/>), and a checkpoint
/// carries every node.
/// </summary>
/// <remarks>Invariant: a panel's colliders are a function of its net's nodes and its strand, cached by the net's
/// <see cref="FixedNet.Revision"/>, which every writer of a node moves; <see cref="Panel.Colliders"/> and
/// <see cref="Panel.HoldColliders"/> are the readers.</remarks>
public sealed class WorldNets {
    private readonly Panel[] m_panels;
    private readonly Dictionary<string, int> m_byId;

    /// <summary>One net and what the latest step read of the bodies about it.</summary>
    public sealed class Panel {
        private FixedStaticCollider[] m_colliders = [];
        private int m_collidersRevision = -1;
        private FixedStaticCollider[] m_holdColliders = [];
        private int m_holdCollidersRevision = -1;

        // Scale is the row's composed frame scale (WorldPlacementNet.TrySpecIn): its strand and its gravity take it, as a
        // body's size scales its gravity, so a scaled net falls and settles in the time the unscaled one does.
        internal Panel(WorldPlacement placement, WorldPlacementNet facet, FixedNet net, FixedQ4816 contactReach, FixedQ4816 scale) {
            Id = placement.Id;
            Facet = facet;
            Net = net;
            Strand = (FixedQ4816.FromDouble(value: facet.Strand) * scale);
            Gravity = new FixedVector3(X: FixedQ4816.Zero, Y: -(FixedQ4816.FromDouble(value: facet.Gravity) * scale), Z: FixedQ4816.Zero);
            Near = net.RestBox.Grow(margin: ((net.Spec.Extent + Strand) + contactReach));
            Opening = FixedQ4816.Max(x: FixedQ4816.Zero, y: net.Opening(strand: Strand));
        }

        /// <summary>Gets the placement's id.</summary>
        public string Id { get; }
        /// <summary>Gets the placement's net facet.</summary>
        public WorldPlacementNet Facet { get; }
        /// <summary>Gets the net.</summary>
        public FixedNet Net { get; }
        /// <summary>Gets the thickness the net collides at, in metres, the row's composed scale applied.</summary>
        public FixedQ4816 Strand { get; }
        /// <summary>Gets the widest a body may be and pass through the net's mesh at rest, in metres
        /// (<see cref="FixedNet.Opening"/>): a body that narrow meets the openings between the strands, and no hold
        /// takes the net for it.</summary>
        public FixedQ4816 Opening { get; }
        /// <summary>Gets the acceleration the net's free nodes fall at.</summary>
        public FixedVector3 Gravity { get; }
        /// <summary>Gets the box a body's foot point lies in, grown by the body's broadphase radius, whenever the body can
        /// touch the net: the rest layout grown by the reach, the strand and the contact band.</summary>
        public FixedAabb Near { get; }
        /// <summary>Gets how many bodies held the net at its latest step.</summary>
        public int Holders { get; internal set; }
        // How many bodies the step being gathered found holding the net; its loads hold a hand and a foot load each.
        internal int Holding { get; set; }
        /// <summary>Gets how many body volumes leaned on the net at its latest step.</summary>
        public int Pushers { get; internal set; }
        internal List<FixedNetLoad> Loads { get; } = [];
        /// <summary>Returns a point's place on the net's grid, whatever its height off the net, when it lies over the
        /// grid.</summary>
        /// <param name="point">The point.</param>
        /// <param name="column">The grid column, in node units.</param>
        /// <param name="row">The grid row, in node units.</param>
        /// <returns>Whether the point lies over the grid.</returns>
        public bool TryLocateOnGrid(in FixedVector3 point, out FixedQ4816 column, out FixedQ4816 row) =>
            (Net.TryGrid(column: out column, height: out _, point: in point, row: out row) && OnGrid(column: column, row: row));
        private bool OnGrid(FixedQ4816 column, FixedQ4816 row) => ((column >= FixedQ4816.Zero) && (row >= FixedQ4816.Zero) &&
            (column <= FixedQ4816.FromInteger(value: (Net.Spec.Columns - 1))) && (row <= FixedQ4816.FromInteger(value: (Net.Spec.Rows - 1))));
        internal List<FixedNetPusher> Pushing { get; } = [];

        /// <summary>Returns whether a point lies on the net: over its grid, and within its reach, its strand and the contact
        /// band of where the net stands there along its normal; and the point's place on the grid.</summary>
        /// <param name="point">The point: a hold's anchor, or a foot's support.</param>
        /// <param name="contactReach">The contact band, in metres (<c>FixedWorldCollision.ContactReach</c>).</param>
        /// <param name="column">The grid column, in node units.</param>
        /// <param name="row">The grid row, in node units.</param>
        /// <returns>Whether the point lies on the net.</returns>
        public bool TryLocate(in FixedVector3 point, FixedQ4816 contactReach, out FixedQ4816 column, out FixedQ4816 row) {
            if (!Net.TryGrid(column: out column, height: out var height, point: in point, row: out row) || !OnGrid(column: column, row: row)) { return false; }
            var surface = FixedVector3.Dot(left: Net.OffsetAt(column: column, row: row), right: Net.Normal);

            return (FixedQ4816.Abs(value: (height - surface)) <= ((Net.Spec.Reach + Strand) + contactReach));
        }

        /// <summary>Returns the colliders bodies meet as the net stands: its strands (<see cref="FixedNet.StrandColliders"/>),
        /// each rounded by half the strand, recomputed only when a node moved.</summary>
        /// <returns>The colliders, in strand order, borrowed until the net next moves.</returns>
        public ReadOnlySpan<FixedStaticCollider> Colliders() => Posed(cache: ref m_colliders, revision: ref m_collidersRevision, strands: true, written: ref m_strandCount);
        /// <summary>Returns the surface a hold takes as the net stands: a sheet over its cells (<see cref="FixedNet.CellColliders"/>),
        /// so a climber's hands find the net between its strands as on them, recomputed only when a node moved. Bodies
        /// do not meet it. While the net is parted it offers no hold (<see cref="FixedNet.CellColliders"/>), so a body running
        /// at its hole is never caught climbing the ropes splayed round it.</summary>
        /// <returns>The colliders, row by row, borrowed until the net next moves.</returns>
        public ReadOnlySpan<FixedStaticCollider> HoldColliders() => Posed(cache: ref m_holdColliders, revision: ref m_holdCollidersRevision, strands: false, written: ref m_holdCount);
        private int m_strandCount;
        private int m_holdCount;
        // One of the net's collider sets as it stands, rounded by half the strand, posed again only when a node moved.
        private ReadOnlySpan<FixedStaticCollider> Posed(ref FixedStaticCollider[] cache, ref int revision, bool strands, ref int written) {
            if (revision != Net.Revision) {
                var count = (strands ? Net.FreeStrandCount : Net.CellCount);
                var radius = (Strand / FixedQ4816.FromInteger(value: 2));

                if (cache.Length != count) { cache = new FixedStaticCollider[count]; }
                if (strands) {
                    Net.StrandColliders(destination: cache, radius: radius);
                    written = count;
                } else {
                    written = Net.CellColliders(destination: cache, radius: radius);
                }
                revision = Net.Revision;
            }
            return cache.AsSpan(start: 0, length: written);
        }
    }

    private WorldNets(Panel[] panels) {
        m_panels = panels;
        m_byId = new Dictionary<string, int>(capacity: panels.Length, comparer: StringComparer.Ordinal);
        Near = FixedAabb.Empty;
        for (var index = 0; (index < panels.Length); index++) {
            m_byId[panels[index].Id] = index;
            Near = Near.Enclose(other: panels[index].Near);
        }
    }

    /// <summary>Gets a set of no nets.</summary>
    public static WorldNets Empty { get; } = new(panels: []);

    /// <summary>Gets the nets, in document order.</summary>
    public ReadOnlySpan<Panel> Panels => m_panels;
    /// <summary>Gets the box every net's <see cref="Panel.Near"/> lies in: a body outside it, grown by its broadphase
    /// radius, touches no net.</summary>
    public FixedAabb Near { get; }
    /// <summary>Gets how many nets are awake.</summary>
    public int AwakeCount {
        get {
            var awake = 0;

            foreach (var panel in m_panels) { awake += (panel.Net.Awake ? 1 : 0); }
            return awake;
        }
    }
    /// <summary>Gets the nodes every net holds.</summary>
    public int NodeCount {
        get {
            var nodes = 0;

            foreach (var panel in m_panels) { nodes += panel.Net.NodeCount; }
            return nodes;
        }
    }

    /// <summary>Builds the nets a document's placements declare: a net whose placement and layout
    /// <paramref name="previous"/> held unchanged keeps its nodes, and any other starts at its rest layout, awake, so it
    /// settles into its sag before it first sleeps.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="previous">The nets before, or <see langword="null"/>.</param>
    /// <param name="contactReach">The contact band a body acts across (<c>FixedWorldCollision.ContactReach</c>), in
    /// metres, which each net's <see cref="Panel.Near"/> box reaches.</param>
    /// <returns>The nets.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldNets Build(WorldDefinition definition, WorldNets? previous, FixedQ4816 contactReach) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        List<Panel>? panels = null;

        foreach (var placement in definition.Placements) {
            if ((placement.Net is not { } facet) || !facet.TrySpecIn(definition: definition, placement: placement, spec: out var spec, scale: out var scale)) { continue; }

            var net = new FixedNet(spec: in spec);

            if ((previous is not null) && previous.TryPanel(id: placement.Id, panel: out var before) && (before.Net.Spec == spec) && (before.Facet == facet)) {
                net.Restore(state: before.Net.Capture());
            } else {
                // A new net starts awake, so it settles into its rest sag under gravity before it first sleeps.
                net.Wake();
            }
            (panels ??= []).Add(item: new Panel(contactReach: contactReach, facet: facet, net: net, placement: placement, scale: scale));
        }
        return ((panels is null) ? Empty : new WorldNets(panels: [.. panels]));
    }
    /// <summary>Finds the net a placement carries.</summary>
    /// <param name="id">The placement's id.</param>
    /// <param name="panel">The net, when found.</param>
    /// <returns>Whether the placement carries a net.</returns>
    public bool TryPanel(string id, out Panel panel) {
        if (m_byId.TryGetValue(key: id, value: out var index)) {
            panel = m_panels[index];
            return true;
        }
        panel = null!;
        return false;
    }
    /// <summary>Captures every net's state.</summary>
    /// <returns>One record per net, in document order.</returns>
    public WorldNetCheckpoint[] Capture() => [.. m_panels.Select(selector: static panel => new WorldNetCheckpoint(Placement: panel.Id, State: panel.Net.Capture()))];
    /// <summary>Checks a capture against these nets before anything is restored.</summary>
    /// <param name="checkpoints">The capture.</param>
    /// <exception cref="InvalidOperationException">The capture names other nets, or a net of another node count.</exception>
    public void Validate(IReadOnlyList<WorldNetCheckpoint> checkpoints) {
        ArgumentNullException.ThrowIfNull(argument: checkpoints);
        if (checkpoints.Count != m_panels.Length) { throw new InvalidOperationException(message: "Checkpoint net inventory differs from its document."); }
        for (var index = 0; (index < checkpoints.Count); index++) {
            if (!string.Equals(a: checkpoints[index].Placement, b: m_panels[index].Id, comparisonType: StringComparison.Ordinal) ||
                (checkpoints[index].State.Offsets.Length != m_panels[index].Net.NodeCount)) {
                throw new InvalidOperationException(message: $"Checkpoint net '{checkpoints[index].Placement}' differs from its document's.");
            }
        }
    }
    /// <summary>Restores every net from a capture <see cref="Validate"/> admitted.</summary>
    /// <param name="checkpoints">The capture.</param>
    public void Restore(IReadOnlyList<WorldNetCheckpoint> checkpoints) {
        Validate(checkpoints: checkpoints);
        for (var index = 0; (index < checkpoints.Count); index++) { m_panels[index].Net.Restore(state: checkpoints[index].State); }
    }
    /// <summary>Folds every net's deterministic state into a hash, each after its placement's id.</summary>
    /// <param name="hash">The hash.</param>
    public void AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(value: m_panels.Length);
        foreach (var panel in m_panels) {
            hash.Add(value: panel.Id.Length);
            hash.Add(value: panel.Id);
            panel.Net.AppendStateHash(hash: ref hash);
        }
    }
    /// <summary>Describes the nets for <c>world.nets</c>: each net's nodes, whether it is awake, its deepest deflection,
    /// its holders and the volumes leaning on it at its latest step.</summary>
    /// <returns>One line per net, or a none line.</returns>
    public string Describe() {
        if (m_panels.Length == 0) { return "[world.nets: none]"; }

        var builder = new System.Text.StringBuilder();

        builder.Append(handler: $"[world.nets: {m_panels.Length} nets, {AwakeCount} awake, {NodeCount} nodes]");
        foreach (var panel in m_panels) {
            var net = panel.Net;

            builder.Append(value: '\n');
            builder.Append(provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"  {panel.Id}: nodes={net.Spec.Columns}x{net.Spec.Rows} awake={(net.Awake ? 1 : 0)} deflection={((double)net.MaxDeflection):0.0000} reach={((double)net.Spec.Reach):0.000} openness={((double)net.Openness):0.000} opens={(net.Opens ? 1 : 0)} driven={net.DrivenCount} holders={panel.Holders} pushers={panel.Pushers} revision={net.Revision}");
        }
        return builder.ToString();
    }
}
