using Puck.Abstractions.Presentation;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void CapturingOneResidencyDoesNotFreezeAnUnrelatedHeldOrLaterResolvedSource() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame();
        var ownCaptures = 0;
        var unrelatedCaptures = 0;
        var laterCaptures = 0;
        using var own = new SdfWorldResidency(pipelines, new CapturingFrameSource(capture: () => { ownCaptures++; return source; }),
            SdfTestPipelines.Kernels(), "own", Extent, Extent, brickPoolVoxelCapacity: 0);
        using var unrelated = new SdfWorldResidency(pipelines, new CapturingFrameSource(capture: () => { unrelatedCaptures++; return source; }),
            SdfTestPipelines.Kernels(), "unrelated", Extent, Extent, brickPoolVoxelCapacity: 0);
        using var later = new SdfWorldResidency(pipelines, new CapturingFrameSource(capture: () => { laterCaptures++; return source; }),
            SdfTestPipelines.Kernels(), "later", Extent, Extent, brickPoolVoxelCapacity: 0);
        var views = new SdfWorldPasses(name => new SdfWorldView(Residency: name switch { "own" => own, "later" => later, _ => unrelated }, View: 0));
        var context = ContextOf(gpu: gpu);

        _ = views.ViewOf(instance: "own");
        _ = views.ViewOf(instance: "unrelated");
        own.ProduceFirstFrame(context: context);
        unrelated.ProduceFirstFrame(context: context);
        views.BeginConvergence(instance: "own", convergence: new RenderGraphConvergence(request: new FrameCaptureRequest("unused-scoped-capture.png")));
        views.BeginFrame(context: context);
        _ = views.ViewOf(instance: "later");
        later.ProduceFirstFrame(context: context);
        var ownBefore = ownCaptures;
        var unrelatedBefore = unrelatedCaptures;
        var laterBefore = laterCaptures;

        for (var frame = 0; (frame < 3); frame++) { views.BeginFrame(context: context); }
        Assert.Equal(actual: ownCaptures, expected: ownBefore);
        Assert.Equal(actual: unrelatedCaptures, expected: (unrelatedBefore + 3));
        Assert.Equal(actual: laterCaptures, expected: (laterBefore + 3));
    }
}
