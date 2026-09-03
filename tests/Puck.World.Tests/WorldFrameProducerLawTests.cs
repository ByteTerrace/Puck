using Puck.Maths;
using Puck.World.Protocol;

using Xunit;
using Puck.Physics.Motion;

namespace Puck.World.Tests;

/// <summary>Laws for producers driving kits whose human controls are resolved into world-frame axes.</summary>
public sealed class WorldFrameProducerLawTests {
    [Fact]
    public void WanderProducer_SteersInTwoAxesUnderWorldFrameMotion() {
        var document = Fixtures.BuildDocument();
        var sourceKit = Assert.Single(collection: document.Kits);
        var sourceMotion = sourceKit.Motion;
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
            ProducersRaw = new Dictionary<string, BodyProgramParameters> {
                ["wander"] = Fixtures.TravelerWanderParameters with { Scalars = scalars },
            },
        };
        var population = document.Population with {
            CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1),
            NetworkPlayers = 1,
            DefaultPeerSourceRaw = IntentSource.Producer(name: "wander"),
            DistributionRaw = new WorldDistribution(
                Region: new WorldDistributionRegion.Points(HalfExtent: 0f, Names: ["seat-2"]),
                Fill: new WorldSequence(Name: WorldSequence.R2, Offset: 0, Step: 0f)),
        };

        using var fixture = Fixtures.FreshServer(definition: document with { KitRowsRaw = [kit], PopulationRaw = population });

        Assert.Equal(expected: 1, actual: fixture.Server.Population.SetSimulatedCount(count: 1));
        var body = fixture.Server.Population.EntryBody(index: WorldBodiesLimits.LocalSeatCount)!;
        var start = body.FixedPosition;

        for (var tick = 0; (tick < 240); tick++) {
            fixture.Step();
        }

        // The claim is that world-frame wander steers in TWO axes at all: a producer whose steering decision never
        // reached the body could only march along -Z, leaving X exactly where it started. The direction it turns is
        // not the claim — the inward pull now measures against the body's own home (its spawn point), not the world
        // origin, so which way it arcs off is a function of that home rather than of the origin's bearing.
        Assert.True(condition: (FixedQ4816.Abs(value: (body.FixedPosition.X - start.X)) > FixedQ4816.FromDouble(value: 0.5)),
            userMessage: $"world-frame wander never acquired lateral steering: start={start}, end={body.FixedPosition}");
        Assert.NotEqual(expected: FixedQ4816.Zero, actual: body.FixedYaw);
        // And the home it steers against is its own spawn, never the origin.
        Assert.Equal(expected: start, actual: body.FixedHome);
    }
}
