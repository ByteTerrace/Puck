using Puck.Abstractions.Collections;
using Puck.Hosting;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost {
    // The windows' graph by dense ids, which the frame's walks over the windows read instead of string-keyed sets
    // (CompleteFootprints). An edge is a screen footprint (its index in m_screenFootprints) or, after them, a filming
    // camera's view (ScreenCount plus its index in m_filmedViews), so edges keep the order the walks visit them in.
    //
    // Invariant: the topology describes the current screen footprints, filmed views and portals. The host replaces
    // m_screenFootprints with a new list whenever it changes any of the three (Reconcile, Install), and never mutates
    // a list once a frame has read it, so the topology is current exactly when it was derived from the same list, the same
    // portal version and as many filmed views. Door: Topology, the only read of the derived graph.
    private sealed class FootprintTopology {
        private readonly Dictionary<string, int> m_ids = new(comparer: StringComparer.Ordinal);
        private readonly int[] m_outStart;
        private readonly int[] m_out;

        public FootprintTopology(List<RenderGraphFootprint> screens, List<(WorldView Camera, WorldView Filmed)> filmed, HashSet<string> portals, int portalsVersion) {
            Screens = screens;
            PortalsVersion = portalsVersion;
            FilmedCount = filmed.Count;
            ScreenCount = screens.Count;

            var edges = (screens.Count + filmed.Count);
            var names = new List<string>();

            Consumer = new int[edges];
            Producer = new int[edges];
            for (var edge = 0; (edge < edges); edge++) {
                var (consumer, producer) = ((edge < screens.Count) ? (screens[edge].Consumer, screens[edge].Producer) : (filmed[(edge - screens.Count)].Camera.Name, filmed[(edge - screens.Count)].Filmed.Name));

                Consumer[edge] = Intern(name: consumer, names: names);
                Producer[edge] = Intern(name: producer, names: names);
            }

            Names = [.. names];
            Portal = new bool[Names.Length];
            for (var id = 0; (id < Names.Length); id++) { Portal[id] = portals.Contains(item: Names[id]); }

            // Each consumer's edges in edge order (a counting sort by consumer).
            m_outStart = new int[(Names.Length + 1)];
            m_out = new int[edges];
            foreach (var consumer in Consumer) { m_outStart[(consumer + 1)]++; }
            for (var id = 0; (id < Names.Length); id++) { m_outStart[(id + 1)] += m_outStart[id]; }

            var fill = m_outStart[..^1];

            for (var edge = 0; (edge < edges); edge++) { m_out[fill[Consumer[edge]]++] = edge; }

            // The session view whose image shows each view's glass: the first screen footprint, in order, that a portal
            // consumes it through; -1 when none does.
            ConsumingPortal = new int[Names.Length];
            Array.Fill(array: ConsumingPortal, value: -1);
            for (var edge = (screens.Count - 1); (edge >= 0); edge--) {
                if (Portal[Consumer[edge]]) { ConsumingPortal[Producer[edge]] = Consumer[edge]; }
            }
        }

        public List<RenderGraphFootprint> Screens { get; }
        public int PortalsVersion { get; }
        public int FilmedCount { get; }
        public int ScreenCount { get; }
        public int EdgeCount => Consumer.Length;
        // Each id's view name.
        public string[] Names { get; }
        // Each edge's consumer and producer ids.
        public int[] Consumer { get; }
        public int[] Producer { get; }
        // Whether each id is a session view (one that does not film its world).
        public bool[] Portal { get; }
        public int[] ConsumingPortal { get; }

        public int IdOf(string name) => (m_ids.TryGetValue(key: name, value: out var id) ? id : -1);
        public ReadOnlySpan<int> EdgesFrom(int id) => m_out.AsSpan(start: m_outStart[id], length: (m_outStart[(id + 1)] - m_outStart[id]));

        private int Intern(string name, List<string> names) {
            if (!m_ids.TryGetValue(key: name, value: out var id)) {
                id = names.Count;
                m_ids.Add(key: name, value: id);
                names.Add(item: name);
            }

            return id;
        }
    }

    private FootprintTopology? m_topology;

    private FootprintTopology Topology {
        get {
            if (
                (m_topology is not { } topology) ||
                !ReferenceEquals(objA: topology.Screens, objB: m_screenFootprints) ||
                (topology.PortalsVersion != m_portalsVersion) ||
                (topology.FilmedCount != m_filmedViews.Count)
            ) {
                topology = m_topology = new FootprintTopology(filmed: m_filmedViews, portals: m_portals, portalsVersion: m_portalsVersion, screens: m_screenFootprints);
                m_edgeSights = new EdgeSight[topology.EdgeCount];
            }

            return topology;
        }
    }

    // What a consumer sees of a producer's glass on one edge, read once a frame (FootprintFrame.PortalCoverage) and the
    // occlusion answer once a frame (PortalSightOf): the frame it was read on, the sight, the share of the consumer's
    // image the glass spans, the glass a tier view tints, and whether the glass was found occluded (0 not asked yet,
    // 1 open, 2 occluded).
    private struct EdgeSight {
        public long Frame;
        public PortalSight Sight;
        public double Width;
        public double Height;
        public int Glass;
        public byte Occlusion;
    }

    private EdgeSight[] m_edgeSights = [];
    private long m_footprintFrames;

    // Adds the edges out of every id of a frontier, in edge order, to a level's list: the order the walks visited the
    // footprints, then the filming cameras, in before they read the graph by ids.
    private static void LevelEdges(FootprintTopology topology, MarkedIndexSet frontier, List<int> level) {
        level.Clear();
        foreach (var id in frontier.Members) {
            foreach (var edge in topology.EdgesFrom(id: id)) { level.Add(item: edge); }
        }
        level.Sort();
    }
}
