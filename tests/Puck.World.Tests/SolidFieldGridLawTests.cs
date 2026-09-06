using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The solid field's distance grid against the shipped world: a world authoring <c>collision.gridCellSize</c> reads
/// its solids through a grid whose answers are the exact program's inside the contact band and for every gradient
/// and overlap, casts agree on hit or miss, the bake hash names the bake, and a collision edit that keeps the cell
/// size keeps the grid.
/// </summary>
public sealed class SolidFieldGridLawTests {
    private static readonly FixedVector3[] Directions = [
        Vector(x: 1.0, y: 0.0, z: 0.0),
        Vector(x: -1.0, y: 0.0, z: 0.0),
        Vector(x: 0.0, y: -1.0, z: 0.0),
        Vector(x: 0.0, y: 0.0, z: 1.0),
        Vector(x: 0.0, y: 0.0, z: -1.0),
        Vector(x: 1.0, y: -0.5, z: -1.0),
    ];

    private static WorldSolidField Build(WorldDefinition definition) {
        Assert.True(condition: WorldSolidField.TryBuild(
            built: out var field,
            definition: definition,
            reason: out var reason
        ), userMessage: reason);

        return field!;
    }
    private static WorldDefinition Shipped(float gridCellSize) =>
        (AuthoredGameFixtures.Nexus with {
            CollisionRaw = (AuthoredGameFixtures.Nexus.Collision with { GridCellSize = gridCellSize }),
        });
    // The deck and the air above it, sampled every two units and off every grid corner.
    private static IEnumerable<FixedPosition> Lattice() {
        for (var x = -23.7; (x <= 23.7); x += 2.0) {
            for (var y = -1.3; (y <= 6.7); y += 2.0) {
                for (var z = -23.1; (z <= 23.1); z += 2.0) {
                    yield return FixedPosition.FromLocal(local: Vector(
                        x: x,
                        y: y,
                        z: z
                    ));
                }
            }
        }
    }
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );

    [Fact]
    public void TheShippedWorldBakesAGridWhoseSurfaceIsExactInsideTheBand() {
        var gridded = Build(definition: Shipped(gridCellSize: 0.5f));
        var exact = Build(definition: Shipped(gridCellSize: 0f));

        Assert.Null(@object: exact.Grid);
        Assert.NotNull(@object: gridded.Grid);
        Assert.Equal(expected: 0.5, actual: ((double)gridded.Grid.CellSize));
        // The walker's capsule radius at the scale row's ceiling of 1, plus the contact skin, plus the grid's slack.
        Assert.True(condition: (((double)gridded.ContactBand) > (0.35 + 0.02)), userMessage: $"band {gridded.ContactBand}");

        var inBand = 0;
        var beyond = 0;
        var casts = 0;
        var maxDistance = FixedQ4816.FromInteger(value: 4L);
        var radius = FixedQ4816.FromDouble(value: 0.2);

        foreach (var position in Lattice()) {
            Assert.True(condition: exact.Evaluator.TryDistance(distance: out var expectedDistance, material: out var expectedMaterial, position: position));
            Assert.True(condition: gridded.Evaluator.TryDistance(distance: out var actualDistance, material: out var actualMaterial, position: position));

            if (expectedDistance < gridded.ContactBand) {
                inBand++;
                Assert.Equal(expected: expectedDistance.Value, actual: actualDistance.Value);
                Assert.Equal(expected: expectedMaterial, actual: actualMaterial);
            } else {
                beyond++;
                Assert.True(condition: (actualDistance <= expectedDistance));
                Assert.True(condition: (actualDistance >= gridded.ContactBand));
            }

            Assert.Equal(
                expected: exact.Evaluator.TryFieldGradient(gradient: out var expectedGradient, position: position),
                actual: gridded.Evaluator.TryFieldGradient(gradient: out var actualGradient, position: position)
            );
            Assert.Equal(expected: expectedGradient, actual: actualGradient);
            Assert.Equal(expected: exact.Query.Overlap(center: position, radius: radius), actual: gridded.Query.Overlap(center: position, radius: radius));

            foreach (var direction in Directions) {
                casts++;
                Assert.Equal(
                    expected: exact.Query.SphereCast(dir: direction, hit: out var expectedHit, maxDist: maxDistance, origin: position, radius: radius),
                    actual: gridded.Query.SphereCast(dir: direction, hit: out var actualHit, maxDist: maxDistance, origin: position, radius: radius)
                );
                Assert.Equal(expected: expectedHit.Confidence, actual: actualHit.Confidence);
            }
        }

        Assert.True(condition: (inBand > 100), userMessage: $"only {inBand} lattice points fell inside the band");
        Assert.True(condition: (beyond > 100), userMessage: $"only {beyond} lattice points fell beyond the band");
        Assert.True(condition: (casts > 2_000));
        Assert.True(condition: (gridded.Grid.BakedCornerCount > 0L));
    }

    [Fact]
    public void TheBakeHashNamesTheBakeAndACollisionEditKeepsTheGrid() {
        var half = Build(definition: Shipped(gridCellSize: 0.5f));
        var halfAgain = Build(definition: Shipped(gridCellSize: 0.5f));
        var whole = Build(definition: Shipped(gridCellSize: 1f));
        var none = Build(definition: Shipped(gridCellSize: 0f));

        Assert.NotEqual(expected: 0UL, actual: half.Census.SolidBakeHash);
        Assert.Equal(expected: half.Census.SolidBakeHash, actual: halfAgain.Census.SolidBakeHash);
        Assert.NotEqual(expected: half.Census.SolidBakeHash, actual: whole.Census.SolidBakeHash);
        Assert.Equal(expected: 0UL, actual: none.Census.SolidBakeHash);

        using var fixture = Fixtures.FreshServer(definition: Shipped(gridCellSize: 0.5f));
        var before = fixture.Server.SolidField!.Grid;

        Assert.NotNull(@object: before);

        var collision = fixture.Server.Definition.Collision;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetCollision(Principal: WorldPrincipal.Console, Collision: (collision with { ContactSkin = 0.03f })));
        fixture.Step();
        Assert.Same(expected: before, actual: fixture.Server.SolidField!.Grid);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetCollision(Principal: WorldPrincipal.Console, Collision: (collision with { GridCellSize = 1f })));
        fixture.Step();
        Assert.NotSame(expected: before, actual: fixture.Server.SolidField!.Grid);
        Assert.Equal(expected: 1.0, actual: ((double)fixture.Server.SolidField!.Grid!.CellSize));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetCollision(Principal: WorldPrincipal.Console, Collision: (collision with { GridCellSize = 0f })));
        fixture.Step();
        Assert.Null(@object: fixture.Server.SolidField!.Grid);
    }

    // Two boots of the shipped world under no input reach the same authoritative hash at the same ticks: the grid's
    // bake and every bound it answers are pure functions of the program, so the mapping reproduces.
    [Fact]
    public void TheShippedWorldReproducesItsAuthoritativeHashTraceAcrossBoots() {
        ulong[] Trace() {
            using var fixture = Fixtures.FreshServer(definition: Shipped(gridCellSize: 0.5f));
            var stepTicks = EngineTicks.PerRate(ratePerSecond: ((uint)fixture.Server.Definition.SimulationRateHz));
            var trace = new ulong[3];

            for (var tick = 1; (tick <= 90); tick++) {
                fixture.Step(stepTicks: stepTicks);

                if ((tick % 30) == 0) {
                    trace[((tick / 30) - 1)] = WorldRuntimeStateHash.HashAuthoritative(server: fixture.Server, tick: ((ulong)tick));
                }
            }

            return trace;
        }

        Assert.Equal(expected: Trace(), actual: Trace());
    }

    [Fact]
    public void TheCellSizeIsValidatedByName() {
        var tooSmall = Shipped(gridCellSize: 0.01f);
        var tooLarge = Shipped(gridCellSize: 100f);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: tooSmall, reason: out var reason));
        Assert.Contains(expectedSubstring: "collision.gridCellSize", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: tooLarge, reason: out reason));
        Assert.Contains(expectedSubstring: "collision.gridCellSize", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: Shipped(gridCellSize: 0f), reason: out reason), userMessage: reason);
    }
}
