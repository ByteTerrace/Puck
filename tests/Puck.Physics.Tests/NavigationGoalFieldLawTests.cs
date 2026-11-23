using Puck.Maths;
using Puck.Physics.Navigation;

namespace Puck.Physics.Tests;

/// <summary>Pins goal fields: a field leads every cell that can reach its goal there across the domains of one
/// network, never into another network, gives the same steps whichever fields are resident, and lets many bodies share
/// a few goals at a per-tick cost of one step read per body. The crowd law reports the work a town of bodies spends
/// with goal fields beside the work a route search per body spends.</summary>
public sealed class NavigationGoalFieldLawTests(ITestOutputHelper output) {
    private const int DistrictCells = 96;
    private const double CellSize = 0.5;
    private const int GoalCount = 200;

    private static readonly FixedStaticContactSolver Solver = new(ContactSkin: Q(x: 0.02), GroundedThreshold: Q(x: 0.5), MaxIterations: 4);

    private static FixedQ4816 Q(double x) => FixedQ4816.FromDouble(value: x);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(x: x), Y: Q(x: y), Z: Q(x: z));
    private static NavigationCapacity Capacity(long goalFieldBytes = (64L << 20)) => new(MaxMediumSegmentSubdivisions: 32, GoalFieldBytes: goalFieldBytes);
    // The side of one district in metres; neighbouring districts share their boundary column or row of cells.
    private static double DistrictSide => ((DistrictCells - 1) * CellSize);
    private static NavigationDomainInput District(string name, double x, double z, string? network = null, int cells = DistrictCells) => new(
        Name: name,
        Kind: NavigationKind.Surface,
        Origin: V(x: x, y: 0.05, z: z),
        CellSize: Q(x: CellSize),
        Width: cells,
        Depth: cells,
        Layers: 1,
        Connectivity: NavigationConnectivity.Full,
        ProbeUp: Q(x: 2),
        ProbeDown: Q(x: 1),
        AgentRadius: Q(x: 0.2),
        AgentHeight: Q(x: 1.8),
        MaxStepHeight: Q(x: 0.3),
        MaximumSlopeRise: Q(x: 0.3),
        ArrivalDistance: Q(x: CellSize),
        MaxExpandedNodes: (cells * cells),
        MaxPathNodes: 1024,
        Medium: null,
        Shared: true,
        Network: network
    );
    // A town of four districts in a square on one floor, each crossed by two walls with a gap at one end, and a road
    // domain of another network laid over the first district.
    private static NavigationRuntime Town(long goalFieldBytes = (64L << 20), NavigationSearchWork? work = null) {
        var side = DistrictSide;
        var colliders = new List<FixedStaticCollider> {
            FixedStaticCollider.AxisAlignedBox(center: V(x: side, y: -0.5, z: side), halfExtents: V(x: (side + 2), y: 0.5, z: (side + 2))),
        };

        for (var i = 0; (i < 2); i++) {
            for (var j = 0; (j < 2); j++) {
                var x = (i * side);
                var z = (j * side);

                colliders.Add(item: FixedStaticCollider.AxisAlignedBox(center: V(x: (x + (side * 0.33)), y: 1.5, z: (z + (side * 0.4))), halfExtents: V(x: 0.2, y: 1.5, z: (side * 0.4))));
                colliders.Add(item: FixedStaticCollider.AxisAlignedBox(center: V(x: (x + (side * 0.66)), y: 1.5, z: (z + (side * 0.6))), halfExtents: V(x: 0.2, y: 1.5, z: (side * 0.4))));
            }
        }

        return new NavigationRuntime(
            capacity: Capacity(goalFieldBytes: goalFieldBytes),
            domains: [
                District(name: "square", x: 0, z: 0),
                District(name: "terraces", x: side, z: 0),
                District(name: "works", x: 0, z: side),
                District(name: "quay", x: side, z: side),
                District(cells: 32, name: "road", network: "road", x: 0, z: 0),
            ],
            fields: null,
            geometry: new FixedStaticContactField(colliders: colliders.ToArray(), solver: Solver),
            work: work
        );
    }
    // Every walkable cell of the four districts, in domain then cell order.
    private static (int Domain, int Cell)[] Walkable(NavigationRuntime runtime) {
        var cells = new List<(int Domain, int Cell)>();

        for (var domain = 0; (domain < 4); domain++) {
            for (var cell = 0; (cell < runtime[domain].CellCount); cell++) {
                if (runtime[domain].IsWalkable(node: cell)) {
                    cells.Add(item: (domain, cell));
                }
            }
        }

        return cells.ToArray();
    }
    // Follows a field from a cell to its goal; returns the steps taken, or -1 when the field leads nowhere.
    private static int Follow(NavigationRuntime runtime, NavigationGoalField field, int domain, int cell) {
        for (var steps = 0; (steps <= field.ReachedCells); steps++) {
            switch (runtime.GoalStep(cell: cell, domain: domain, field: field, nextCell: out var nextCell, nextDomain: out var nextDomain)) {
                case NavigationStatus.Arrived:
                    return steps;
                case NavigationStatus.Active:
                    domain = nextDomain;
                    cell = nextCell;
                    break;
                default:
                    return -1;
            }
        }

        return -1;
    }

    [Fact]
    public void AFieldLeadsEveryCellTheRouteSearchReachesToTheGoalAcrossDistricts() {
        var runtime = Town();
        var walkable = Walkable(runtime: runtime);
        var goal = walkable[(walkable.Length - 1)];
        var field = runtime.GoalField(goalCell: goal.Cell, goalDomain: goal.Domain);
        var route = new List<NavigationRouteNode>();

        Assert.True((runtime.JunctionCount > 0), userMessage: "neighbouring districts join where they share cells");
        for (var index = 0; (index < walkable.Length); index += 97) {
            var (domain, cell) = walkable[index];
            var status = runtime.FindRoute(fromCell: cell, fromDomain: domain, goal: new NavigationCellGoal(Cell: goal.Cell, Domain: goal.Domain, Position: runtime[goal.Domain].Position(node: goal.Cell)), maxExpanded: int.MaxValue, maxNodes: int.MaxValue, route: route, work: out _);
            var steps = Follow(cell: cell, domain: domain, field: field, runtime: runtime);

            Assert.Equal(expected: (status is NavigationStatus.Active or NavigationStatus.Arrived), actual: (steps >= 0));
        }
        // The road is another network: the field neither covers it nor steps into it.
        Assert.Equal(expected: NavigationStatus.OutsideDomain, actual: runtime.GoalStep(cell: 0, domain: 4, field: field, nextCell: out _, nextDomain: out _));
    }

    [Fact]
    public void AFieldGivesTheSameStepsWhicheverFieldsAreResident() {
        var roomy = Town();
        var tight = Town(goalFieldBytes: 1);
        var walkable = Walkable(runtime: roomy);
        var goals = new[] { walkable[17], walkable[(walkable.Length / 2)], walkable[(walkable.Length - 3)] };

        for (var round = 0; (round < 2); round++) {
            foreach (var goal in goals) {
                var kept = roomy.GoalField(goalCell: goal.Cell, goalDomain: goal.Domain);
                var rebuilt = tight.GoalField(goalCell: goal.Cell, goalDomain: goal.Domain);

                for (var index = 0; (index < walkable.Length); index += 7) {
                    var (domain, cell) = walkable[index];

                    Assert.Equal(
                        expected: (roomy.GoalStep(cell: cell, domain: domain, field: kept, nextCell: out var keptCell, nextDomain: out var keptDomain), keptDomain, keptCell),
                        actual: (tight.GoalStep(cell: cell, domain: domain, field: rebuilt, nextCell: out var rebuiltCell, nextDomain: out var rebuiltDomain), rebuiltDomain, rebuiltCell)
                    );
                }
            }
        }
        Assert.Equal(expected: 3, actual: roomy.ResidentGoalFields);
        Assert.Equal(expected: 1, actual: tight.ResidentGoalFields);
        Assert.True((tight.GoalFieldEvictions >= 5), userMessage: $"the one-byte cache dropped {tight.GoalFieldEvictions} fields");
    }

    [Fact]
    public void ATownOfBodiesSharingFewGoalsReadsOneStepEachPerTick() {
        var walkable = Walkable(runtime: Town());
        // Goals and starts spread over the walkable cells by a fixed stride; a body's start and goal repeat every 2,000
        // bodies, so a crowd's searches are the 2,000-body crowd's searches repeated.
        var goals = Enumerable.Range(start: 0, count: GoalCount).Select(selector: index => walkable[((index * 7919) % walkable.Length)]).ToArray();
        (int Domain, int Cell) Start(int body) => walkable[((((body % 2000) * 104729) + 13) % walkable.Length)];

        foreach (var bodies in new[] { 128, 2_000, 10_000 }) {
            var work = new NavigationSearchWork();
            var runtime = Town(work: work);
            var positions = Enumerable.Range(start: 0, count: bodies).Select(selector: Start).ToArray();
            var ticks = new (long Builds, long Cells, long Steps)[4];

            for (var tick = 0; (tick < ticks.Length); tick++) {
                var before = (Builds: work.Read(kind: NavigationSearchWork.FieldBuilds), Cells: work.Read(kind: NavigationSearchWork.FieldCells), Steps: work.Read(kind: NavigationSearchWork.FieldSteps));

                runtime.BeginStep();
                for (var body = 0; (body < bodies); body++) {
                    var goal = goals[(body % GoalCount)];
                    var field = runtime.GoalField(goalCell: goal.Cell, goalDomain: goal.Domain);

                    if (runtime.GoalStep(cell: positions[body].Cell, domain: positions[body].Domain, field: field, nextCell: out var next, nextDomain: out var nextDomain) == NavigationStatus.Active) {
                        positions[body] = (nextDomain, next);
                    }
                }
                ticks[tick] = (
                    (work.Read(kind: NavigationSearchWork.FieldBuilds) - before.Builds),
                    (work.Read(kind: NavigationSearchWork.FieldCells) - before.Cells),
                    (work.Read(kind: NavigationSearchWork.FieldSteps) - before.Steps)
                );
            }

            // The same crowd with a route search per body, each searched once from its start: the work a body spends
            // when it first heads off, and again on every replan a push forces.
            var routeWork = new NavigationSearchWork();
            var routes = Town(work: routeWork);
            var route = new List<NavigationRouteNode>();
            var searched = new Dictionary<(int, int, int, int), (long Expanded, int Nodes)>();
            var routeExpanded = 0L;
            var routeNodes = 0L;

            for (var body = 0; (body < bodies); body++) {
                var start = Start(body: body);
                var goal = goals[(body % GoalCount)];

                if (!searched.TryGetValue(key: (start.Domain, start.Cell, goal.Domain, goal.Cell), value: out var cost)) {
                    _ = routes.FindRoute(fromCell: start.Cell, fromDomain: start.Domain, goal: new NavigationCellGoal(Cell: goal.Cell, Domain: goal.Domain, Position: routes[goal.Domain].Position(node: goal.Cell)), maxExpanded: int.MaxValue, maxNodes: int.MaxValue, route: route, work: out var searchWork);
                    cost = (searchWork.Expanded, route.Count);
                    searched[(start.Domain, start.Cell, goal.Domain, goal.Cell)] = cost;
                }
                routeExpanded += cost.Expanded;
                routeNodes += cost.Nodes;
            }

            var distinctGoals = Math.Min(val1: bodies, val2: GoalCount);

            output.WriteLine(message: $"bodies={bodies} goals={distinctGoals}: goal fields tick0 builds={ticks[0].Builds} cells={ticks[0].Cells} steps={ticks[0].Steps}; later ticks builds={ticks[1].Builds}+{ticks[2].Builds}+{ticks[3].Builds} cells={ticks[1].Cells} steps={ticks[1].Steps}; resident={runtime.ResidentGoalFields} bytes={runtime.GoalFieldBytes}");
            output.WriteLine(message: $"bodies={bodies}: a route per body: searches={bodies} expanded={routeExpanded} route nodes held={routeNodes} ({(routeNodes * sizeof(int))} bytes)");

            Assert.Equal(expected: distinctGoals, actual: ticks[0].Builds);
            Assert.Equal(expected: bodies, actual: ticks[0].Steps);
            for (var tick = 1; (tick < ticks.Length); tick++) {
                Assert.Equal(expected: (0L, 0L, (long)bodies), actual: ticks[tick]);
            }
        }
    }
}
