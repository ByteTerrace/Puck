using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for producers driving kits whose human controls are resolved into world-frame axes.</summary>
public sealed class WorldFrameProducerLawTests {
    [Fact]
    public void WanderProducer_SteersInTwoAxesUnderWorldFrameMotion() {
        var document = Fixtures.BuildDocument();
        var sourceKit = Assert.Single(collection: document.Kits);
        var sourceMotion = Assert.IsType<WorldMotionModel.Grounded>(@object: sourceKit.Motion);
        var scalars = Fixtures.TravelerWanderParameters.Scalars.ToDictionary();
        scalars = new Dictionary<string, float>(dictionary: scalars) {
            ["forward"] = 1f,
            ["softRadius"] = 0.1f,
            ["weaveAmplitude"] = 0f,
            ["inwardGain"] = 8f,
            ["turnScale"] = 1f,
        };
        var kit = sourceKit with {
            Motion = sourceMotion with { MoveFrame = MotionMoveFrame.World, FacingSnap = true },
            Producers = new Dictionary<string, BodyProgramParameters> {
                ["wander"] = Fixtures.TravelerWanderParameters with { Scalars = scalars },
            },
        };
        var population = document.Population with {
            Capacity = (WorldPopulation.LocalSeatCount + 1),
            NetworkPlayers = 1,
            DefaultPeerSource = IntentSource.Producer(name: "wander"),
            Distribution = new WorldDistribution(
                Region: new WorldDistributionRegion.Points(Names: ["seat-2"], HalfExtent: 0f),
                Fill: new WorldSequence(Name: WorldSequence.R2, Offset: 0, Step: 0f)),
        };

        using var fixture = Fixtures.FreshServer(definition: document with { Kits = [kit], Population = population });
        Assert.Equal(expected: 1, actual: fixture.Server.Population.SetSimulatedCount(count: 1));
        var body = fixture.Server.Population.EntryBody(index: WorldPopulation.LocalSeatCount)!;
        var start = body.FixedPosition;

        for (var tick = 0; tick < 240; tick++) {
            fixture.Step();
        }

        Assert.True(condition: body.FixedPosition.X < (start.X - FixedQ4816.FromDouble(value: 0.5)),
            userMessage: $"world-frame wander never acquired lateral steering: start={start}, end={body.FixedPosition}");
        Assert.NotEqual(expected: FixedQ4816.Zero, actual: body.FixedYaw);
    }
}
