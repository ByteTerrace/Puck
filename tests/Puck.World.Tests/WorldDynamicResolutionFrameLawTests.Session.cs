using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldDynamicResolutionFrameLawTests {
    [Fact]
    public void SessionResolutionUsesTheAuthoredOutputExtentAndFreezesItsControllerWithTheFrame() {
        var definition = AuthoredGameFixtures.Load(relativePath: "tests/Puck.World.Canaries/uploaded-sources/session.world.json");
        var mirror = new WorldSessionMirror(placeholder: definition);
        var emitter = new WorldSessionSceneEmitter(effectiveCameraName: null, mirror: mirror);
        var controller = new WorldDynamicResolutionController();
        var timing = new Timing();
        var updates = 0;

        emitter.ResolveRenderScale = (width, height) => {
            Assert.Equal(actual: (width, height), expected: (160u, 144u));
            updates++;
            return controller.Update(timing: timing, work: null, displayHertz: 60, stepBudget: 100, floor: 0.5f, ceiling: 1);
        };
        var source = new WorldSessionFrameSource(inner: new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]),
            captureHostFirst: static () => { }, resolution: new WorldScreenResolution(Height: 144, Width: 160));
        using var residency = new SdfWorldResidency(pipelines: SdfTestPipelines.Cache(), frameSource: source,
            kernels: SdfTestPipelines.Kernels(), name: "session", width: 1280, height: 720);
        var context = default(FrameContext);

        SdfViewSnapshot Frame() {
            timing.Advance();
            residency.BeginFrame();
            return residency.HostFrame(context: in context)!.Views[0];
        }
        Assert.Equal(expected: 1, actual: Frame().ResolvedRenderScale);
        Assert.Equal(expected: 1, actual: Frame().ResolvedRenderScale);
        Assert.Equal(expected: 0.875f, actual: Frame().ResolvedRenderScale);
        var request = new FrameCaptureRequest(converge: 8, path: "session-resolution-held.png");

        residency.BeginConvergence(request: request);
        var held = Frame();
        var demand = controller.Demand;
        var updatesAtHold = updates;

        for (var frame = 0; (frame < 8); frame++) {
            Assert.Equal(expected: held, actual: Frame());
            Assert.Equal(actual: updates, expected: updatesAtHold);
            Assert.Equal(expected: demand, actual: controller.Demand);
        }
        request.Write(writer: static _ => { });
        _ = Frame();
        Assert.Equal(actual: updates, expected: (updatesAtHold + 1));
        Assert.Equal(expected: (demand - (1f / 16f)), actual: controller.Demand);
    }
}
