using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Launcher;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: one render-scale grammar controls durable scalar ceilings and per-view tier floors,
/// while a bindable pin is bounded, allocates nothing during a sweep, resumes within one policy step and is never saved.</summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldRenderScaleGrammarLawTests : IDisposable {
    private readonly TemporaryDirectory m_state = new(prefix: "puck-render-scale-grammar-");

    public void Dispose() => m_state.Dispose();

    private (CommandRegistry Commands, WorldRenderSettings Settings, WorldFramePresenter Presenter) Compose() {
        var host = m_state.Own(owner: WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_state,
            world: "tests/Puck.Counters/counters.puck").Build());

        host.Services.GetRequiredService<WorldClient>().AttachSessionLevers(levers: host.Services.GetRequiredService<WorldSessionLeverSink>());
        return (host.Services.GetRequiredService<CommandRegistry>(), host.Services.GetRequiredService<WorldRenderSettings>(),
            host.Services.GetRequiredService<WorldFramePresenter>());
    }

    [Fact]
    public void AnOutOfRangePinIsRefusedByName() {
        var (commands, settings, _) = Compose();
        Assert.Equal(CommandBindability.Bindable, commands.Definitions.Single(predicate: command => (command.Name == "world.render-scale")).Bindability);
        Assert.False(condition: commands.Submit(line: "world.render-scale world 0.875").IsError);
        Assert.False(condition: commands.Submit(line: "world.render-scale world floor half").IsError);
        foreach (var pin in new[] { "quarter", "native", "110%" }) {
            var result = commands.Submit(line: $"world.render-scale world pin {pin}");

            Assert.True(condition: result.IsError, userMessage: $"pin {pin} must be refused");
            Assert.Contains("pin", result.Output);
            Assert.Contains("world", result.Output);
            Assert.Equal(0f, settings.Resolution(view: "world").Pin);
        }
        Assert.False(condition: commands.Submit(line: "world.render-scale world pin 80%").IsError);
        Assert.Equal(0.8f, settings.Resolution(view: "world").Pin);
        // A later ceiling change keeps an existing pin in range.
        Assert.False(condition: commands.Submit(line: "world.render-scale world 0.75").IsError);
        Assert.Equal(settings.Ceiling(view: "world"), settings.Resolution(view: "world").Pin);
    }
    [Fact]
    public void ASweepAtTheSameCeilingAllocatesNothing() {
        var (commands, settings, presenter) = Compose();
        Assert.False(condition: commands.Submit(line: "world.render-scale world 0.875").IsError);
        var snapshot = new SdfViewSnapshot();
        // Warm only registration and JIT; the measured path includes the lever application and view dressing.
        for (var step = 0; (step < 64); step++) { Sweep(step: step); }
        var controller = settings.Resolution(view: "world").Controller;
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var step = 0; (step < 1024); step++) { Sweep(step: step); }
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(actual: allocated, expected: 0L);
        Assert.Same(controller, settings.Resolution(view: "world").Controller);
        Assert.Equal(0.875f, snapshot.RenderScale);
        Assert.Equal(0.75f, snapshot.ResolvedRenderScale);

        void Sweep(int step) {
            if (!settings.SetResolution(operation: WorldRenderScaleOperation.Pin, refusal: out _, scale: (((step & 1) == 0) ? 0.625f : 0.75f), view: "world")) {
                throw new InvalidOperationException(message: "sweep pin was refused");
            }
            snapshot = presenter.DressResolution(height: 144, name: "world", view: snapshot, width: 256);
        }
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AutoResumesWithinAtMostOnePolicyStep(bool budgeted) {
        var (commands, settings, presenter) = Compose();
        Assert.False(condition: commands.Submit(line: "world.render-scale world 0.875").IsError);
        Assert.False(condition: commands.Submit(line: "world.render-scale world pin 0.625").IsError);
        presenter.FrameLoad = (budgeted ? new HeavyLoad() : null);
        var pinned = presenter.DressResolution(new SdfViewSnapshot(), "world", 256, 144);

        Assert.Equal(0.625f, pinned.ResolvedRenderScale);
        Assert.False(condition: commands.Submit(line: "world.render-scale world auto").IsError);
        var resumed = presenter.DressResolution(new SdfViewSnapshot(), "world", 256, 144);

        Assert.Equal(0f, settings.Resolution(view: "world").Pin);
        Assert.Equal((0.625f * ((float)(budgeted ? (1d - WorldDynamicResolution.MaximumFall) : (1d + WorldDynamicResolution.MaximumRise)))), resumed.ResolvedRenderScale);
        Assert.Equal(pinned.RenderScale, resumed.RenderScale);
    }
    [Fact]
    public void AutoAfterAnOffSwitchStartsFromTheCeilingTheViewRendered() {
        var (commands, settings, presenter) = Compose();
        Assert.False(condition: commands.Submit(line: "world.render-scale world 0.875").IsError);
        Assert.False(condition: commands.Submit(line: "world.render-scale world pin 0.625").IsError);
        presenter.FrameLoad = null;
        Assert.Equal(0.625f, presenter.DressResolution(new SdfViewSnapshot(), "world", 256, 144).ResolvedRenderScale);
        Assert.False(condition: commands.Submit(line: "world.render-scale world auto off").IsError);
        Assert.Equal(0.875f, presenter.DressResolution(new SdfViewSnapshot(), "world", 256, 144).ResolvedRenderScale);
        Assert.False(condition: commands.Submit(line: "world.render-scale world auto").IsError);
        Assert.True(condition: settings.Enabled(view: "world"));
        Assert.Equal(0.875f, presenter.DressResolution(new SdfViewSnapshot(), "world", 256, 144).ResolvedRenderScale);
    }
    [Fact]
    public void ACameraOrSessionViewKeepsItsExtentUnlessALeverOrRowNamesIt() {
        var (commands, settings, presenter) = Compose();
        Assert.False(condition: commands.Submit(line: "world.render-scale 0.75").IsError);
        Assert.False(condition: commands.Submit(line: "world.render-scale auto").IsError);
        Assert.False(condition: commands.Submit(line: "world.render-scale pin 0.625").IsError);

        var player = presenter.DressResolution(new SdfViewSnapshot(), "world", 256, 144);
        var second = presenter.DressResolution(new SdfViewSnapshot(), "world$2", 256, 144);
        var session = presenter.DressResolution(new SdfViewSnapshot(), "session$0", 256, 144);

        Assert.Equal(0.75f, player.RenderScale);
        Assert.Equal(0.625f, player.ResolvedRenderScale);
        Assert.Equal(0.75f, second.RenderScale);
        Assert.Equal(1f, session.RenderScale);
        Assert.Equal(1f, session.ResolvedRenderScale);
        Assert.Equal(0f, settings.Resolution(view: "session$0").Pin);
        Assert.False(condition: settings.Enabled(view: "session$0"));
        Assert.False(condition: commands.Submit(line: "world.render-scale session$0 0.5").IsError);
        Assert.Equal(0.5f, presenter.DressResolution(new SdfViewSnapshot(), "session$0", 256, 144).RenderScale);
    }
    [Fact]
    public void ThePlayerDefaultFloorDoesNotBoundACameraView() {
        var (commands, settings, _) = Compose();
        Assert.False(condition: commands.Submit(line: "world.render-scale floor native").IsError);
        Assert.Equal(1f, settings.Floor(view: "world"));
        Assert.Equal(WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Quarter), settings.Floor(view: "counters-cam"));
        Assert.False(condition: commands.Submit(line: "world.render-scale counters-cam pin half").IsError);
        Assert.Equal(WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Half), settings.Resolution(view: "counters-cam").Pin);
    }
    [Fact]
    public void SessionQualitySurvivesAnotherReaderOfTheSameDocumentQuality() {
        var rows = new[] { new WorldViewQuality(Name: "world", RenderScale: 0.5f) };
        var first = (Fixtures.BuildDocument() with { ViewsRaw = new WorldViewDefaults(Quality: rows) });
        var copy = (first with { ViewsRaw = new WorldViewDefaults(Quality: [.. rows]) });
        var revised = (first with { ViewsRaw = new WorldViewDefaults(Quality: [new WorldViewQuality(Name: "world", RenderScale: 0.75f)]) });
        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults());

        settings.ReadQuality(definition: first);
        Assert.True(condition: settings.SetResolution(operation: WorldRenderScaleOperation.Floor, refusal: out _,
            scale: WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Eighth), view: "world"));
        settings.ReadQuality(definition: copy);
        settings.ReadQuality(definition: first);
        Assert.Equal(WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Eighth), settings.Floor(view: "world"));
        Assert.Equal(0.5f, settings.Ceiling(view: "world"));
        settings.ReadQuality(definition: revised);
        Assert.Equal(0.75f, settings.Ceiling(view: "world"));
        Assert.Equal(WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Quarter), settings.Floor(view: "world"));
    }
    [Fact]
    public void ACeilingAndFloorSurviveASaveRoundTrip() {
        var (commands, settings, _) = Compose();
        Assert.False(condition: commands.Submit(line: "world.render-scale 0.731").IsError);
        Assert.False(condition: commands.Submit(line: "world.render-scale world 0.613").IsError);
        Assert.False(condition: commands.Submit(line: "world.render-scale world floor eighth").IsError);
        Assert.False(condition: commands.Submit(line: "world.render-scale world pin 0.6").IsError);
        var definition = Fixtures.BuildDocument() with { RenderRaw = new WorldRenderDefaults() };
        var folded = WorldSessionLevers.Fold(definition, settings, new PresentPacingControl(initialTargetHertz: null),
            new SilentAudio(), new WorldBindingBarVisibility(), new WorldEditorSeats());
        var bytes = WorldDefinitionSerialization.Serialize(definition: folded);
        var reloaded = WorldDefinitionSerialization.Deserialize(bytes);
        var restored = new WorldRenderSettings(defaults: reloaded.Render);

        restored.ReadQuality(definition: reloaded);
        Assert.Equal(0.731f, restored.RenderScale);
        Assert.Equal(0.613f, restored.Ceiling(view: "world"));
        Assert.Equal(WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Eighth), restored.Floor(view: "world"));
        Assert.Equal(0f, restored.Resolution(view: "world").Pin);
        Assert.False(condition: restored.DynamicResolution);
    }

    private sealed class SilentAudio : IWorldAudioLever {
        public float? SessionMasterVolume => null;

        public void SetMasterVolume(float value) { }
    }
    private sealed class HeavyLoad : IWorldFrameLoadSource {
        public Puck.Abstractions.Presentation.PresentTimingSample LastPresentTiming => Puck.Abstractions.Presentation.PresentTimingSample.Unavailable;
        public double MarchStepBudgetPerPixel => 1d;

        public void RequireGpuTiming(bool required) { }
        public void RequireCompletions(bool required) { }
        public bool TryReadGpuFrame(out WorldFrameLoadReading reading) { reading = default; return false; }
        public bool TryReadMarchSteps(out WorldFrameLoadReading reading) {
            reading = new WorldFrameLoadReading(Grid: 0.625d, Load: ((256d * 144d) * 4d), Renders: 1);
            return true;
        }
        public Puck.Shaders.ShaderPipelineCompletions TakeCompletions() => default;
    }
}
