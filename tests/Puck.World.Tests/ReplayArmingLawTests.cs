using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: arming never produces a tape that cannot re-drive. A tape re-establishes the definition and the
/// seats, never state a step reached, so a recording armed after the world's first step refuses by name and arms
/// nothing; one armed before it re-drives to every recorded state.
/// </summary>
public sealed class ReplayArmingLawTests {
    private static WorldReplayTape Tape(WorldFixture fixture, string directory) => new(
        addonHostFactory: static (_, _) => new NullAddonHost(),
        engines: [],
        liveServer: fixture.Server,
        machineHostFactory: Fixtures.MachineHostFactory,
        profiles: fixture.Server.Profiles,
        stateRoot: new WorldStateRoot(path: directory),
        transport: new LoopbackTransport(server: fixture.Server)
    );

    [Fact]
    public void ARecordingArmedAfterTheFirstStepRefusesByName() {
        using var directory = new TemporaryDirectory(prefix: "puck-replay-arming-late-");
        using var fixture = Fixtures.FreshServer();
        var tape = Tape(directory: directory.RootPath, fixture: fixture);

        fixture.Step();

        Assert.False(condition: tape.TryBeginRecording(name: "late", refusal: out var refusal));
        Assert.StartsWith(expectedStartString: "ArmedAfterFirstStep:", actualString: refusal, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: WorldReplayMode.Idle, actual: tape.Mode);
        Assert.False(condition: fixture.Server.Profiles.Recording);
    }
    [Fact]
    public void ARecordingArmedBeforeTheFirstStepReDrivesToEveryRecordedState() {
        using var directory = new TemporaryDirectory(prefix: "puck-replay-arming-early-");
        using var fixture = Fixtures.FreshServer();
        var tape = Tape(directory: directory.RootPath, fixture: fixture);

        Assert.True(condition: tape.TryBeginRecording(name: "early", refusal: out var refusal), userMessage: refusal);
        for (var tick = 0; (tick < 10); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        var stop = tape.StopRecording();

        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());
    }
}
