using Puck.Maths;
using Puck.Physics.Navigation;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>One body's cached route: the domain and goal cell a path was last found for, the path itself, and
    /// the search's last outcome and expansion count.</summary>
    internal sealed class BodyNavigationState {
        public int DomainIndex = -1;
        public int ExpandedLast;
        public int GoalCell = -1;
        public int PathLength;
        public int Waypoint;
        public int[] Path { get; private set; } = [];
        public NavigationStatus Status;

        public Span<int> WritablePath() {
            if (Path.Length == 0) {
                Path = new int[WorldNavigationCapacity.MaxPathNodes];
            }
            return Path;
        }

        public void Clear(NavigationStatus status = NavigationStatus.None) {
            DomainIndex = -1;
            ExpandedLast = 0;
            GoalCell = -1;
            PathLength = 0;
            Waypoint = 0;
            Status = status;
        }
    }

    // Bridges the server's own field-lattice representation to the kernel's narrow medium-field seam, so the
    // navigation runtime depends on neither the document schema nor the lattice's representation.
    private sealed class NavigationMediumFieldAdapter(WorldFieldLattice lattice) : INavigationMediumField {
        public ulong ValueRevision(int field) => lattice.ValueRevision(field: field);
        public bool IsInsideMedium(int field, in FixedVector3 position, FixedQ4816 clearance) =>
            lattice.IsInsideMedium(field: field, position: in position, clearance: clearance);
        public bool IsSegmentInsideMedium(int field, in FixedVector3 from, in FixedVector3 to, FixedQ4816 clearance, int maximumSubdivisions) =>
            lattice.IsSegmentInsideMedium(field: field, from: in from, to: in to, clearance: clearance, maximumSubdivisions: maximumSubdivisions);
        public bool TryFieldIndex(string name, out int field) => lattice.TryFieldIndex(name: name, field: out field);
    }

    // Compiles the authored navigation rows into the kernel's own plain input shape once at resolve time; the
    // kernel parses no document, so every authoring enum crosses the seam through an explicit mapping rather than
    // a numeric cast that would silently drift if either side's member order ever changed.
    private static NavigationDomainInput[] CompileNavigationDomains(IReadOnlyList<WorldNavigationDomain> rows) {
        var domains = new NavigationDomainInput[rows.Count];
        for (var index = 0; index < domains.Length; index++) {
            var row = rows[index];
            var compiled = FixedWorldNavigationDomain.Compile(domain: row);
            domains[index] = new NavigationDomainInput(
                Name: row.Name,
                Kind: MapNavigationKind(kind: compiled.Kind),
                Origin: compiled.Origin,
                CellSize: compiled.CellSize,
                Width: compiled.Width,
                Depth: compiled.Depth,
                Layers: compiled.Layers,
                Connectivity: MapNavigationConnectivity(connectivity: compiled.Connectivity),
                ProbeUp: compiled.ProbeUp,
                ProbeDown: compiled.ProbeDown,
                AgentRadius: compiled.AgentRadius,
                AgentHeight: compiled.AgentHeight,
                MaxStepHeight: compiled.MaxStepHeight,
                MaximumSlopeRise: compiled.MaximumSlopeRise,
                ArrivalDistance: compiled.ArrivalDistance,
                MaxExpandedNodes: compiled.MaxExpandedNodes,
                MaxPathNodes: compiled.MaxPathNodes,
                Medium: compiled.Medium,
                Shared: row.Shared is { } shared ? new NavigationSharing(GoalCapacity: shared.GoalCapacity, ExpandedNodesPerTick: shared.ExpandedNodesPerTick) : null
            );
        }
        return domains;
    }
    private static NavigationKind MapNavigationKind(WorldNavigationKind kind) => kind switch {
        WorldNavigationKind.Surface => NavigationKind.Surface,
        WorldNavigationKind.Volume => NavigationKind.Volume,
        WorldNavigationKind.Medium => NavigationKind.Medium,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(kind), actualValue: kind, message: null),
    };
    private static NavigationConnectivity MapNavigationConnectivity(WorldNavigationConnectivity connectivity) => connectivity switch {
        WorldNavigationConnectivity.Axis => NavigationConnectivity.Axis,
        WorldNavigationConnectivity.FacesAndEdges => NavigationConnectivity.FacesAndEdges,
        WorldNavigationConnectivity.Full => NavigationConnectivity.Full,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(connectivity), actualValue: connectivity, message: null),
    };
}
