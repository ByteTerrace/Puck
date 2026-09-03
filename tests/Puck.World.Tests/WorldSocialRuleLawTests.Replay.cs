using Puck.Hosting;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialRuleLawTests {
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LiveReplayCannotEraseUnresolvedSocialOwnership(bool frozen) {
        Fixtures.SkipIfReplayDirectoryUnwritable();
        using var fixture = Fixtures.FreshServer(Document());
        var transport = new LoopbackTransport(fixture.Server);
        var tape = new WorldReplayTape(fixture.Server, fixture.Server.Profiles, transport, [], static (_, _) => new NullAddonHost());
        var name = $"social-held-{Guid.NewGuid():N}";
        Assert.True(tape.TryBeginRecording(name, out var reason), reason);
        fixture.Step(); tape.NoteTick();
        Assert.True(tape.StopRecording().Verdict!.Value.Match);

        var checkpoint = Capture(fixture);
        var bank = WorldSocialMemory.Restore(CompiledWorldSocialPolicy.Compile(Policy()), checkpoint.Server.Social!);
        var observer = new WorldEntityAddress("social-test", 0, 0);
        if (frozen) { Assert.True(bank.TryFreezeObserver(observer, new("upstream", 17), out reason), reason); }
        else { Assert.True(bank.TryReserveImport(new("upstream", 17), [new(observer, 0, 0)], out reason), reason); }
        fixture.Server.RestoreCheckpoint(checkpoint with { Server = checkpoint.Server with { Social = bank.Capture() } });
        var hash = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, fixture.Server.NextInputTick - 1);
        var echoes = new List<WorldEditEcho>(); fixture.Server.EchoTap += echoes.Add;
        var resets = 0; tape.TimelineRestored += () => resets++;
        Assert.False(tape.TryBeginDrive(name, null, null, null, out reason));
        Assert.Contains("social ownership", reason);
        Assert.Equal(hash, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, fixture.Server.NextInputTick - 1));
        Assert.Empty(echoes);
        Assert.Equal(0, resets);
        Assert.Equal(WorldReplayMode.Idle, tape.Mode);
        Assert.False(transport.InputMasked);

        // Resolve the external ownership explicitly: the same tape must then be allowed to rewind.
        fixture.Server.RestoreCheckpoint(checkpoint);
        Assert.True(tape.TryBeginDrive(name, null, null, null, out reason), reason);
        Assert.Equal(1, resets);
        Assert.Equal(0UL, fixture.Server.CompletedEngineTicks);
        tape.CancelDrive();
    }

    [Fact]
    public void LiveForkRewindsSocialClockReceiptsAndDecisionStateThenContinuesStandalone() {
        Fixtures.SkipIfReplayDirectoryUnwritable();
        var evidence = Evidence() with { Sequence = Clock(), OccurredAt = Clock() };
        var decision = new WorldRule(Name("choose"), [], Decision: new([
            new(Name("follow"), Query(), []), new(Name("avoid"), Constant(0.1m), []),
        ], PeriodSeconds: 0.01m));
        using var fixture = Fixtures.FreshServer(Document(Rule("observe", new ActionEffect.ObserveSocial(evidence)), decision));
        var transport = new LoopbackTransport(fixture.Server);
        var tape = new WorldReplayTape(fixture.Server, fixture.Server.Profiles, transport, [], static (_, _) => new NullAddonHost());
        var parent = $"social-parent-{Guid.NewGuid():N}";
        var child = $"social-child-{Guid.NewGuid():N}";
        Assert.True(tape.TryBeginRecording(parent, out var reason), reason);
        var expected = new ulong[32];
        for (var index = 0; index < expected.Length; index++) {
            fixture.Step(); tape.NoteTick();
            expected[index] = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, fixture.Server.NextInputTick - 1);
        }
        var stopped = tape.StopRecording();
        Assert.Null(stopped.VerifyFault);
        Assert.True(stopped.Verdict!.Value.Match, stopped.Verdict.Value.Describe());
        for (var index = 0; index < 48; index++) { fixture.Step(); }
        Assert.Equal(80, Memory(fixture).Receipts.Count);
        Assert.NotEmpty(Capture(fixture).Server.Decisions);

        var resets = 0;
        tape.TimelineRestored += () => resets++;
        Assert.True(tape.TryBeginDrive(parent, 8, child, null, out reason), reason);
        Assert.Equal(1, resets);
        Assert.Equal(0UL, fixture.Server.CompletedEngineTicks);
        Assert.Equal(1UL, fixture.Server.NextInputTick);
        Assert.Empty(Memory(fixture).Receipts);
        Assert.Empty(Capture(fixture).Server.Decisions);

        // Host pacing is deliberately far ahead. It still publishes monotonically while the authority replays 1..8.
        var published = new List<ulong>();
        var hostContext = new FixedStepContext(Tick: 10000, ElapsedTicks: 10001 * Fixtures.StepTicks, StepTicks: Fixtures.StepTicks);
        Assert.Equal(10008UL, WorldServerStepShell.Step(fixture.Server, tape, published.Add, in hostContext));
        Assert.Equal(Enumerable.Range(10001, 8).Select(static tick => (ulong)tick), published);
        Assert.Equal(WorldReplayMode.Recording, tape.Mode);
        Assert.Equal(expected[7], WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 8));
        Assert.Equal(8, Memory(fixture).Receipts.Count);

        for (var index = 8; index < expected.Length; index++) {
            fixture.Step(); tape.NoteTick();
            Assert.Equal(expected[index], WorldRuntimeStateHash.HashAuthoritative(fixture.Server, fixture.Server.NextInputTick - 1));
        }
        stopped = tape.StopRecording();
        Assert.Null(stopped.VerifyFault);
        Assert.True(stopped.Verdict!.Value.Match, stopped.Verdict.Value.Describe());
        Assert.True(tape.Verify(child).Match);
    }
}
