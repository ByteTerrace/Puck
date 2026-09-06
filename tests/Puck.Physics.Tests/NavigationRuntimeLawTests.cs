using System.Numerics;
using Puck.Maths;
using Puck.Physics.Navigation;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.Physics.Tests;

/// <summary>Pins the navigation kernel's own contract now that it lives beside every other Physics kernel: it
/// builds from a plain <see cref="NavigationDomainInput"/>, drives an <see cref="IWorldQuery"/> it is handed, and
/// consults a medium field only through <see cref="INavigationMediumField"/> — no <c>Puck.World</c> or
/// <c>Puck.World.Schema</c> type appears anywhere in this file.</summary>
public sealed class NavigationRuntimeLawTests {
    private static NavigationCapacity Capacity() => new(MaxSurfaceClearanceSweeps: 16, MaxMediumSegmentSubdivisions: 32, MaxConcurrentRequesters: 4096);

    private static NavigationDomainInput VolumeDomain(int width = 6, int depth = 1, int layers = 1, string? medium = null, NavigationSharing? shared = null) => new(
        Name: (medium is null ? "air" : "water"),
        Kind: (medium is null ? NavigationKind.Volume : NavigationKind.Medium),
        Origin: FixedVector3.Zero,
        CellSize: FixedQ4816.One,
        Width: width,
        Depth: depth,
        Layers: layers,
        Connectivity: NavigationConnectivity.Full,
        ProbeUp: FixedQ4816.Zero,
        ProbeDown: FixedQ4816.Zero,
        AgentRadius: FixedQ4816.FromDouble(value: 0.1),
        AgentHeight: FixedQ4816.Zero,
        MaxStepHeight: FixedQ4816.Zero,
        MaximumSlopeRise: FixedQ4816.Zero,
        ArrivalDistance: FixedQ4816.FromDouble(value: 0.2),
        MaxExpandedNodes: 256,
        MaxPathNodes: 256,
        Medium: medium,
        Shared: shared
    );

    // Reports every point clear — the open-universe control an obstruction test is measured against.
    private sealed class OpenQuery : IWorldQuery {
        public QueryCapabilities Capabilities => new(HasHeightfield: false, HasBlocked: false, HasOccupancy: false);
        public bool Raycast(FixedPosition origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit) { hit = default; return false; }
        public bool SphereCast(FixedPosition origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit) { hit = default; return false; }
        public bool Overlap(FixedPosition center, FixedQ4816 radius) => false;
        public bool TryGroundHeight(FixedPosition position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY) { groundY = FixedQ4816.Zero; return true; }
        public bool LineOfSight(FixedPosition from, FixedPosition to) => true;
    }

    // A minimal live-medium field the kernel drives purely through its own narrow seam.
    private sealed class StubMediumField(Func<FixedVector3, bool> isWet) : INavigationMediumField {
        public ulong ValueRevision(int field) => 0;
        public bool IsInsideMedium(int field, in FixedVector3 position, FixedQ4816 clearance) => isWet(position);
        public bool IsSegmentInsideMedium(int field, in FixedVector3 from, in FixedVector3 to, FixedQ4816 clearance, int maximumSubdivisions) =>
            isWet(from) && isWet(to);
        public bool TryFieldIndex(string name, out int field) { field = 0; return true; }
    }

    private static SdfFieldEvaluator ThinWallAt(float x) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f)));
        builder.Translate(offset: new Vector3(x: x, y: 0f, z: 0f));
        builder.Box(halfExtents: new Vector3(x: 0.15f, y: 5f, z: 5f), round: 0f, material: material);
        return new SdfFieldEvaluator(program: builder.Build());
    }

    [Fact]
    public void FindPathCrossesAnOpenVolumeDomain() {
        var runtime = new NavigationRuntime(domains: [VolumeDomain()], query: new OpenQuery(), fields: null, capacity: Capacity());
        var domain = runtime[0];
        Assert.True(condition: domain.TryCell(position: FixedVector3.Zero, node: out var start));
        Assert.True(condition: domain.TryCell(position: new FixedVector3(X: FixedQ4816.FromInteger(value: 5), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), node: out var goal));

        Span<int> path = stackalloc int[256];
        var status = domain.FindPath(start: start, goal: goal, path: path, pathLength: out var length, expanded: out var expanded);

        Assert.Equal(expected: NavigationStatus.Active, actual: status);
        Assert.True(condition: length > 1);
        Assert.True(condition: expanded > 0);
        Assert.Equal(expected: start, actual: path[0]);
        Assert.Equal(expected: goal, actual: path[length - 1]);
    }

    [Fact]
    public void AdmitsLocomotionRefusesASweptSegmentThatCrossesSolidGeometryAndAdmitsAClearOne() {
        var query = ThinWallAt(x: 2.5f);
        var domain = new NavigationRuntime(domains: [VolumeDomain()], query: query, fields: null, capacity: Capacity())[0];

        var origin = FixedVector3.Zero;
        var throughTheWall = new FixedVector3(X: FixedQ4816.FromInteger(value: 5), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var shortOfTheWall = new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);

        Assert.False(condition: domain.AdmitsLocomotion(from: origin, to: throughTheWall));
        Assert.True(condition: domain.AdmitsLocomotion(from: origin, to: shortOfTheWall));
    }

    [Fact]
    public void MediumDomainReadsOccupancyEntirelyThroughTheInjectedFieldSeam() {
        var mediumField = new StubMediumField(isWet: position => position.X <= FixedQ4816.FromDouble(value: 1.5));
        var domain = new NavigationRuntime(
            domains: [VolumeDomain(width: 3, medium: "water")],
            query: new OpenQuery(),
            fields: mediumField,
            capacity: Capacity()
        )[0];

        Assert.True(condition: domain.IsWalkable(node: 0));
        Assert.False(condition: domain.IsWalkable(node: 2));

        Span<int> path = stackalloc int[8];
        var status = domain.FindPath(start: 0, goal: 2, path: path, pathLength: out _, expanded: out _);

        Assert.Equal(expected: NavigationStatus.OutsideDomain, actual: status);
    }

    [Fact]
    public void SharedRequestPendsThenSettlesAndCheckpointsThroughPlainRecordsAlone() {
        var domain = new NavigationRuntime(
            domains: [VolumeDomain(shared: new NavigationSharing(GoalCapacity: 1, ExpandedNodesPerTick: 1))],
            query: new OpenQuery(),
            fields: null,
            capacity: Capacity()
        )[0];

        Span<int> path = stackalloc int[256];
        var status = domain.RequestShared(start: 0, goal: 5, path: path, length: out var length);
        Assert.True(condition: status is NavigationStatus.Pending or NavigationStatus.Active);

        for (var tick = 0; tick < 32 && status != NavigationStatus.Active; tick++) {
            domain.AdvanceShared();
            status = domain.RequestShared(start: 0, goal: 5, path: path, length: out length);
        }

        Assert.Equal(expected: NavigationStatus.Active, actual: status);
        Assert.Equal(expected: 0, actual: path[0]);
        Assert.Equal(expected: 5, actual: path[length - 1]);

        var checkpoint = domain.CaptureShared();
        domain.RestoreShared(checkpoint: checkpoint);
        var replay = domain.CaptureShared();

        Assert.Equal(expected: checkpoint.Cursor, actual: replay.Cursor);
        Assert.Equal(expected: checkpoint.Trees.Length, actual: replay.Trees.Length);
    }
}
