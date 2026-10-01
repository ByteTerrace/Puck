using Puck.Hosting;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A replay fork paces as its host does (<see cref="HostPacing"/>): under a host that steps one tick per
/// produced frame it advances one authority tick per shell call, keeping the rest for later calls, and under a wall-clock
/// host it fast-forwards its burst in one call.</summary>
public sealed class ReplayTickPacingLawTests {
    [InlineData(true, 1UL)]
    [InlineData(false, 3UL)]
    [Theory]
    public void AReplayForkPacesAsItsHostDoes(bool oneTickPerFrame, ulong completed) {
        var pacing = (oneTickPerFrame
            ? HostPacing.OneTickPerFrame
            : HostPacing.WallClock);
        using var directory = new TemporaryDirectory(prefix: "puck-replay-pacing-");
        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            stateRoot: new WorldStateRoot(path: directory.RootPath),
            liveServer: fixture.Server,
            profiles: fixture.Server.Profiles,
            transport: transport,
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            addonHostFactory: static (_, _) => new NullAddonHost()
        );

        Assert.True(condition: tape.TryBeginRecording(name: "parent", refusal: out var refusal), userMessage: refusal);
        for (var index = 0; (index < 3); index++) {
            fixture.Step();
            tape.NoteTick();
        }
        var recorded = tape.StopRecording();

        Assert.Null(@object: recorded.VerifyFault);
        Assert.True(condition: recorded.Verdict!.Value.Match);
        Assert.True(condition: tape.TryBeginDrive(
            documentPath: null,
            forkName: "child",
            name: "parent",
            refusal: out refusal,
            toTick: null
        ), userMessage: refusal);
        Assert.True(condition: tape.WantsFastForwardStep);

        var published = new List<ulong>();
        var stepTicks = EngineTicks.PerRate(ratePerSecond: ((uint)fixture.Server.Definition.SimulationRateHz));
        var context = new FixedStepContext(ElapsedTicks: stepTicks, StepTicks: stepTicks, Tick: 0UL);
        var actual = WorldServerStepShell.Step(
            pacing: pacing,
            server: fixture.Server,
            tape: tape,
            publishTick: published.Add,
            context: in context
        );

        Assert.Equal(actual: actual, expected: completed);
        Assert.Equal(expected: completed, actual: (fixture.Server.NextInputTick - 1UL));
        Assert.Equal(expected: ((int)completed), actual: published.Count);
        Assert.Equal(expected: (completed * stepTicks), actual: fixture.Server.CompletedEngineTicks);
        if (oneTickPerFrame) {
            Assert.Equal(expected: 1, actual: tape.DriveProgress!.Value.Cursor);
            // No ticks are discarded: later calls finish the same fork one tick at a time.
            for (var tick = 1UL; (tick < 3UL); tick++) {
                context = new FixedStepContext(ElapsedTicks: ((tick + 1UL) * stepTicks), StepTicks: stepTicks, Tick: tick);
                Assert.Equal(expected: (tick + 1UL), actual: WorldServerStepShell.Step(
                    pacing: HostPacing.OneTickPerFrame,
                    server: fixture.Server,
                    tape: tape,
                    publishTick: published.Add,
                    context: in context
                ));
            }
        }
        Assert.Equal(expected: WorldReplayMode.Recording, actual: tape.Mode);
        Assert.Equal(expected: 3UL, actual: (fixture.Server.NextInputTick - 1UL));
        _ = tape.CancelRecording();
    }
}
