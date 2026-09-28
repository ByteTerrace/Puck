using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldRoutedPresentationLawTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowResolutionAdvancesOnlyOnUnfrozenDressIncludingTheDefaultProjection(bool fitted) {
        using var state = new TemporaryDirectory(prefix: "puck-window-resolution-");
        using var host = WorldBootHarness.Compose(stateDirectory: state, presentation: WorldHostPresentation.Offscreen,
            world: "tests/Puck.Counters/counters.world.json").Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        using var destination = Endpoint(definition: AwayDocument(), identity: Away, position: AwayPose);
        using var window = presenter.AttachWindow(endpoint: destination);
        _ = Capture(source: presenter);
        var native = Capture(source: window.Scene.FrameSource).Views[window.Index];
        window.View = fitted ? native : null;
        var controller = new WorldDynamicResolutionController();
        var timing = new ResolutionTiming();
        var updates = 0;
        window.ResolveRenderScale = (_, _) => {
            updates++;
            return controller.Update(timing: timing, work: null, displayHertz: 60,
                stepBudget: 100, floor: 0.5f, ceiling: 1);
        };
        var films = 0;
        using var residency = new SdfWorldResidency(pipelines: SdfTestPipelines.Cache(), frameSource: window.Scene.FrameSource,
            kernels: SdfTestPipelines.Kernels(), name: "window", width: 64, height: 36,
            film: _ => { films++; return true; });
        var context = default(FrameContext);
        SdfViewSnapshot Frame() {
            timing.Advance();
            residency.BeginFrame();
            return residency.HostFrame(context: in context)!.Views[window.Index];
        }
        Assert.Equal(expected: 1, actual: Frame().ResolvedRenderScale);
        Assert.Equal(expected: 1, actual: Frame().ResolvedRenderScale);
        Assert.Equal(expected: 0.875f, actual: Frame().ResolvedRenderScale);
        var request = new FrameCaptureRequest(path: state.PathOf(name: "window.png"), converge: 8);
        residency.BeginConvergence(request: request);
        var held = Frame();
        var demand = controller.Demand;
        var updatesAtHold = updates;
        for (var frame = 0; frame < 8; frame++) {
            Assert.Equal(expected: held, actual: Frame());
            Assert.Equal(expected: demand, actual: controller.Demand);
            Assert.Equal(expected: updatesAtHold, actual: updates);
        }
        Assert.Equal(expected: updatesAtHold + 8, actual: films);
        request.Write(writer: static _ => { });
        _ = Frame();
        Assert.Equal(expected: updatesAtHold + 1, actual: updates);
        Assert.Equal(expected: demand - 1f / 16f, actual: controller.Demand);
    }

    private sealed class ResolutionTiming : IPresentTimingFeedback {
        public PresentTimingSample LastPresentTiming { get; private set; }
        public void Advance() => LastPresentTiming = new(PresentCount: LastPresentTiming.PresentCount + 1,
            PresentTimestampTicks: LastPresentTiming.PresentTimestampTicks + Stopwatch.Frequency / 15);
    }
}
