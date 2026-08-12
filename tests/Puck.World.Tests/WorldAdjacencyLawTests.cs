using System.Numerics;
using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for invisible reciprocal ownership boundaries: exact cardinal frames, outward swept handoff,
/// compiler-owned symmetric overlap, and cross-document reciprocal proof.</summary>
public sealed class WorldAdjacencyLawTests {
    [Fact]
    public void CardinalFramesKeepExactAxes() {
        var east = Boundary(yaw: 90f).CompileFrame();
        var west = Boundary(yaw: -90f).CompileFrame();

        Assert.Equal(new FixedVector3(FixedQ4816.One, FixedQ4816.Zero, FixedQ4816.Zero), east.Normal);
        Assert.Equal(new FixedVector3(-FixedQ4816.One, FixedQ4816.Zero, FixedQ4816.Zero), west.Normal);
        Assert.Equal(-east.Right, west.Right);
    }

    [Fact]
    public void CardinalPitchKeepsExactAxesAndVerticalPairIsIdentity() {
        var up = Boundary(yaw: 0f, pitch: 90f).CompileFrame();
        var down = Boundary(yaw: 180f, pitch: -90f).CompileFrame();
        var worldUp = Fixed(x: 0, y: 1, z: 0);
        var probe = Fixed(x: 3, y: 7, z: -2);

        Assert.Equal(worldUp, up.Normal);
        Assert.Equal(-worldUp, down.Normal);
        Assert.Equal(probe, WorldFrameIsometry.MapVector(value: probe, source: up, destination: down));
    }

    [Fact]
    public void HorizontalBoundarySweepsUpward() {
        var frame = Boundary(yaw: 0f, pitch: 90f).CompileFrame();
        var crossing = WorldAdjacencyRegion.Sweep(frame, Fixed(x: 0, y: -1, z: 0), Fixed(x: 0, y: 1, z: 0));

        Assert.True(crossing.Crossed);
        Assert.Equal(FixedQ4816.FromDouble(0.5), crossing.Parameter);
    }

    [Fact]
    public void SweepOnlyHandsOffOutwardThroughRectangle() {
        var frame = Boundary(yaw: 90f).CompileFrame();
        var outward = WorldAdjacencyRegion.Sweep(frame, Fixed(x: -1, y: 0, z: 0), Fixed(x: 1, y: 0, z: 0));
        var inward = WorldAdjacencyRegion.Sweep(frame, Fixed(x: 1, y: 0, z: 0), Fixed(x: -1, y: 0, z: 0));
        var above = WorldAdjacencyRegion.Sweep(frame, Fixed(x: -1, y: 20, z: 0), Fixed(x: 1, y: 20, z: 0));

        Assert.True(outward.Crossed);
        Assert.Equal(FixedQ4816.FromDouble(0.5), outward.Parameter);
        Assert.False(inward.Crossed);
        Assert.False(above.Crossed);
    }

    [Fact]
    public void OverlapIsSymmetricAndPositive() {
        var first = Fixtures.BuildDocument();
        var second = Fixtures.BuildDocument() with { Simulation = new WorldSimulationDefaults(RateHz: 30) };

        Assert.True(WorldAdjacencyPolicy.TryDeriveOverlap(first, second, out var forward, out var forwardReason), forwardReason);
        Assert.True(WorldAdjacencyPolicy.TryDeriveOverlap(second, first, out var reverse, out var reverseReason), reverseReason);
        Assert.True(WorldAdjacencyPolicy.TryReciprocalHysteresis(first, out var firstHysteresis, out var firstReason), firstReason);
        Assert.True(WorldAdjacencyPolicy.TryReciprocalHysteresis(second, out var secondHysteresis, out var secondReason), secondReason);
        Assert.Equal(forward, reverse);
        Assert.True(forward > FixedQ4816.Zero);
        Assert.True(forward >= firstHysteresis);
        Assert.True(forward >= secondHysteresis);
    }

    [Fact]
    public void ReciprocalHysteresisFormsAClosedOwnershipDeadbandForRapidReversal() {
        var source = Boundary(yaw: 90f).CompileFrame();
        var destination = Boundary(yaw: -90f).CompileFrame();
        var hysteresis = FixedQ4816.FromDouble(value: 0.72);
        var sourceThreshold = WorldAdjacencyPolicy.OwnershipThreshold(frame: in source, reciprocalHysteresis: hysteresis);
        var destinationThreshold = WorldAdjacencyPolicy.OwnershipThreshold(frame: in destination, reciprocalHysteresis: hysteresis);

        var seamZig = WorldAdjacencyRegion.Sweep(
            frame: source,
            from: Fixed(x: -0.1, y: 0, z: 0),
            to: Fixed(x: 0.1, y: 0, z: 0),
            outwardThreshold: sourceThreshold);
        var sourceExit = WorldAdjacencyRegion.Sweep(
            frame: source,
            from: Fixed(x: 0.7, y: 0, z: 0),
            to: Fixed(x: 0.8, y: 0, z: 0),
            outwardThreshold: sourceThreshold);
        var destinationNearReturn = WorldAdjacencyRegion.Sweep(
            frame: destination,
            from: Fixed(x: 0.8, y: 0, z: 0),
            to: Fixed(x: -0.1, y: 0, z: 0),
            outwardThreshold: destinationThreshold);
        var destinationExit = WorldAdjacencyRegion.Sweep(
            frame: destination,
            from: Fixed(x: 0.8, y: 0, z: 0),
            to: Fixed(x: -0.8, y: 0, z: 0),
            outwardThreshold: destinationThreshold);

        Assert.Equal(hysteresis, sourceThreshold);
        Assert.Equal(hysteresis, destinationThreshold);
        Assert.False(seamZig.Crossed);
        Assert.True(sourceExit.Crossed);
        Assert.False(destinationNearReturn.Crossed);
        Assert.True(destinationExit.Crossed);
    }

    [Fact]
    public void YawOnlyHysteresisClosesTheDiagonalCornerBetweenPerpendicularBoundaries() {
        var east = new WorldAdjacencyBoundary(
            Center: new Vector3(x: 0f, y: 0f, z: -12f),
            OutwardYawDegrees: 90f,
            OutwardPitchDegrees: 0f,
            Width: 24f,
            Height: 16f).CompileFrame();
        var south = new WorldAdjacencyBoundary(
            Center: new Vector3(x: -12f, y: 0f, z: 0f),
            OutwardYawDegrees: 0f,
            OutwardPitchDegrees: 0f,
            Width: 24f,
            Height: 16f).CompileFrame();
        var hysteresis = FixedQ4816.FromDouble(value: 0.72);
        var from = Fixed(x: 0.7, y: 0.05, z: 0.7);
        var to = Fixed(x: 0.8, y: 0.05, z: 0.8);

        var eastCrossing = WorldAdjacencyRegion.Sweep(frame: east, from: from, to: to, outwardThreshold: hysteresis);
        var southCrossing = WorldAdjacencyRegion.Sweep(frame: south, from: from, to: to, outwardThreshold: hysteresis);

        Assert.True(eastCrossing.Crossed, userMessage: "the east ownership face left the expanded southeast corner unclaimed");
        Assert.True(southCrossing.Crossed, userMessage: "the south ownership face left the expanded southeast corner unclaimed");
        Assert.Equal(eastCrossing.Parameter, southCrossing.Parameter);
    }

    [Fact]
    public void FloorAdjacencyTransfersAtItsPlaneWithoutConsumingAscentHeadroom() {
        var up = Boundary(yaw: 0f, pitch: 90f).CompileFrame();
        var hysteresis = FixedQ4816.FromDouble(value: 0.72);
        var threshold = WorldAdjacencyPolicy.OwnershipThreshold(frame: in up, reciprocalHysteresis: hysteresis);

        var crossing = WorldAdjacencyRegion.Sweep(
            frame: up,
            from: Fixed(x: 0, y: -0.1, z: 0),
            to: Fixed(x: 0, y: 0.1, z: 0),
            outwardThreshold: threshold);

        Assert.Equal(FixedQ4816.Zero, threshold);
        Assert.True(crossing.Crossed);
        Assert.Equal(FixedQ4816.FromDouble(value: 0.5), crossing.Parameter);
    }

    [Fact]
    public void ReciprocalHysteresisCoversTwoBodyContactAndSkin() {
        var definition = Fixtures.BuildGradientUpDocument(gradientUp: true);

        Assert.True(WorldAdjacencyPolicy.TryReciprocalHysteresis(definition, out var depth, out var reason), reason);
        Assert.True(depth >= FixedQ4816.FromDouble(value: 0.72));
        Assert.True(depth < FixedQ4816.FromDouble(value: 0.721));
    }

    [Fact]
    public void ValidatorProvesReciprocalBoundaryAndRefusesDrift() {
        var (west, east) = Pair();
        var resolver = new Resolver(new Dictionary<string, WorldDefinition> { ["east.world.json"] = east, ["west.world.json"] = west });

        Assert.True(WorldDefinitionValidator.TryValidate(west, out var accepted, resolver), accepted);

        var drifted = east with {
            Adjacencies = [east.Adjacencies![0] with { Boundary = Boundary(yaw: -90f) with { Width = 7f } }],
        };
        var driftResolver = new Resolver(new Dictionary<string, WorldDefinition> { ["east.world.json"] = drifted, ["west.world.json"] = west });

        Assert.False(WorldDefinitionValidator.TryValidate(west, out var refused, driftResolver));
        Assert.Contains("but neighbour", refused, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatorRefusesAFramePairThatCannotPreserveBodyUp() {
        var (west, east) = Pair();
        var pitched = east with {
            Adjacencies = [east.Adjacencies![0] with { Boundary = Boundary(yaw: -90f, pitch: 90f) }],
        };
        var resolver = new Resolver(new Dictionary<string, WorldDefinition> { ["east.world.json"] = pitched, ["west.world.json"] = west });

        Assert.False(WorldDefinitionValidator.TryValidate(west, out var refused, resolver));
        Assert.Contains("do not preserve world up", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorRequiresTheDirectRouteForADerivedCornerPeer() {
        var (source, left, right, corner) = Corner();
        var resolver = new Resolver(new Dictionary<string, WorldDefinition> {
            ["left.world.json"] = left,
            ["right.world.json"] = right,
            ["corner.world.json"] = corner,
        });

        Assert.True(WorldDefinitionValidator.TryValidate(source, out var accepted, resolver), accepted);

        var missingRoute = source with {
            Destinations = source.Destinations!.Where(row => !string.Equals(row?.Name.Value, "corner", StringComparison.Ordinal)).ToArray(),
        };

        Assert.False(WorldDefinitionValidator.TryValidate(missingRoute, out var refused, resolver));
        Assert.Contains("derives corner neighbour 'corner.world.json'", refused, StringComparison.Ordinal);
        Assert.Contains("no global persisted destination/reference", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorRefusesACornerWhoseTwoTransformPathsDisagree() {
        var (source, left, right, corner) = Corner();
        var shiftedCorner = corner with {
            Adjacencies = [
                corner.Adjacencies![0],
                corner.Adjacencies[1] with {
                    Boundary = corner.Adjacencies[1]!.Boundary with { Center = new Vector3(x: 0.25f, y: 0f, z: 0f) },
                },
            ],
        };
        var resolver = new Resolver(new Dictionary<string, WorldDefinition> {
            ["left.world.json"] = left,
            ["right.world.json"] = right,
            ["corner.world.json"] = shiftedCorner,
        });

        Assert.False(WorldDefinitionValidator.TryValidate(source, out var refused, resolver));
        Assert.Contains("does not close its transform diamond", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableBindingMustNameADeclaredChannel() {
        var (west, east) = Pair();
        west = west with {
            Adjacencies = [west.Adjacencies![0] with { OnUnavailable = "missing-channel" }],
        };
        var resolver = new Resolver(new Dictionary<string, WorldDefinition> { ["east.world.json"] = east, ["west.world.json"] = west });

        Assert.False(WorldDefinitionValidator.TryValidate(west, out var refused, resolver));
        Assert.Contains("onUnavailable 'missing-channel' names no declared channel", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void FederatedEntityAddressesUseTheDeclaredAuthorityNamespace() {
        const string endpoint = "127.0.0.1:38601";
        var definition = Fixtures.BuildDocument() with { Host = Fixtures.BuildDocument().Host with { Authority = endpoint } };
        using var fixture = Fixtures.FreshServer(definition);

        Assert.Equal(endpoint, fixture.Server.AuthorityIdentity);
    }

    private static (WorldDefinition West, WorldDefinition East) Pair() {
        var west = Fixtures.BuildDocument() with {
            References = [new WorldReference(WorldSafeName.Parse("east-ref"), "east.world.json")],
            Destinations = [new WorldDestination(WorldSafeName.Parse("east"), "east-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global)],
            Adjacencies = [new WorldAdjacency(WorldSafeName.Parse("east-edge"), "east", "west-edge", Boundary(yaw: 90f))],
        };
        var east = Fixtures.BuildDocument() with {
            References = [new WorldReference(WorldSafeName.Parse("west-ref"), "west.world.json")],
            Destinations = [new WorldDestination(WorldSafeName.Parse("west"), "west-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global)],
            Adjacencies = [new WorldAdjacency(WorldSafeName.Parse("west-edge"), "west", "east-edge", Boundary(yaw: -90f))],
        };
        return (west, east);
    }

    private static (WorldDefinition Source, WorldDefinition Left, WorldDefinition Right, WorldDefinition Corner) Corner() {
        var source = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("left-ref"), "left.world.json"),
                new WorldReference(WorldSafeName.Parse("right-ref"), "right.world.json"),
                new WorldReference(WorldSafeName.Parse("corner-ref"), "corner.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("left"), "left-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("right"), "right-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("corner"), "corner-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("left-edge"), "left", "source-edge", Boundary(yaw: 90f)),
                new WorldAdjacency(WorldSafeName.Parse("right-edge"), "right", "source-edge", Boundary(yaw: 0f)),
            ],
        };
        var left = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("source-ref"), "source.world.json"),
                new WorldReference(WorldSafeName.Parse("corner-ref"), "corner.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("source"), "source-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("corner"), "corner-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("source-edge"), "source", "left-edge", Boundary(yaw: -90f)),
                new WorldAdjacency(WorldSafeName.Parse("corner-edge"), "corner", "left-edge", Boundary(yaw: 0f)),
            ],
        };
        var right = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("source-ref"), "source.world.json"),
                new WorldReference(WorldSafeName.Parse("corner-ref"), "corner.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("source"), "source-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("corner"), "corner-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("source-edge"), "source", "right-edge", Boundary(yaw: 180f)),
                new WorldAdjacency(WorldSafeName.Parse("corner-edge"), "corner", "right-edge", Boundary(yaw: 90f)),
            ],
        };
        var corner = Fixtures.BuildDocument() with {
            References = [
                new WorldReference(WorldSafeName.Parse("left-ref"), "left.world.json"),
                new WorldReference(WorldSafeName.Parse("right-ref"), "right.world.json"),
            ],
            Destinations = [
                new WorldDestination(WorldSafeName.Parse("left"), "left-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
                new WorldDestination(WorldSafeName.Parse("right"), "right-ref", WorldDestinationDurability.Persisted, WorldDestinationScope.Global),
            ],
            Adjacencies = [
                new WorldAdjacency(WorldSafeName.Parse("left-edge"), "left", "corner-edge", Boundary(yaw: 180f)),
                new WorldAdjacency(WorldSafeName.Parse("right-edge"), "right", "corner-edge", Boundary(yaw: -90f)),
            ],
        };
        return (source, left, right, corner);
    }

    private static WorldAdjacencyBoundary Boundary(float yaw, float pitch = 0f) => new(Center: Vector3.Zero, OutwardYawDegrees: yaw, OutwardPitchDegrees: pitch, Width: 8f, Height: 8f);

    private static FixedVector3 Fixed(double x, double y, double z) => new(FixedQ4816.FromDouble(x), FixedQ4816.FromDouble(y), FixedQ4816.FromDouble(z));

    private sealed class Resolver(IReadOnlyDictionary<string, WorldDefinition> definitions) : IWorldNeighbourResolver {
        public WorldNeighbourResolution Resolve(string document) => definitions.TryGetValue(document, out var definition)
            ? WorldNeighbourResolution.Resolved(definition)
            : WorldNeighbourResolution.Unavailable($"no '{document}'");
    }
}
