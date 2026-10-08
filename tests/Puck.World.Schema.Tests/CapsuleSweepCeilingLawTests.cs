using Puck.Physics;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a kit's capsule collider is admitted only when a moving body's certified sweep can cover its
/// core, at most <see cref="FixedFieldContactSolver.MaximumCapsuleSweepPieces"/> core spheres, at every orientation.
/// Validation refuses a longer core in radii by name, so no admitted document reaches a tick with a core its sweep
/// cannot run, where the piece count once overflowed an int and threw.
/// </summary>
public sealed class CapsuleSweepCeilingLawTests {
    private static WorldDefinition WithSeatCapsule(float length, float radius) {
        var source = Fixtures.BuildDocument();

        return (source with {
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with {
                Collider = new WorldCollider.Capsule(Endpoint: new(x: 0f, y: length, z: 0f), Radius: radius),
            })],
        });
    }

    [Fact]
    public void ACapsuleWhoseCoreNeedsMoreSweepPiecesThanTheCeilingIsRefusedByName() {
        // A ten-thousand-unit core at a hundredth of a unit's radius: about a million pieces, and a radius of 1e-5
        // with a 1e5-unit core passes an int's worth.
        foreach (var (length, radius) in ((ReadOnlySpan<(float, float)>)[(10_000f, 0.01f), (100_000f, 0.00001f), (22.1f, 0.35f)])) {
            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: WithSeatCapsule(length: length, radius: radius), reason: out var reason), userMessage: $"a {length}-unit core at radius {radius} was admitted");
            Assert.Contains(actualString: reason, expectedSubstring: $"needs more than the {FixedFieldContactSolver.MaximumCapsuleSweepPieces} sweep pieces");
        }
    }
    [Fact]
    public void AColliderRadiusUnderTwoRawsIsRefusedByNameAndTwoRawsAreAdmitted() {
        // 1e-6 units quantizes to no raw at all: the core spheres a moving body's sweep moves would be points, and a
        // capsule's core would sweep its lower end alone. Two raws, 2⁻¹⁵ units, is the least radius whose half is a raw.
        var source = Fixtures.BuildDocument();

        WorldDefinition WithSeatCollider(WorldCollider collider) => (source with {
            KitRowsRaw = [.. source.Kits.Select(selector: kit => kit with { Collider = collider })],
        });
        var least = (FixedFieldContactSolver.MinimumColliderRadiusRaws / 65536f);

        foreach (var collider in ((WorldCollider[])[new WorldCollider.Sphere(Radius: 1e-6f), new WorldCollider.Capsule(Endpoint: new(x: 0f, y: 0.001f, z: 0f), Radius: 1e-6f), new WorldCollider.Box(HalfExtents: new(x: 1f, y: 1e-6f, z: 1f), Rotation: new(w: 1f, x: 0f, y: 0f, z: 0f))])) {
            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: WithSeatCollider(collider: collider), reason: out var reason), userMessage: $"{collider} was admitted");
            Assert.Contains(actualString: reason, expectedSubstring: $"under the {FixedFieldContactSolver.MinimumColliderRadiusRaws} raws");
        }

        foreach (var collider in ((WorldCollider[])[new WorldCollider.Sphere(Radius: least), new WorldCollider.Capsule(Endpoint: new(x: 0f, y: (100f / 65536f), z: 0f), Radius: least)])) {
            Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: WithSeatCollider(collider: collider), reason: out var reason), userMessage: reason);
        }
    }
    [Fact]
    public void ACapsuleAtTheCeilingIsAdmitted() {
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: WithSeatCapsule(length: 22f, radius: 0.35f), reason: out var reason), userMessage: reason);
    }
}
