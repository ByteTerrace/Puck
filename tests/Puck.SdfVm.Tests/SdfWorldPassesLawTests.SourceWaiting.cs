using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // THE LAW (C0.4): a frame source that waits on a build answers through the residency, never by blocking. Before its
    // first frame the residency's not-ready reason names the build; once it renders, a capture that films nothing keeps
    // the frame standing, the view is withheld, and the graph answers waiting with the build's reason, which is what a
    // lockstep host holds its tick on and what world.wait names; the next frame the source films renders again.
    [Fact]
    public void AFrameSourceWaitingOnABuildAnswersWaitingWithItsReasonAndKeepsTheFrameStanding() {
        const string BuildReason = "the test program is building";
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var source = new WaitingFrameSource(frame: Frame()) { Building = BuildReason };
        using var view = new SdfTestView(
            device: gpu,
            extent: Extent,
            hostsOnDirectX: false,
            pipelines: pipelines,
            residency: new SdfWorldResidency(
                frameSource: source,
                height: Extent,
                kernels: SdfTestPipelines.Kernels(),
                name: SdfTestView.Instance,
                pipelines: pipelines,
                width: Extent
            )
        );
        var context = new FrameContext(
            AccumulatorTicks: 0UL,
            DeltaTicks: 0UL,
            ElapsedTicks: 0UL,
            FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object> {
                [typeof(IGpuDeviceContext)] = gpu,
            }),
            StepTicks: 0UL,
            TargetHeight: Extent,
            TargetWidth: Extent
        );

        _ = view.Produce(context: in context);
        Assert.Contains(expectedSubstring: BuildReason, actualString: view.Residency.NotReadyReason);
        Assert.Equal(expected: BuildReason, actual: view.Residency.SourceWaiting);

        source.Building = null;
        TestLiveness.Until(
            step: () => view.Produce(context: in context) && view.Runtime.Render.IsRendered,
            reason: () => (view.NotReadyReason ?? view.Runtime.Render.Reason),
            wait: view.Residency.WaitPipelineBuilds
        );

        var captures = source.Captures;

        source.Building = BuildReason;
        _ = view.Produce(context: in context);

        Assert.Equal(expected: (captures + 1), actual: source.Captures);
        Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: view.Runtime.Render.Completion);
        Assert.Contains(expectedSubstring: BuildReason, actualString: view.Runtime.Render.Reason);
        Assert.Contains(expectedSubstring: BuildReason, actualString: view.Passes.WithheldOf(instance: SdfTestView.Instance));
        Assert.True(condition: view.Residency.IsReady);

        source.Building = null;
        TestLiveness.Until(
            step: () => view.Produce(context: in context) && view.Runtime.Render.IsRendered,
            reason: () => view.Runtime.Render.Reason
        );
        Assert.Null(@object: view.Residency.SourceWaiting);
        Assert.Null(@object: view.Passes.WithheldOf(instance: SdfTestView.Instance));
    }

    // Films its frame, or nothing while a build stands, naming the build.
    private sealed class WaitingFrameSource(SdfFrame frame) : ISdfFrameSource {
        public string? Building { get; set; }
        public int Captures { get; private set; }
        public string? Waiting => Building;

        public SdfFrame? CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            Captures++;

            return ((Building is null) ? frame : null);
        }
    }
}
