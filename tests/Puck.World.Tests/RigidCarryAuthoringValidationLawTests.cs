using System.Numerics;

using Puck.Assets.Documents;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the rigid/carry facets' fixed-point representation boundary: authoring that passes the semantic
/// sign/range checks must also compile without saturation or wrap before a world is admitted.</summary>
public sealed class RigidCarryAuthoringValidationLawTests {
    private static WorldDefinition WithRigid(WorldRigid rigid, WorldCollider? collider = null) {
        var source = Fixtures.BuildDocument();

        return source with {
            KitRowsRaw = [source.Kits[0] with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = (collider ?? new WorldCollider.Sphere(Radius: 0.4f)),
                Rigid = rigid,
            }],
        };
    }
    private static WorldDefinition WithCarry(WorldCarry carry) {
        var source = Fixtures.BuildDocument();

        return source with {
            KitRowsRaw = [source.Kits[0] with { Carry = carry }],
        };
    }

    [Theory]
    [InlineData(float.Epsilon)]
    [InlineData(float.MaxValue)]
    public void RigidMassOutsideCompiledMassScaleRefusesWhileOrdinaryMassPasses(float mass) {
        var denied = WithRigid(rigid: new WorldRigid(Mass: mass));
        var admitted = WithRigid(rigid: new WorldRigid(Mass: 1f));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var denial));
        Assert.Contains(actualString: denial, comparisonType: StringComparison.Ordinal, expectedSubstring: "cannot compile deterministic mass properties");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Theory]
    [InlineData(float.Epsilon)]
    [InlineData(float.MaxValue)]
    public void RigidPrimitiveOutsideFixedGeometryScaleRefusesWhileOrdinaryPrimitivePasses(float radius) {
        var denied = WithRigid(
            rigid: new WorldRigid(Mass: 1f),
            collider: new WorldCollider.Sphere(Radius: radius)
        );
        var admitted = WithRigid(rigid: new WorldRigid(Mass: 1f));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var denial));
        Assert.Contains(actualString: denial, comparisonType: StringComparison.Ordinal, expectedSubstring: "cannot compile deterministic mass properties");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void CarryDerivedMassOverflowRefusesWhileRepresentableProductPasses() {
        var denied = WithCarry(carry: new WorldCarry(
            Offset: new DocumentVector3(value: Vector3.Zero),
            MassEquivalent: 100_000_000_000f,
            MaxCarryFraction: 100_000_000_000f,
            MaxReach: 1f
        ));
        var admitted = WithCarry(carry: new WorldCarry(
            Offset: new DocumentVector3(value: Vector3.Zero),
            MassEquivalent: 60f,
            MaxCarryFraction: 1f,
            MaxReach: 1.5f
        ));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var denial));
        Assert.Contains(actualString: denial, comparisonType: StringComparison.Ordinal, expectedSubstring: "cannot compile deterministically");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Theory]
    [InlineData(float.Epsilon, 1f)]
    [InlineData(1f, float.Epsilon)]
    public void CarryPositiveValuesThatQuantizeToZeroRefuseWhileOrdinaryValuesPass(float massEquivalent, float maxReach) {
        var denied = WithCarry(carry: new WorldCarry(
            Offset: new DocumentVector3(value: Vector3.Zero),
            MassEquivalent: massEquivalent,
            MaxCarryFraction: 1f,
            MaxReach: maxReach
        ));
        var admitted = WithCarry(carry: new WorldCarry(
            Offset: new DocumentVector3(value: Vector3.Zero),
            MassEquivalent: 60f,
            MaxCarryFraction: 1f,
            MaxReach: 1.5f
        ));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var denial));
        Assert.Contains(actualString: denial, comparisonType: StringComparison.Ordinal, expectedSubstring: "cannot compile deterministically");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }
}
