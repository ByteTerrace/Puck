using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A non-seat body's <c>bodies.sleepAfterSeconds</c> idle floor: falls asleep once its program has produced
/// no motion and received no intent for the authored ticks, and wakes on an adopted intent, a contact-field version
/// bump, or a designation targeting it.</summary>
public sealed class BodySleepLawTests {
    // Below one fixture Step's own engine-tick width, so a single idle step crosses the floor and the body is
    // asleep after it. A woken body therefore re-crosses the floor within the very step that woke it: an
    // observation of the wake reads the sleep latch's own tick (AsleepSinceTick), never a still-awake body.
    private const decimal SleepAfterSeconds = 0.0125m;

    private static WorldAuthorityCheckpoint RoundTrip(WorldAuthorityCheckpoint checkpoint) {
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint),
                checkpoint: out var decoded,
                reason: out var reason
            ),
            userMessage: reason
        );

        return decoded!;
    }
    // Zero gravity on the fixture kit's one Free hold — Fixtures.BuildDocument's kit carries no collider and no
    // ground plane, so a body under any nonzero fall rate would never stop integrating downward and could never
    // qualify as "produced no motion" no matter how long it idles.
    private static WorldDefinition Document(decimal sleepAfterSeconds = SleepAfterSeconds) {
        var definition = Fixtures.BuildDocument();
        var kit = definition.Kits[0];
        var motion = (kit.Motion with {
            Holds = [kit.Motion.Holds![0] with {
                Gravity = new WorldHoldGravity(
                Fall: 0.0001f,
                Rise: 0.0001f
            ),
            }],
        });

        return definition with {
            KitRowsRaw = [kit with { Motion = motion }],
            PopulationRaw = definition.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1),
                NetworkPlayers = 1,
                SleepAfterSeconds = sleepAfterSeconds,
            },
        };
    }

    [InlineData("0.0125", true)]
    [InlineData("0.001", false)]
    [InlineData("-1", false)]
    [Theory]
    public void TheIdleFloorIsAdmittedOnlyOnAWholeEngineTick(string seconds, bool admitted) {
        var definition = Document();

        definition = definition with {
            PopulationRaw = definition.Population with {
                SleepAfterSeconds = decimal.Parse(
                    provider: System.Globalization.CultureInfo.InvariantCulture,
                    s: seconds
                ),
            },
        };

        Assert.Equal(
            actual: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            expected: admitted
        );

        if (!admitted) {
            Assert.Contains(
                actualString: reason,
                expectedSubstring: "bodies.sleepAfterSeconds"
            );
        }
    }
    [Fact]
    public void RestingBodySleepsAfterAuthoredTicksAndWakesOnIntent() {
        using var fixture = Fixtures.FreshServer(Document());
        var population = fixture.Server.Population;

        Assert.Equal(
            expected: 1,
            actual: population.SetSimulatedCount(count: 1)
        );

        var index = WorldBodiesLimits.LocalSeatCount;
        var body = population.EntryBody(index: index)!;

        Assert.False(condition: body.Asleep);

        for (var tick = 0; (tick < 1); tick++) {
            fixture.Step();
        }

        Assert.True(condition: body.Asleep);
        Assert.Equal(
            expected: 1,
            actual: population.SleepingCount
        );

        // A live source is required for a submitted intent to be adopted rather than masked by Idle (see
        // WorldBody.SubmitIntent's own remarks) — this proves the wake, not merely that the flag was set.
        body.SetIntentSource(source: IntentSource.Live);
        body.SubmitIntent(intent: new PlayerIntent(Channels: default).WithChannel(
            ordinal: 0,
            value: FixedQ4816.One
        ));
        fixture.Step();

        Assert.False(condition: body.Asleep);
    }
    [Fact]
    public void RestingBodyWakesOnContactFieldVersionBump() {
        using var fixture = Fixtures.FreshServer(Document());
        var population = fixture.Server.Population;

        Assert.Equal(
            expected: 1,
            actual: population.SetSimulatedCount(count: 1)
        );

        var index = WorldBodiesLimits.LocalSeatCount;
        var body = population.EntryBody(index: index)!;

        for (var tick = 0; (tick < 1); tick++) {
            fixture.Step();
        }

        Assert.True(condition: body.Asleep);

        var sleptAt = body.AsleepSinceTick;
        var versionBeforeBump = population.ContactFieldVersion;

        // A no-op collision retune still recompiles the analytic contact field — an install, by this population's
        // own ComposeContactField contract, regardless of whether the compiled colliders differ.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetCollision(
            Collision: fixture.Server.Definition.Collision,
            Principal: Principal.Console
        ));
        fixture.Step();

        Assert.NotEqual(
            expected: versionBeforeBump,
            actual: population.ContactFieldVersion
        );

        // The bump woke it: only WorldBody.Advance's own sleep bookkeeping writes the latch, so a latch naming a
        // later tick than the one the body first slept at is proof the body advanced — motion program and contact
        // solve — under the reinstalled field.
        var wokeAt = body.AsleepSinceTick;

        Assert.True(
            (wokeAt > sleptAt),
            userMessage: $"the contact-field bump never woke the body; latch still names tick {sleptAt}"
        );

        // The control: exactly once. A further step with no install behind it leaves the latch where the wake put
        // it, so the assertion above reads a wake rather than a body that advances every tick regardless.
        fixture.Step();
        Assert.Equal(
            expected: wokeAt,
            actual: body.AsleepSinceTick
        );
    }
    [Fact]
    public void RestingBodyWakesOnDesignationTargetingIt() {
        var definition = (Document() with {
            TargetRegistersRaw = [new WorldTargetRegister(
                Name: "goal",
                MaximumRange: 100f,
                MaximumHalfAngleDegrees: 180f,
                RequiresLineOfSight: false
            )],
        });
        using var fixture = Fixtures.FreshServer(definition);
        var population = fixture.Server.Population;

        Assert.Equal(
            expected: 1,
            actual: population.SetSimulatedCount(count: 1)
        );

        var index = WorldBodiesLimits.LocalSeatCount;
        var body = population.EntryBody(index: index)!;

        for (var tick = 0; (tick < 1); tick++) {
            fixture.Step();
        }

        Assert.True(condition: body.Asleep);
        Assert.True(condition: population.TryResolveTargetRegister(
            index: out var registerIndex,
            name: "goal"
        ));

        population.SetDesignation(
            bodyIndex: index,
            registerIndex: registerIndex,
            target: WorldTargetDesignation.Body(index: 0)
        );

        Assert.False(condition: body.Asleep);
    }
    [Fact]
    public void CheckpointRestorePreservesTheSleepLatchAndItsPartialIdleFloor() {
        using var fixture = Fixtures.FreshServer(Document(sleepAfterSeconds: 0.05m));
        var population = fixture.Server.Population;

        Assert.Equal(
            expected: 1,
            actual: population.SetSimulatedCount(count: 1)
        );

        var index = WorldBodiesLimits.LocalSeatCount;

        fixture.Step();

        var before = population.Capture().Entries.Single().Residue;

        Assert.Equal(expected: 0UL, actual: before.AsleepSinceTick);
        Assert.True(condition: (before.SleepIdleTicks > 0UL));
        Assert.True(condition: before.ContactFieldObservationCurrent);
        Assert.Equal(expected: 0UL, actual: before.LastContactFieldVersion);
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var reason
            ),
            userMessage: reason
        );

        var firstBytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);
        var decoded = RoundTrip(checkpoint: checkpoint!);

        fixture.Server.RestoreCheckpoint(checkpoint: decoded);

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var recaptured,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: firstBytes,
            actual: WorldAuthorityCheckpointCodec.Encode(checkpoint: recaptured!)
        );

        var restoredPartial = fixture.Server.Population.Capture().Entries.Single().Residue;

        Assert.Equal(expected: before.SleepIdleTicks, actual: restoredPartial.SleepIdleTicks);
        Assert.Equal(expected: before.LastContactFieldVersion, actual: restoredPartial.LastContactFieldVersion);

        fixture.Step();

        var slept = fixture.Server.Body(index: index)!;

        Assert.True(condition: slept.Asleep);
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out reason
            ),
            userMessage: reason
        );

        fixture.Server.RestoreCheckpoint(checkpoint: RoundTrip(checkpoint: checkpoint!));

        var restoredAsleep = fixture.Server.Body(index: index)!;

        Assert.True(condition: restoredAsleep.Asleep);
        Assert.Equal(expected: slept.AsleepSinceTick, actual: restoredAsleep.AsleepSinceTick);
        Assert.Equal(expected: 1, actual: fixture.Server.Population.SleepingCount);
    }
    [Fact]
    public void CheckpointRestorePreservesAPendingContactFieldWake() {
        using var fixture = Fixtures.FreshServer(Document());
        var population = fixture.Server.Population;

        Assert.Equal(expected: 1, actual: population.SetSimulatedCount(count: 1));
        fixture.Step();

        var index = WorldBodiesLimits.LocalSeatCount;
        var body = fixture.Server.Body(index: index)!;
        var sleptAt = body.AsleepSinceTick;

        Assert.True(condition: body.Asleep);
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var reason
            ),
            userMessage: reason
        );

        // A field install can occur after this body's latest advance and before a settled capture. Its numeric
        // version is process-local, but the pending edge is continuation state and must survive reconstruction.
        var populationCheckpoint = checkpoint!.Population;
        var capturedEntry = populationCheckpoint.Entries.Single();
        var pendingEntry = capturedEntry with {
            Residue = capturedEntry.Residue with { ContactFieldObservationCurrent = false },
        };

        checkpoint = checkpoint with {
            Population = populationCheckpoint with { Entries = [pendingEntry] },
        };

        fixture.Server.RestoreCheckpoint(checkpoint: RoundTrip(checkpoint: checkpoint));
        fixture.Step();

        Assert.True(
            condition: (fixture.Server.Body(index: index)!.AsleepSinceTick > sleptAt),
            userMessage: "the pending contact-field edge was rebased away during restore"
        );
    }
}
