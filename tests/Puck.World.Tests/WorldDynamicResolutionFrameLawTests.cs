using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldDynamicResolutionFrameLawTests {
    [Fact]
    public void LiveViewsConsumeTheInjectedTimingAndAConvergingCaptureHoldsItsScale() {
        using var state = new TemporaryDirectory(prefix: "puck-resolution-frame-");
        using var host = WorldBootHarness.Compose(stateDirectory: state, presentation: WorldHostPresentation.Offscreen,
            world: "tests/Puck.Counters/counters.world.json").Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();
        settings.RenderScale = 1;
        settings.DynamicResolution = true;
        var timing = new Timing();
        var context = new FrameContext(Host: new HostContext(capabilities: new Dictionary<Type, object> {
            [typeof(IPresentTimingFeedback)] = timing,
        }), ElapsedTicks: 0, DeltaTicks: 0, FrameDeltaTicks: 0, AccumulatorTicks: 0,
            StepTicks: 1680, TargetWidth: 64, TargetHeight: 64, DisplayHertz: 60);
        SdfViewSnapshot Frame() {
            timing.Advance();
            presenter.PrepareGraph(context: in context);
            return presenter.CaptureFrame(width: 64, height: 64, deltaSeconds: 0, interpolationAlpha: 1).Views[0];
        }
        Assert.Equal(expected: 1f, actual: Frame().ResolvedRenderScale);
        Assert.Equal(expected: 1f, actual: Frame().ResolvedRenderScale);
        Assert.Equal(expected: 0.875f, actual: Frame().ResolvedRenderScale);
        var request = new FrameCaptureRequest(path: state.PathOf(name: "held.png"), converge: 8);
        presenter.BeginConvergence(request: request);
        var frozen = Frame();
        for (var frame = 0; frame < 8; frame++) {
            Assert.Equal(expected: frozen.ResolvedRenderScale, actual: Frame().ResolvedRenderScale);
        }
        request.Write(writer: static _ => { });
        settings.DynamicResolution = false;
        var disabled = Frame();
        Assert.Equal(expected: 0f, actual: disabled.ResolvedRenderScale);
        Assert.Equal(expected: 1f, actual: disabled.RenderScale);
    }
    private sealed class Timing : IPresentTimingFeedback {
        public PresentTimingSample LastPresentTiming { get; private set; }
        public void Advance() => LastPresentTiming = new(PresentCount: LastPresentTiming.PresentCount + 1,
            PresentTimestampTicks: LastPresentTiming.PresentTimestampTicks + Stopwatch.Frequency / 15);
    }
}
