using System.Numerics;
using Xunit;

namespace Puck.World.Tests;

public sealed class AuthoredAdjacencyHysteresisLawTests {
    [Fact]
    public void ReciprocalRowsMustDeclareTheSameAuthoredDeadband() {
        var left = Document(counterpart: "west", destination: "right", edge: "east", hysteresis: 2f, path: "right.world.json", yaw: 0f);
        var right = Document(counterpart: "east", destination: "left", edge: "west", hysteresis: 1f, path: "left.world.json", yaw: 180f);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(left, out var reason, new Resolver(definition: right, path: "right.world.json")));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "reciprocal rows must agree");
    }

    private static WorldDefinition Document(string destination, string path, string edge, string counterpart, float hysteresis, float yaw) => Fixtures.BuildDocument() with {
        References = [new WorldReference(SafeName.Parse(candidate: destination), path)],
        Destinations = [new WorldDestination(SafeName.Parse(candidate: destination), destination, WorldDestinationDurability.Persisted)],
        Adjacencies = [new WorldAdjacency(SafeName.Parse(candidate: edge), destination, counterpart, Boundary(yaw: yaw), Hysteresis: hysteresis)],
    };
    private static WorldAdjacencyBoundary Boundary(float yaw) => new(Vector3.Zero, yaw, 0f, 8f, 8f);

    private sealed class Resolver(string path, WorldDefinition definition) : IWorldNeighbourResolver {
        public WorldNeighbourResolution Resolve(string document) => (string.Equals(a: document, b: path, comparisonType: StringComparison.Ordinal)
            ? WorldNeighbourResolution.Resolved(definition)
            : WorldNeighbourResolution.Unavailable(reason: "not found"));
    }
}
