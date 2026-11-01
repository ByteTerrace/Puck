using Puck.Hosting;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A runtime lockstep change reaches the replay fast-forward loop through the same live policy.</summary>
public sealed class LiveReplayPacingLawTests {
    [Fact]
    public void LockstepTakesOneAuthorityTickAndUnlockingResumesTheRemainingBurst() {
        using var directory = new TemporaryDirectory(prefix: "puck-live-replay-pacing-");
        using var fixture = Fixtures.FreshServer();
        var tape = new WorldReplayTape(
            stateRoot: new WorldStateRoot(path: directory.RootPath),
            liveServer: fixture.Server, profiles: fixture.Server.Profiles,
            transport: new LoopbackTransport(server: fixture.Server), engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            addonHostFactory: static (_, _) => new NullAddonHost());

        Assert.True(condition: tape.TryBeginRecording(name: "parent", refusal: out var refusal), userMessage: refusal);
        for (var index = 0; (index < 4); index++) {
            fixture.Step();
            tape.NoteTick();
        }
        Assert.True(condition: tape.StopRecording().Verdict!.Match);
        Assert.True(condition: tape.TryBeginDrive(documentPath: null, forkName: "child", name: "parent", refusal: out refusal, toTick: null), userMessage: refusal);
        Assert.True(condition: tape.WantsFastForwardStep);
        var pacing = new HostPacing();
        var published = new List<ulong>();
        var width = FixedTickConversion.PerRate(ratePerSecond: ((uint)fixture.Server.Definition.SimulationRateHz));
        var context = new FixedStepContext(ElapsedTicks: width, StepTicks: width, Tick: 0);

        pacing.SetTickLocked(locked: true);
        Assert.Equal(expected: 1UL, actual: WorldServerStepShell.Step(
            server: fixture.Server, tape: tape, publishTick: published.Add, context: in context, pacing: pacing));
        Assert.Equal(expected: 1UL, actual: (fixture.Server.NextInputTick - 1));
        Assert.Equal(actual: published, expected: new ulong[] { 1 });
        Assert.True(condition: tape.WantsFastForwardStep);
        pacing.SetTickLocked(locked: false);
        context = new FixedStepContext(ElapsedTicks: (width * 2), StepTicks: width, Tick: 1);
        Assert.Equal(expected: 4UL, actual: WorldServerStepShell.Step(
            server: fixture.Server, tape: tape, publishTick: published.Add, context: in context, pacing: pacing));
        Assert.Equal(actual: published, expected: new ulong[] { 1, 2, 3, 4 });
        Assert.Equal(expected: 4UL, actual: (fixture.Server.NextInputTick - 1));
        Assert.Equal(expected: WorldReplayMode.Recording, actual: tape.Mode);
        _ = tape.CancelRecording();
    }
}
