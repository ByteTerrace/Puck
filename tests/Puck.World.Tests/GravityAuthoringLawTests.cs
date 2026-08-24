using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the document-to-fixed point/planet gravity authoring seam.</summary>
public sealed class GravityAuthoringLawTests {
    private static WorldDefinition WithGravity(WorldGravity gravity) => Fixtures.BuildGradientUpDocument(gradientUp: false) with {
        GravityRaw = gravity,
    };
    private static WorldGravity PointGravity(
        float gravitationalConstant = 45f,
        IReadOnlyList<WorldGravityAttractor>? attractors = null,
        IReadOnlyList<WorldGravityPoint>? points = null,
        DocumentVector3? uniform = null
    ) => new(
        Attractors: (attractors ?? []),
        GravitationalConstant: gravitationalConstant,
        Points: points,
        SofteningLength: 0.5f,
        Solver: WorldGravitySolver.Pairwise,
        Uniform: uniform
    );

    [Fact]
    public void PointPreset_LowersThroughTheSoftenedKernelToItsSurfacePromise() {
        var definition = WithGravity(gravity: PointGravity(points: [
            new WorldGravityPoint(PlacementId: "ball", SurfaceGravity: 9.81f, ReferenceRadius: 100f),
        ]));

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason),
            userMessage: reason
        );

        var compiled = FixedWorldGravity.Compile(
            gravity: definition.Gravity,
            placements: definition.Placements
        );
        var field = new WorldGravityField(
            capacity: 1,
            compiled: compiled
        );

        field.Solve(targets: [new WorldGravityTarget(
            EntityIndex: 0,
            Mass: FixedQ4816.Zero,
            Position: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 100),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            )
        )]);

        Assert.True(condition: field.TryAcceleration(entityIndex: 0, acceleration: out var acceleration));
        Assert.InRange(
            actual: -((double)acceleration.X),
            low: 9.80,
            high: 9.82
        );
        Assert.Equal(expected: FixedQ4816.Zero, actual: acceleration.Y);
        Assert.Equal(expected: FixedQ4816.Zero, actual: acceleration.Z);
    }

    [Fact]
    public void PointPreset_RequiresPositiveG_WhileUniformOnlyDoesNot() {
        var point = WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            points: [new WorldGravityPoint(PlacementId: "ball", SurfaceGravity: 9.81f, ReferenceRadius: 100f)]
        ));
        var uniform = WithGravity(gravity: PointGravity(
            gravitationalConstant: 0f,
            uniform: new DocumentVector3(value: new Vector3(x: 0f, y: -9.81f, z: 0f))
        ));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: point, reason: out var pointReason));
        Assert.Contains(
            expectedSubstring: "gravity.gravitationalConstant must be positive when gravity.points declares a source",
            actualString: pointReason,
            comparisonType: StringComparison.Ordinal
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: uniform, reason: out var uniformReason),
            userMessage: uniformReason
        );

        var compiled = FixedWorldGravity.Compile(
            gravity: uniform.Gravity,
            placements: uniform.Placements
        );

        Assert.Empty(collection: compiled.Attractors);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: -9.81), actual: compiled.Uniform.Y);
    }

    [Fact]
    public void APlacementMayNotBeCountedByBothSourceSpellings() {
        var denied = WithGravity(gravity: PointGravity(
            attractors: [new WorldGravityAttractor(PlacementId: "ball", Mass: 10f)],
            points: [new WorldGravityPoint(PlacementId: "ball", SurfaceGravity: 9.81f, ReferenceRadius: 100f)]
        ));
        var control = WithGravity(gravity: PointGravity(
            points: [new WorldGravityPoint(PlacementId: "ball", SurfaceGravity: 9.81f, ReferenceRadius: 100f)]
        ));

        Laws.RefusalWithControl(
            lawId: "gravity.point.duplicate-placement",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out _),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out _)
        );
        _ = WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason);
        Assert.Contains(
            expectedSubstring: "gravity.points[0].placementId duplicates gravity source 'ball'",
            actualString: reason,
            comparisonType: StringComparison.Ordinal
        );
    }

    [Fact]
    public void PointPreset_UnrepresentableLoweringRefusesBeforeRuntimeCompilation() {
        var denied = WithGravity(gravity: PointGravity(points: [
            new WorldGravityPoint(PlacementId: "ball", SurfaceGravity: float.MaxValue, ReferenceRadius: float.MaxValue),
        ]));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(
            expectedSubstring: "gravity.points[0] cannot lower",
            actualString: reason,
            comparisonType: StringComparison.Ordinal
        );
    }

    [Fact]
    public void LegacyMassSources_CompileUnchangedWhenPointsAreAbsent() {
        var definition = WithGravity(gravity: PointGravity(
            attractors: [new WorldGravityAttractor(PlacementId: "ball", Mass: 10f)]
        ));

        var compiled = FixedWorldGravity.Compile(
            gravity: definition.Gravity,
            placements: definition.Placements
        );

        var source = Assert.Single(collection: compiled.Attractors);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 10), actual: source.Mass);
        Assert.Equal(expected: FixedVector3.Zero, actual: source.Position);
    }

    [Fact]
    public void PointPreset_RoundTripsAsAuthoredQuantities() {
        var definition = WithGravity(gravity: PointGravity(points: [
            new WorldGravityPoint(PlacementId: "ball", SurfaceGravity: 9.81f, ReferenceRadius: 100f),
        ]));

        var roundTrip = WorldDefinitionSerialization.Deserialize(
            utf8Json: WorldDefinitionSerialization.Serialize(definition: definition)
        );
        var point = Assert.Single(collection: roundTrip.Gravity.Points!);

        Assert.Equal(expected: "ball", actual: point.PlacementId);
        Assert.Equal(expected: 9.81f, actual: point.SurfaceGravity);
        Assert.Equal(expected: 100f, actual: point.ReferenceRadius);
    }

    [Fact]
    public void NullPointRow_RefusesByIndexedPath() {
        var definition = WithGravity(gravity: PointGravity(points: [
            new WorldGravityPoint(PlacementId: "ball", SurfaceGravity: 9.81f, ReferenceRadius: 100f),
        ]));
        var node = JsonNode.Parse(
            json: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition))
        )!.AsObject();

        node["gravity"]!["points"]!.AsArray()[0] = null;

        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(
            utf8Json: Encoding.UTF8.GetBytes(s: node.ToJsonString())
        ));

        Assert.Contains(
            expectedSubstring: "gravity.points[0] is required",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
}
