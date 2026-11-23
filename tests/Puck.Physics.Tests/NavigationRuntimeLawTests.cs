using Puck.Maths;
using Puck.Physics.Navigation;

namespace Puck.Physics.Tests;

/// <summary>Pins the navigation kernel's contract over the static contact geometry bodies collide with: a bake proves
/// what a body's own resolve and sweep would, gives the same answer on the thread pool as inline, and is kept across a
/// replacement exactly when the geometry part its domain reads is equal.</summary>
public sealed class NavigationRuntimeLawTests {
    private static readonly FixedStaticContactSolver Solver = new(ContactSkin: Q(x: 0.02), GroundedThreshold: Q(x: 0.5), MaxIterations: 4);

    private static FixedQ4816 Q(double x) => FixedQ4816.FromDouble(value: x);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(x: x), Y: Q(x: y), Z: Q(x: z));
    private static NavigationCapacity Capacity() => new(MaxMediumSegmentSubdivisions: 32, GoalFieldBytes: 64L << 20);
    // A 5 m by 3 m floor whose top is at zero.
    private static FixedStaticCollider Floor() => FixedStaticCollider.AxisAlignedBox(center: V(x: 2, y: -0.5, z: 1), halfExtents: V(x: 4, y: 0.5, z: 3));
    // A wall across x = 2, from z = -1 to z = 2.25, leaving a gap at the far edge of the floor.
    private static FixedStaticCollider Wall() => FixedStaticCollider.AxisAlignedBox(center: V(x: 2, y: 1.5, z: 0.625), halfExtents: V(x: 0.1, y: 1.5, z: 1.625));
    private static FixedStaticContactField Field(params FixedStaticCollider[] colliders) => new(colliders: colliders, solver: Solver);
    private static NavigationDomainInput Surface(int width = 9, int depth = 6, string name = "ground") => new(
        Name: name,
        Kind: NavigationKind.Surface,
        Origin: V(x: 0, y: 0.05, z: 0),
        CellSize: Q(x: 0.5),
        Width: width,
        Depth: depth,
        Layers: 1,
        Connectivity: NavigationConnectivity.Full,
        ProbeUp: Q(x: 2),
        ProbeDown: Q(x: 1),
        AgentRadius: Q(x: 0.2),
        AgentHeight: Q(x: 1.8),
        MaxStepHeight: Q(x: 0.3),
        MaximumSlopeRise: Q(x: 0.3),
        ArrivalDistance: Q(x: 0.5),
        MaxExpandedNodes: (width * depth),
        MaxPathNodes: 256,
        Medium: null,
        Shared: false
    );

    [Fact]
    public void ARouteBendsThroughTheGapAWallLeaves() {
        var domain = new NavigationRuntime(domains: [Surface()], geometry: Field(Floor(), Wall()), fields: null, capacity: Capacity())[0];
        Span<int> path = stackalloc int[64];

        Assert.True(domain.TryCell(node: out var start, position: V(x: 0.5, y: 0, z: 0)));
        Assert.True(domain.TryCell(node: out var goal, position: V(x: 3.5, y: 0, z: 0)));
        Assert.Equal(expected: NavigationStatus.Active, actual: domain.FindPath(expanded: out _, goal: goal, path: path, pathLength: out var length, start: start));
        // Every crossing of x = 2 runs beyond the wall's far end (z = 2.25) and the agent's radius.
        for (var index = 1; (index < length); index++) {
            var from = domain.Position(node: path[(index - 1)]);
            var to = domain.Position(node: path[index]);

            if ((from.X < Q(x: 2)) != (to.X < Q(x: 2))) {
                Assert.True((FixedQ4816.Min(x: from.Z, y: to.Z) > Q(x: 2.4)), userMessage: $"crossed at z={(double)from.Z}..{(double)to.Z}");
            }
        }
        Assert.False(domain.TryCell(node: out _, position: V(x: 2, y: 0, z: 0)));
    }

    [Fact]
    public void ABakeOnTheThreadPoolEqualsAnInlineBake() {
        var geometry = Field(Floor(), Wall());
        var inline = new NavigationRuntime(domains: [Surface()], geometry: geometry, fields: null, capacity: Capacity())[0];
        var background = new NavigationRuntime(domains: [Surface()], geometry: geometry, fields: null, capacity: Capacity());

        background.BeginBakes();
        var pooled = background[0];

        Assert.Equal(expected: inline.WalkableCellCount, actual: pooled.WalkableCellCount);
        for (var node = 0; (node < inline.CellCount); node++) {
            Assert.Equal(expected: inline.IsWalkable(node: node), actual: pooled.IsWalkable(node: node));
            if (inline.IsWalkable(node: node)) {
                Assert.Equal(expected: inline.Position(node: node), actual: pooled.Position(node: node));
            }
            for (var other = 0; (other < inline.CellCount); other++) {
                Assert.Equal(expected: inline.IsTraversableEdge(current: node, next: other), actual: pooled.IsTraversableEdge(current: node, next: other));
            }
        }
        Assert.Equal(expected: inline.BakeWork with { AllocatedBytes = 0 }, actual: pooled.BakeWork with { AllocatedBytes = 0 });
    }

    [Fact]
    public void EqualGeometryKeepsTheBakeAndAChangeInsideTheRegionRebuildsIt() {
        var previous = new NavigationRuntime(domains: [Surface()], geometry: Field(Floor(), Wall()), fields: null, capacity: Capacity());

        _ = previous[0].WalkableCellCount;

        // Equal colliders in a new array, and one far beyond the domain's region: the part the domain reads is equal.
        var far = FixedStaticCollider.AxisAlignedBox(center: V(x: 100, y: 0, z: 100), halfExtents: V(x: 1, y: 1, z: 1));
        var retained = new NavigationRuntime(domains: [Surface()], geometry: Field(Floor(), Wall(), far), fields: null, capacity: Capacity(), previous: previous);

        Assert.True(retained.WasRetained(index: 0));
        Assert.True(retained[0].IsBaked);
        Assert.Same(expected: previous[0], actual: retained[0]);

        // Without the wall the domain's part differs, so it bakes afresh and the straight crossing opens.
        var rebuilt = new NavigationRuntime(domains: [Surface()], geometry: Field(Floor()), fields: null, capacity: Capacity(), previous: retained);

        Assert.False(rebuilt.WasRetained(index: 0));
        Assert.False(rebuilt[0].IsBaked);
        Assert.True(rebuilt[0].TryCell(node: out _, position: V(x: 2, y: 0, z: 0)));
    }

    [Theory]
    [InlineData(0.25, true)]
    [InlineData(0.4, false)]
    public void ARiserUnderTheStepHeightConnectsItsTreads(double riser, bool connected) {
        var upper = FixedStaticCollider.AxisAlignedBox(center: V(x: 3.5, y: (riser / 2), z: 1), halfExtents: V(x: 1.25, y: (riser / 2), z: 3));
        var domain = new NavigationRuntime(domains: [Surface()], geometry: Field(Floor(), upper), fields: null, capacity: Capacity())[0];
        Span<int> path = stackalloc int[64];

        Assert.True(domain.TryCell(node: out var start, position: V(x: 0.5, y: 0, z: 1)));
        Assert.True(domain.TryCell(node: out var goal, position: V(x: 3.5, y: riser, z: 1)));
        Assert.Equal(expected: (riser + 0.22), actual: (double)domain.Position(node: goal).Y, precision: 3);
        Assert.Equal(
            expected: (connected ? NavigationStatus.Active : NavigationStatus.Unreachable),
            actual: domain.FindPath(expanded: out _, goal: goal, path: path, pathLength: out _, start: start)
        );
    }

    [Fact]
    public void AVolumeAgentAdmitsOnlyAClearSweep() {
        var row = new NavigationDomainInput(
            Name: "air",
            Kind: NavigationKind.Volume,
            Origin: V(x: 0, y: 1, z: 0),
            CellSize: Q(x: 0.5),
            Width: 9,
            Depth: 6,
            Layers: 2,
            Connectivity: NavigationConnectivity.Full,
            ProbeUp: FixedQ4816.Zero,
            ProbeDown: FixedQ4816.Zero,
            AgentRadius: Q(x: 0.2),
            AgentHeight: FixedQ4816.Zero,
            MaxStepHeight: FixedQ4816.Zero,
            MaximumSlopeRise: FixedQ4816.Zero,
            ArrivalDistance: Q(x: 0.5),
            MaxExpandedNodes: 108,
            MaxPathNodes: 108,
            Medium: null,
            Shared: false
        );
        var domain = new NavigationRuntime(domains: [row], geometry: Field(Floor(), Wall()), fields: null, capacity: Capacity())[0];

        Assert.False(domain.AdmitsLocomotion(from: V(x: 1, y: 1, z: 0.5), to: V(x: 3, y: 1, z: 0.5)));
        Assert.True(domain.AdmitsLocomotion(from: V(x: 1, y: 1, z: 2.5), to: V(x: 3, y: 1, z: 2.5)));
    }
}
