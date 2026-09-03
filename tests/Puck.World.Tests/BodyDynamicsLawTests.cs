using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins a kit's <c>dynamics</c>-row planar shaping against the response table's own contract: critical
/// damping rises monotonically to the resolved move speed and coasts back down monotonically on release, never
/// overshoots the speed envelope, and a follower's coefficients are bound to the world's own simulation rate.</summary>
public sealed class BodyDynamicsLawTests {
    private const int ForwardOrdinal = 0;

    private static WorldDefinition WithKitDynamics(string dynamicsRow, float damping) {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];
        var grounded = kit.Motion;

        return document with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, new WorldDynamicsRow(Damping: damping, Frequency: 2f, Name: dynamicsRow, Response: 0f)],
            KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = dynamicsRow } }],
        };
    }
    private static WorldBody JoinBody(WorldFixture fixture) {
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        return fixture.Server.Body(index: actor.Index)!;
    }

    [Fact]
    public void CriticalDamping_RisesMonotonicallyToMoveSpeed_ThenDecaysMonotonicallyOnRelease() {
        using var fixture = Fixtures.FreshServer(definition: WithKitDynamics(damping: 1f, dynamicsRow: "settle"));
        var body = JoinBody(fixture: fixture);
        var moveSpeed = ((float)((double)body.EffectiveMoveSpeed));
        var previous = 0f;

        for (var tick = 0; (tick < 480); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One));
            fixture.Step();

            var speed = body.PlanarSpeed;

            Assert.True(condition: (speed >= (previous - 1e-4f)), userMessage: $"tick {tick}: speed dropped from {previous:0.#####} to {speed:0.#####} under critical damping");
            Assert.True(condition: (speed <= (moveSpeed + 1e-3f)), userMessage: $"tick {tick}: speed {speed:0.#####} exceeds the move speed {moveSpeed:0.#####}");
            previous = speed;
        }

        Assert.True(condition: (MathF.Abs(x: (previous - moveSpeed)) < (moveSpeed * 0.01f)), userMessage: $"after 2 s the speed {previous:0.#####} should sit within 1% of the move speed {moveSpeed:0.#####}");

        for (var tick = 0; (tick < 480); tick++) {
            body.SubmitIntent(intent: default);
            fixture.Step();

            var speed = body.PlanarSpeed;

            Assert.True(condition: (speed <= (previous + 1e-4f)), userMessage: $"tick {tick}: speed rose from {previous:0.#####} to {speed:0.#####} while releasing under critical damping");
            previous = speed;
        }

        Assert.True(condition: (previous < 1e-3f), userMessage: $"after 2 s of release the speed should have decayed below one LSB; read {previous:0.#####}");
    }
    // A stick held at FULL deflection commands a target that already sits at the kit's own move-speed ceiling, so
    // StepPlanarFollower's clamp masks an overshoot there by construction (the very next step re-seeds the follower
    // back onto the clamped value). Holding a PARTIAL deflection instead commands a target well under the ceiling,
    // leaving the clamp inert and any overshoot past that lower target genuinely observable on PlanarSpeed.
    [Fact]
    public void LightDamping_OvershootsThePartialTarget_WhereCriticalDampingNeverDoes() {
        var partial = FixedQ4816.FromDouble(value: 0.4);

        using var lightFixture = Fixtures.FreshServer(definition: WithKitDynamics(damping: 0.25f, dynamicsRow: "loose"));
        var lightBody = JoinBody(fixture: lightFixture);
        var targetSpeed = (((float)((double)lightBody.EffectiveMoveSpeed)) * 0.4f);
        var lightOvershot = false;

        for (var tick = 0; (tick < 240); tick++) {
            lightBody.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: partial));
            lightFixture.Step();

            if (lightBody.PlanarSpeed > (targetSpeed * 1.02f)) {
                lightOvershot = true;
            }
        }

        Assert.True(condition: lightOvershot, userMessage: $"light damping should overshoot the partial target {targetSpeed:0.#####} at some tick");

        using var criticalFixture = Fixtures.FreshServer(definition: WithKitDynamics(damping: 1f, dynamicsRow: "settle"));
        var criticalBody = JoinBody(fixture: criticalFixture);
        var criticalOvershot = false;

        for (var tick = 0; (tick < 240); tick++) {
            criticalBody.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: partial));
            criticalFixture.Step();

            if (criticalBody.PlanarSpeed > (targetSpeed * 1.02f)) {
                criticalOvershot = true;
            }
        }

        Assert.False(condition: criticalOvershot, userMessage: "critical damping should never overshoot the partial target");
    }
    [Fact]
    public void IdenticalIntentReplays_ProduceIdenticalHashTraces_WhileADifferentSimulationRateDiverges() {
        Action<WorldBody, int> holdForward = static (body, _) => body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One));
        var document = WithKitDynamics(damping: 1f, dynamicsRow: "settle");
        var first = Fixtures.DriveHashTrace(document: document, ticks: 240, join: JoinBody, perTick: holdForward);
        var second = Fixtures.DriveHashTrace(document: document, ticks: 240, join: JoinBody, perTick: holdForward);

        Assert.Equal(actual: second, expected: first);

        const int slowRateHz = 120;
        var slowDocument = document with { Simulation = new WorldSimulationDefaults(RateHz: slowRateHz) };
        var slowStepTicks = checked((ulong)(FixedTickConversion.TicksPerSecond / ((ulong)slowRateHz)));
        var third = Fixtures.DriveHashTrace(document: slowDocument, join: JoinBody, perTick: holdForward, stepTicks: slowStepTicks, ticks: 240);

        Assert.NotEqual(actual: third, expected: first);
    }
}
