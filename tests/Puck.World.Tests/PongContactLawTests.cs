using System.Numerics;
using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

public sealed class PongContactLawTests {
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    public void ShippedBallEstablishesContactFromItsAuthoredSpawn(float height) {
        var definition = AuthoredGameFixtures.Load(relativePath: "tests/Puck.World.Tests/Fixtures/minimal-pong-host.world.json");
        definition = definition with {
            PlacementRowsRaw = [.. definition.Placements.Select(selector: row => ((row.Id == "pongBall")
                ? row with { Position = new Vector3(x: row.Position.X, y: height, z: row.Position.Z) }
                : row))],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var ordinal = definition.Placements.ToList().FindIndex(match: row => (row.Id == "pongBall"));
        Assert.True(condition: (ordinal >= 0));
        var ball = fixture.Server.Body(index: fixture.Server.Population.BodyForPlacementOrdinal(ordinal: ordinal))!;
        Assert.True(condition: ball.IsRigid);
        for (var tick = 0; (tick < 240); tick++) {
            fixture.Step();
            Assert.True(condition: (ball.FixedPosition.Y >= FixedQ4816.FromDouble(value: -0.001d)),
                userMessage: $"Spawn {height}, tick {tick}: position {ball.FixedPosition}, velocity {ball.RigidVelocity}");
        }
        Assert.True(condition: ball.Resting);
        Assert.True(condition: ball.Grounded);
    }
}
