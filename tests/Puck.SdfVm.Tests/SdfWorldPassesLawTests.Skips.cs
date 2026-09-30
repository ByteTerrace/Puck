using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// The shading levers decide which stages a view's frame runs: the ambient part runs exactly when ambient occlusion is on,
// and the shadow part exactly when soft shadows are on and the environment has a shadow light. A part the lever turns off
// records nothing, so its pass line binds no pipeline.
public sealed partial class SdfWorldPassesLawTests {
    // Whether each of the ambient and shadow parts bound its pipeline in a view's latest completed frame.
    private static (bool Ambient, bool Shadow) StagesOf(SdfFrame frame) {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        using var view = new SdfTestView(
            device: gpu,
            extent: Extent,
            pipelines: pipelines,
            residency: new SdfWorldResidency(
                brickPoolVoxelCapacity: 0,
                frameSource: new FixedFrameSource(frame: frame),
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

        SdfTestPipelines.ProduceUntil(
            frame: () => view.Produce(context: in context),
            reason: () => view.NotReadyReason,
            wait: view.Residency.WaitPipelineBuilds
        );

        for (var frameIndex = 0; (frameIndex < 3); frameIndex++) {
            _ = view.Produce(context: in context);
        }

        var sample = new GpuWorkSample();

        Assert.True(condition: view.Runtime.Work(instance: 0).TryReadCompleted(sample: sample));

        var binds = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.PipelineBinds);

        bool Ran(string part) {
            var pass = sample.PassLabels.IndexOf(value: $"{RenderGraphPackageCatalog.SdfWorld}${part}");

            Assert.True(condition: (pass >= 0), userMessage: $"no {part} pass line");

            // A part the frame skips is counted skipped and has no counts; one that ran binds its pipeline.
            if (sample.GetPassState(pass: pass) == GpuPassState.Skipped) {
                return false;
            }

            Assert.True(condition: sample.TryGetPassCount(column: binds, pass: pass, value: out var bound));
            Assert.True(condition: (bound > 0L), userMessage: $"{part} ran but bound no pipeline");

            return true;
        }

        return (Ran(part: SdfWorldPackage.Parts.Ambient), Ran(part: SdfWorldPackage.Parts.Shadow));
    }

    [Fact]
    public void TheAmbientAndShadowPartsRunExactlyWhenTheirLeversTurnThemOn() {
        var frame = Frame();

        Assert.Equal(
            actual: StagesOf(frame: frame),
            expected: (true, true)
        );
        Assert.Equal(
            actual: StagesOf(frame: (frame with { Views = [(frame.Views[0] with { Quality = new SdfViewQuality { DisableAmbientOcclusion = true } })] })),
            expected: (false, true)
        );
        Assert.Equal(
            actual: StagesOf(frame: (frame with { Views = [(frame.Views[0] with { Quality = new SdfViewQuality { DisableSoftShadows = true } })] })),
            expected: (true, false)
        );
        // Soft shadows on, but no light casts them.
        Assert.Equal(
            actual: StagesOf(frame: (frame with { Environment = new SdfEnvironment() })),
            expected: (true, false)
        );
    }
}
