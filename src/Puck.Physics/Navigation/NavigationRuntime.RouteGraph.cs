namespace Puck.Physics.Navigation;

public sealed partial class NavigationRuntime {
    // A domain's route graph as a goal field searches it, derived from its bake: each cell's forward steps (the neighbour
    // slots a route standing there may step to) and reverse steps (the slots of the neighbours that may step onto it), as
    // bit masks in neighbour order, with what a step's cost reads. One mask word a cell and a fixed table a slot replace
    // walking a cell's neighbours and testing each edge on every visit. Never written after it is built, so a derivation
    // reads it on any thread.
    internal sealed class RouteGraph {
        // The axes each neighbour slot's step moves along (one for a face, two for an edge, three for a corner).
        public static readonly int[] SlotAxes = BuildSlotAxes();

        public RouteGraph(uint[] forward, uint[] reverse, int[] slotDelta, byte[] clearance, int straight, bool[] walkable, ulong mediumRevision) {
            Forward = forward;
            Reverse = reverse;
            SlotDelta = slotDelta;
            Clearance = clearance;
            Walkable = walkable;
            MediumRevision = mediumRevision;
            m_straight = straight;
            m_axisLength = [0L, AxisLength(axes: 1), AxisLength(axes: 2), AxisLength(axes: 3)];

            long AxisLength(int axes) => checked((((long)straight) * Domain.StepCostOf(axes: axes)) / Domain.StraightStepCost);
        }

        private readonly int m_straight;
        private readonly long[] m_axisLength;

        public uint[] Forward { get; }
        public uint[] Reverse { get; }
        // The cell offset of each neighbour slot's step.
        public int[] SlotDelta { get; }
        // Each surface cell's clearance (Domain.RouteClearance); empty for a volume or medium domain.
        public byte[] Clearance { get; }
        // The bake and the live medium the graph was derived from.
        public bool[] Walkable { get; }
        public ulong MediumRevision { get; }

        // Writes, at costs[1..3], the cost of a step onto a cell along one, two or three axes: its length, the cell's
        // clearance surcharge, times the profile's multiplier for the cell's area. The caller has checked the area is not
        // excluded.
        public void StepCosts(int cell, byte area, NavigationCostProfile profile, Span<long> costs) {
            var surcharge = Surcharge(cell: cell);

            for (var axes = 1; (axes <= 3); axes++) {
                costs[axes] = profile.Scale(area: area, cost: checked(m_axisLength[axes] + surcharge));
            }
        }
        // The cost of one step onto a cell, or -1 when the profile excludes the cell's area.
        public long StepCost(int next, int axes, byte area, NavigationCostProfile profile) =>
            profile.Scale(area: area, cost: checked(m_axisLength[axes] + Surcharge(cell: next)));

        private long Surcharge(int cell) => ((Clearance.Length == 0) ? 0L : (((long)m_straight) * (Domain.RouteClearanceSteps - Clearance[cell])));

        private static int[] BuildSlotAxes() {
            var axes = new int[MaxRouteSteps];

            for (var slot = 0; (slot < MaxRouteSteps); slot++) {
                var (dx, dy, dz) = Domain.OffsetOfSlot(slot: ((byte)slot));

                axes[slot] = (((dx != 0) ? 1 : 0) + ((dy != 0) ? 1 : 0) + ((dz != 0) ? 1 : 0));
            }

            return axes;
        }
    }

    public sealed partial class Domain {
        private RouteGraph? m_routeGraph;

        internal const int StraightStepCost = StraightCost;
        internal const int RouteClearanceSteps = RouteClearanceCells;
        internal static int StepCostOf(int axes) => StepCost(axes: axes);
        internal static (int Dx, int Dy, int Dz) OffsetOfSlot(byte slot) => SlotOffset(slot: slot);

        // The cells' area codes a goal field's profile prices, derived on the thread that owns the runtime.
        internal byte[] GoalAreas() => AreaCodes();

        // The domain's route graph: every edge the route search's predicate admits (CanTraverse, over the steps the
        // domain's connectivity allows), kept while the bake and, for a medium domain, the live medium stand. Built on the
        // thread that owns the runtime, since a medium's edges read the live field.
        internal RouteGraph GoalGraph() {
            EnsureBaked();

            var medium = MediumValueRevision;

            if ((m_routeGraph is { } kept) && ReferenceEquals(objA: kept.Walkable, objB: m_walkable) && (kept.MediumRevision == medium)) {
                return kept;
            }

            var forward = new uint[CellCount];
            var reverse = new uint[CellCount];
            var slotDelta = new int[MaxRouteSteps];

            for (var slot = 0; (slot < MaxRouteSteps); slot++) {
                var (dx, dy, dz) = SlotOffset(slot: ((byte)slot));

                slotDelta[slot] = (((((dy * Tuning.Depth) + dz)) * Tuning.Width) + dx);
            }
            for (var node = 0; (node < CellCount); node++) {
                foreach (var step in Neighbors(node: node)) {
                    if (!CanTraverse(current: node, dx: step.Dx, dy: step.Dy, dz: step.Dz, next: step.Node, x: step.X, y: step.Y, z: step.Z)) {
                        continue;
                    }

                    var slot = NeighborSlot(dx: step.Dx, dy: step.Dy, dz: step.Dz);

                    forward[node] |= (1U << slot);
                    // The opposite step's slot mirrors this one's through the cell itself.
                    reverse[step.Node] |= (1U << ((MaxRouteSteps - 1) - slot));
                }
            }

            var graph = new RouteGraph(clearance: RouteClearance(), forward: forward, mediumRevision: medium, reverse: reverse, slotDelta: slotDelta,
                straight: Millimetres(value: Tuning.CellSize), walkable: m_walkable);

            m_routeGraph = graph;
            return graph;
        }
    }
}
