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
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(37)]
    [InlineData(-135)]
    public void RotatedGridPositionsRoundTripAndKeepTheSameRoutes(int degrees) {
        var row = VolumeDomain(width: 4, depth: 3, layers: 2);
        var original = new NavigationRuntime([row], new OpenQuery(), null, Capacity())[0];
        var turned = new NavigationRuntime([row with {
            Origin = FixedVector3.FromVector3(new Vector3(12, 5, -7)),
            YawRadians = FixedQ4816.FromDouble(degrees * (Math.PI / 180))
        }], new OpenQuery(), null, Capacity())[0];
        for (var index = 0; index < turned.CellCount; index++) {
            Assert.True(turned.TryCell(turned.Position(index), out var roundTrip));
            Assert.Equal(index, roundTrip);
        }
        var first = new int[256];
        var second = new int[256];
        Assert.Equal(original.FindPath(0, original.CellCount - 1, first, out var firstLength, out _),
            turned.FindPath(0, turned.CellCount - 1, second, out var secondLength, out _));
        Assert.Equal(first[..firstLength], second[..secondLength]);
    }
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
    private sealed class StubMediumField(Func<FixedVector3, bool> isWet, ulong revision = 0) : INavigationMediumField {
        private readonly ulong m_revision = revision;
        public ulong ValueRevision(int field) => m_revision;
        public bool IsInsideMedium(int field, in FixedVector3 position, FixedQ4816 clearance) => isWet(position);
        public bool IsSegmentInsideMedium(int field, in FixedVector3 from, in FixedVector3 to, FixedQ4816 clearance, int maximumSubdivisions) =>
            isWet(from) && isWet(to);
        public bool TryFieldIndex(string name, out int field) { field = 0; return true; }
    }

    private static NavigationDomainInput SurfaceDomain() => new(
        Name: "ground",
        Kind: NavigationKind.Surface,
        Origin: FixedVector3.Zero,
        CellSize: FixedQ4816.One,
        Width: 3,
        Depth: 1,
        Layers: 1,
        Connectivity: NavigationConnectivity.Axis,
        ProbeUp: FixedQ4816.FromInteger(value: 2),
        ProbeDown: FixedQ4816.FromInteger(value: 2),
        AgentRadius: FixedQ4816.FromDouble(value: 0.1),
        AgentHeight: FixedQ4816.FromDouble(value: 2),
        MaxStepHeight: FixedQ4816.FromInteger(value: 1),
        MaximumSlopeRise: FixedQ4816.FromInteger(value: 1),
        ArrivalDistance: FixedQ4816.FromDouble(value: 0.2),
        MaxExpandedNodes: 256,
        MaxPathNodes: 256,
        Medium: null,
        Shared: null
    );

    private sealed class SurfaceQuery(int? blockedX) : IWorldQuery {
        public QueryCapabilities Capabilities => new(HasHeightfield: true, HasBlocked: true, HasOccupancy: false);
        public bool Raycast(FixedPosition origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit) { hit = default; return false; }
        public bool SphereCast(FixedPosition origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit) { hit = default; return false; }
        public bool Overlap(FixedPosition center, FixedQ4816 radius) => blockedX is { } x && center.Local.X == FixedQ4816.FromInteger(x);
        public bool TryGroundHeight(FixedPosition position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY) { groundY = FixedQ4816.Zero; return true; }
        public bool LineOfSight(FixedPosition from, FixedPosition to) => true;
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
    public void RetainsEquivalentBakeAndForwardsOffGridQueriesToTheNewProvider() {
        var row = VolumeDomain(width: 4);
        var previous = new NavigationRuntime([row], new OpenQuery(), null, Capacity());
        var replacementQuery = ThinWallAt(x: 2.5f);
        var replacement = new NavigationRuntime([row], replacementQuery, null, Capacity(), previous);

        Assert.Equal(1, replacement.RetainedDomainCount);
        Assert.Equal(0, replacement.RebuiltDomainCount);
        Assert.Same(previous[0], replacement[0]);
        Assert.False(replacement[0].AdmitsLocomotion(
            from: FixedVector3.Zero,
            to: new FixedVector3(X: FixedQ4816.FromInteger(value: 3), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)));
    }

    [Fact]
    public void RebuildsWhenACompileTimeCellBakeChanges() {
        var row = VolumeDomain(width: 4);
        var previous = new NavigationRuntime([row], new OpenQuery(), null, Capacity());
        var replacement = new NavigationRuntime([row], ThinWallAt(x: 0f), null, Capacity(), previous);

        Assert.Equal(0, replacement.RetainedDomainCount);
        Assert.Equal(1, replacement.RebuiltDomainCount);
        Assert.NotSame(previous[0], replacement[0]);
    }

    [Fact]
    public void RetainsAnUnaffectedDomainWhileRebuildingTheAffectedDomain() {
        var affected = VolumeDomain(width: 4);
        var unaffected = affected with {
            Name = "far",
            Origin = new FixedVector3(X: FixedQ4816.FromInteger(value: 100), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)
        };
        var previous = new NavigationRuntime([affected, unaffected], new OpenQuery(), null, Capacity());
        var replacement = new NavigationRuntime([affected, unaffected], ThinWallAt(x: 0f), null, Capacity(), previous);

        Assert.Equal(1, replacement.RetainedDomainCount);
        Assert.Equal(1, replacement.RebuiltDomainCount);
        Assert.NotSame(previous[0], replacement[0]);
        Assert.Same(previous[1], replacement[1]);
    }

    [Fact]
    public void RetainedDomainKeepsSharedSearchState() {
        var row = VolumeDomain(width: 6, shared: new NavigationSharing(GoalCapacity: 1, ExpandedNodesPerTick: 1));
        var previous = new NavigationRuntime([row], new OpenQuery(), null, Capacity());
        Span<int> path = stackalloc int[256];
        _ = previous[0].RequestShared(start: 0, goal: 5, path: path, length: out _);
        previous[0].AdvanceShared();
        var before = previous[0].CaptureShared();

        var replacement = new NavigationRuntime([row], new OpenQuery(), null, Capacity(), previous);

        Assert.Same(previous[0], replacement[0]);
        var after = replacement[0].CaptureShared();
        Assert.Equal(before.Cursor, after.Cursor);
        Assert.Equal(before.Trees.Length, after.Trees.Length);
        Assert.Equal(before.Trees[0].Goal, after.Trees[0].Goal);
        Assert.Equal(before.Trees[0].Nodes.Length, after.Trees[0].Nodes.Length);
        Assert.Equal(before.Trees[0].Pending, after.Trees[0].Pending);
    }

    [Fact]
    public void RetainsAnUnchangedSurfaceBakeIncludingItsBlockedCell() {
        var row = SurfaceDomain();
        var query = new SurfaceQuery(blockedX: 1);
        var previous = new NavigationRuntime([row], query, null, Capacity());

        Assert.Equal(2, previous[0].WalkableCellCount);
        Assert.False(previous[0].TryCell(new FixedVector3(
            X: FixedQ4816.One,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero), out _));

        var replacement = new NavigationRuntime([row], new SurfaceQuery(blockedX: 1), null, Capacity(), previous);

        Assert.Equal(1, replacement.RetainedDomainCount);
        Assert.Equal(0, replacement.RebuiltDomainCount);
        Assert.Same(previous[0], replacement[0]);
    }

    [Fact]
    public void RebuildsWhenNavigationCapacityChanges() {
        var row = VolumeDomain(width: 3);
        var previous = new NavigationRuntime([row], new OpenQuery(), null, Capacity());
        var changed = Capacity() with { MaxMediumSegmentSubdivisions = Capacity().MaxMediumSegmentSubdivisions + 1 };

        var replacement = new NavigationRuntime([row], new OpenQuery(), null, changed, previous);

        Assert.Equal(0, replacement.RetainedDomainCount);
        Assert.Equal(1, replacement.RebuiltDomainCount);
        Assert.NotSame(previous[0], replacement[0]);
    }

    [Fact]
    public void RetainsAnUnchangedMediumDomainWithTheSameProviderAndRevision() {
        var row = VolumeDomain(width: 3, medium: "water");
        var query = new OpenQuery();
        var field = new StubMediumField(isWet: static _ => true, revision: 17);
        var previous = new NavigationRuntime([row], query, field, Capacity());

        var replacement = new NavigationRuntime([row], query, field, Capacity(), previous);

        Assert.Equal(1, replacement.RetainedDomainCount);
        Assert.Equal(0, replacement.RebuiltDomainCount);
        Assert.Same(previous[0], replacement[0]);
    }

    [Fact]
    public void RebuildsMediumDomainWhenASeparateProviderReusesItsRevision() {
        var row = VolumeDomain(width: 3, medium: "water");
        var query = new OpenQuery();
        var previous = new NavigationRuntime([row], query, new StubMediumField(static _ => true, revision: 17), Capacity());
        var replacement = new NavigationRuntime([row], query, new StubMediumField(static _ => true, revision: 17), Capacity(), previous);

        Assert.Equal(0, replacement.RetainedDomainCount);
        Assert.Equal(1, replacement.RebuiltDomainCount);
        Assert.NotSame(previous[0], replacement[0]);
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
