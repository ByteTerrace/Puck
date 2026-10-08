using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;
using static Puck.World.Testing.TimelineLeverFixtures;

namespace Puck.World.Presentation.Tests;

public sealed class WorldTimelineLeverLawTests {
    private sealed class Authority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;
            return true;
        }
    }

    private static void Control(WorldStateMirror mirror, string name, WorldTimelineOperation operation, ulong tick = 0, double rate = 1d) =>
        Assert.True(condition: mirror.ControlClock(name: name, operation: operation, rate: rate, refusal: out var refusal, tick: tick), userMessage: refusal);

    [Fact]
    public void HeldStateClockKeepsItsPresentedRowWhileTheSimulationContinues() {
        using var row = HostRow.Build(name: "p18-12-timeline", definition: Definition());
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => row.Server.Definition));

        while (row.Server.CompletedEngineTicks < 25200UL) { row.Server.Advance(stepTicks: Fixtures.StepTicks); }
        Assert.Equal(25200UL, row.Server.CompletedEngineTicks);
        mirror.Install(tick: 0UL, engineTick: row.Server.CompletedEngineTicks);
        var registry = new CommandRegistry(modules: [new WorldTimelineCommandModule(authority: new Authority(instance: row.Instance), presentation: _ => mirror)]);
        var before = WorldDefinitionSerialization.Serialize(definition: row.Server.Definition);
        var result = registry.Submit(line: "world.timeline hold tide");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.Equal(25200UL, row.Server.CompletedEngineTicks);
        Assert.Equal(before, WorldDefinitionSerialization.Serialize(definition: row.Server.Definition));
        using var environment = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        Assert.Equal(0.1f, environment.Resolve(row.Server.Definition, 0, mirror).Sky.Atmosphere.FogDensity, 6);
        Assert.Equal(0.5f, environment.Resolve(row.Server.Definition, 0, mirror).Sky.Layers[1].Phase);
        var resolutions = environment.Resolutions;
        var keyed = mirror.KeyedResolutionCount;

        for (var index = 0; (index < 10); index++) {
            row.Server.Advance(stepTicks: Fixtures.StepTicks);
            mirror.Advance(tick: ((ulong)(index + 1)), engineTick: row.Server.CompletedEngineTicks);
            mirror.Apply(fraction: 1f);
            Assert.True(condition: mirror.TryReadPhase(clock: out _, name: "tide", phase: out var phase));
            Assert.True(condition: (phase == 0.5d), userMessage: "A held clock must not advance its presented state row.");
            Assert.Equal(new PresentedTick(Fraction: 0d, Whole: 25200UL), mirror.ClockTick(name: "tide"));
            Assert.Equal(0.1f, environment.Resolve(row.Server.Definition, 0, mirror).Sky.Atmosphere.FogDensity, 6);
            Assert.Equal(0.5f, environment.Resolve(row.Server.Definition, 0, mirror).Sky.Layers[1].Phase);
        }
        Assert.True(condition: (row.Server.CompletedEngineTicks > 0UL));
        Assert.Equal(row.Server.CompletedEngineTicks, mirror.EngineTick);
        Assert.True(condition: mirror.TryClockPhase(clock: Definition().Timeline.Clocks![1], phase: out var livePhase));
        Assert.NotEqual(actual: livePhase, expected: 0.5d);
        Assert.Equal(resolutions, environment.Resolutions);
        Assert.Equal(keyed, mirror.KeyedResolutionCount);
    }
    [Fact]
    public void ScrubbingAStateClockReadsThatTickWithoutWritingTheServer() {
        using var row = HostRow.Build(name: "p18-12-scrub", definition: Definition());
        var mirror = ClientFixtures.StateMirror(row.Server.Definition);
        var before = WorldDefinitionSerialization.Serialize(definition: row.Server.Definition);

        Control(mirror, "tide", WorldTimelineOperation.At, tick: 12600UL);
        Assert.True(condition: mirror.TryReadPhase(clock: out _, name: "tide", phase: out var phase));
        Assert.Equal(actual: phase, expected: 0.25d);
        Assert.Equal(0UL, row.Server.CompletedEngineTicks);
        Assert.Equal(before, WorldDefinitionSerialization.Serialize(definition: row.Server.Definition));
    }
    [Fact]
    public void TickScrubIsExactAndRunAndRateContinueWithoutJumping() {
        var mirror = ClientFixtures.StateMirror(Definition(), engineTick: 100UL);

        Control(mirror, "day", WorldTimelineOperation.At, tick: 12600UL);
        mirror.Advance(engineTick: 50400UL, tick: 1UL);
        mirror.Apply(fraction: 1f);
        Assert.True(condition: mirror.TryReadPhase(clock: out _, name: "day", phase: out var phase));
        Assert.Equal(actual: phase, expected: 0.25d);
        Control(mirror, "day", WorldTimelineOperation.Rate, rate: 2d);
        Control(mirror, "day", WorldTimelineOperation.Run);
        mirror.Advance(engineTick: 56700UL, tick: 2UL);
        mirror.Apply(fraction: 1f);
        Assert.Equal(new PresentedTick(Fraction: 0d, Whole: 25200UL), mirror.ClockTick(name: "day"));
        Control(mirror, "day", WorldTimelineOperation.Hold);
        mirror.Advance(engineTick: 63000UL, tick: 3UL);
        mirror.Apply(fraction: 1f);
        Assert.Equal(new PresentedTick(Fraction: 0d, Whole: 25200UL), mirror.ClockTick(name: "day"));
        Assert.Equal(mirror.Presented, mirror.ClockTick(name: "tide"));
        Control(mirror, "day", WorldTimelineOperation.At, tick: (ulong.MaxValue - 1UL));
        Assert.Equal((ulong.MaxValue - 1UL), mirror.ClockTick(name: "day").Whole);
        Assert.True(condition: mirror.TryReadPhase(clock: out _, name: "day", phase: out phase));
        Assert.Equal(WorldClocks.Phase(clock: Definition().Timeline.Clocks![0], tick: new PresentedTick(Fraction: 0d, Whole: (ulong.MaxValue - 1UL))), phase);
    }
    [Fact]
    public void KeyedRatesUseTheScrubbedTickAndDeliveriesIgnoreThePreview() {
        var mirror = ClientFixtures.StateMirror(Definition());
        var rate = new BindableScalar(keys: new WorldKeyTrack<float>(clock: "day", keys: [new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: 2f)]));

        Control(mirror, "day", WorldTimelineOperation.At, tick: 12600UL);
        Assert.Equal(0.5d, mirror.Integrate(modulus: 100d, rate: in rate));
        Assert.True(condition: mirror.TryPhase("day", out _, out var phase, delivered: true));
        Assert.Equal(actual: phase, expected: 0d);
        mirror.BeginLifetime();
        Assert.Equal(mirror.Presented, mirror.ClockTick(name: "day"));
    }
    [InlineData("world.timeline at day -1")]
    [InlineData("world.timeline at day 1.5")]
    [InlineData("world.timeline rate day NaN")]
    [InlineData("world.timeline rate day -1")]
    [InlineData("world.timeline hold missing")]
    [InlineData("world.timeline run day extra")]
    [Theory]
    public void InvalidControlsLeaveTheClockUntouched(string command) {
        using var row = HostRow.Build(name: "p18-12-timeline", definition: Definition());
        var mirror = ClientFixtures.StateMirror(row.Server.Definition);
        var registry = new CommandRegistry(modules: [new WorldTimelineCommandModule(authority: new Authority(instance: row.Instance), presentation: _ => mirror)]);

        Control(mirror, "day", WorldTimelineOperation.At, tick: 123UL);
        Assert.True(condition: registry.Submit(line: command).IsError);
        Assert.Equal(new PresentedTick(Fraction: 0d, Whole: 123UL), mirror.ClockTick(name: "day"));
    }
}
