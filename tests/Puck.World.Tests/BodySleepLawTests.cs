using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A non-seat body's <c>bodies.sleepAfterTicks</c> idle floor: falls asleep once its program has produced
/// no motion and received no intent for the authored ticks, and wakes on either an adopted intent or a contact-field
/// version bump.</summary>
public sealed class BodySleepLawTests {
    // Comfortably above one fixture Step's own engine-tick width (240 Hz => 210 engine ticks/step), so a body that
    // wakes for one tick genuinely stays awake that tick rather than crossing straight back over the floor from the
    // single step's own stepTicks alone.
    private const int SleepAfterTicks = 1000;

    // Zero gravity on the fixture kit's one Free hold — Fixtures.BuildDocument's kit carries no collider and no
    // ground plane, so a body under any nonzero fall rate would never stop integrating downward and could never
    // qualify as "produced no motion" no matter how long it idles.
    private static WorldDefinition Document() {
        var definition = Fixtures.BuildDocument();
        var kit = definition.Kits[0];
        var motion = (kit.Motion with {
            Holds = [kit.Motion.Holds![0] with {
                Gravity = new WorldHoldGravity(Fall: 0.0001f, Rise: 0.0001f),
            }],
        });

        return definition with {
            KitRowsRaw = [kit with { Motion = motion }],
            PopulationRaw = definition.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1),
                NetworkPlayers = 1,
                SleepAfterTicks = SleepAfterTicks,
            },
        };
    }

    [Fact]
    public void RestingBodySleepsAfterAuthoredTicksAndWakesOnIntent() {
        using var fixture = Fixtures.FreshServer(Document());
        var population = fixture.Server.Population;

        Assert.Equal(expected: 1, actual: population.SetSimulatedCount(count: 1));

        var index = WorldBodiesLimits.LocalSeatCount;
        var body = population.EntryBody(index: index)!;

        Assert.False(condition: body.Asleep);

        for (var tick = 0; (tick < 5); tick++) {
            fixture.Step();
        }

        Assert.True(condition: body.Asleep);
        Assert.Equal(expected: 1, actual: population.SleepingCount);

        // A live source is required for a submitted intent to be adopted rather than masked by Idle (see
        // WorldBody.SubmitIntent's own remarks) — this proves the wake, not merely that the flag was set.
        body.SetIntentSource(source: IntentSource.Live);
        body.SubmitIntent(intent: new PlayerIntent(Channels: default).WithChannel(ordinal: 0, value: FixedQ4816.One));
        fixture.Step();

        Assert.False(condition: body.Asleep);
    }

    [Fact]
    public void RestingBodyWakesOnContactFieldVersionBump() {
        using var fixture = Fixtures.FreshServer(Document());
        var population = fixture.Server.Population;

        Assert.Equal(expected: 1, actual: population.SetSimulatedCount(count: 1));

        var index = WorldBodiesLimits.LocalSeatCount;
        var body = population.EntryBody(index: index)!;

        for (var tick = 0; (tick < 5); tick++) {
            fixture.Step();
        }

        Assert.True(condition: body.Asleep);

        var versionBeforeBump = population.ContactFieldVersion;

        // A no-op collision retune still recompiles the analytic contact field — an install, by this population's
        // own ComposeContactField contract, regardless of whether the compiled colliders differ.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetCollision(
            Collision: fixture.Server.Definition.Collision,
            Principal: WorldPrincipal.Console
        ));
        fixture.Step();

        Assert.NotEqual(expected: versionBeforeBump, actual: population.ContactFieldVersion);
        Assert.False(condition: body.Asleep);
    }
}
