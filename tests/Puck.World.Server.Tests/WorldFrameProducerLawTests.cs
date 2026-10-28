using Puck.Maths;
using Puck.Commands;
using Puck.World.Protocol;

using Xunit;
using Puck.Physics.Motion;

namespace Puck.World.Server.Tests;

/// <summary>Laws for producers driving kits whose human controls are resolved into world-frame axes.</summary>
public sealed class WorldFrameProducerLawTests {
    [Fact]
    public void RoamProducer_SteersInTwoAxesUnderWorldFrameMotion() {
        var document = Fixtures.BuildDocument();
        var sourceKit = Assert.Single(collection: document.Kits);
        var sourceMotion = sourceKit.Motion;
        var scalars = Fixtures.TravelerRoamParameters.Scalars.ToDictionary();

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
                ["roam"] = Fixtures.TravelerRoamParameters with { Scalars = scalars },
            },
        };
        var population = document.Population with {
            CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1),
            NetworkPlayers = 1,
            DefaultPeerSourceRaw = IntentSource.Producer(name: "roam"),
            DistributionRaw = new WorldDistribution(
            Region: new WorldDistributionRegion.Points(
                HalfExtent: 0f,
                Names: ["seat-2"]
            ),
            Fill: new WorldSequence(
                Name: WorldSequence.R2,
                Offset: 0,
                Step: 0f
            )
        ),
        };

        var definition = document with { KitRowsRaw = [kit], PopulationRaw = population };
        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.Equal(
            expected: 1,
            actual: fixture.Server.Population.SetSimulatedCount(count: 1)
        );
        var body = fixture.Server.Population.EntryBody(index: WorldBodiesLimits.LocalSeatCount)!;
        var start = body.FixedPosition;

        for (var tick = 0; (tick < 30); tick++) {
            fixture.Step();
        }

        // The claim is that world-frame roam steers in TWO axes at all: a producer whose steering decision never
        // reached the body could only march along -Z, leaving X exactly where it started. The direction it turns is
        // not the claim — the inward pull now measures against the body's own home (its spawn point), not the world
        // origin, so which way it arcs off is a function of that home rather than of the origin's bearing.
        Assert.True(
            condition: (FixedQ4816.Abs(value: (body.FixedPosition.X - start.X)) > FixedQ4816.FromDouble(value: 0.5)),
            userMessage: $"world-frame roam never acquired lateral steering: start={start}, end={body.FixedPosition}"
        );
        Assert.NotEqual(
            expected: FixedQ4816.Zero,
            actual: body.FixedYaw
        );
        // And the home it steers against is its own spawn, never the origin.
        Assert.Equal(
            expected: start,
            actual: body.FixedHome
        );
        var participant = Assert.Single(collection: fixture.Server.Input.Capture().Owners);

        Assert.Equal(Principal.World, participant.Controller!.Value.Actor);
        Assert.Equal("roam", participant.Controller.Value.Source);
        Assert.Equal(PrincipalKind.Session, participant.Principal.Kind);
        Assert.Contains(collection: participant.Input.History, filter: record => (record.Event.Kind == WorldInputEventKind.AxisSample));
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!), out var decoded, out reason), userMessage: reason);
        using var restored = Fixtures.FreshServer(definition);

        restored.Server.RestoreCheckpoint(checkpoint: decoded!);
        fixture.Step();
        restored.Step();
        var sampledTick = fixture.Server.Input.Interval.Tick;

        Assert.Equal(WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: sampledTick),
            WorldStateHashComposition.HashAuthoritative(server: restored.Server, tick: sampledTick));
        body.SetIntentSource(source: IntentSource.Idle);
        fixture.Step();
        Assert.True(condition: fixture.Server.Input.TryGet(participant.Principal, out var input));
        Assert.Equal(default, input!.Image);
    }
}
