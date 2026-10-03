using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// The shading levers decide which stages a view's frame runs: the ambient part runs exactly when ambient occlusion is on,
// and the shadow part exactly when soft shadows are on and the environment has shadow slots. A part the lever turns off
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

        TestLiveness.Until(
            step: () => view.Produce(context: in context),
            reason: () => view.NotReadyReason,
            wait: view.Residency.WaitPipelineBuilds
        );

        for (var frameIndex = 0; (frameIndex < 3); frameIndex++) {
            _ = view.Produce(context: in context);
        }

        var sample = new GpuWorkSample();

        Assert.True(condition: view.Runtime.Work(instance: 0).TryReadCompleted(sample: sample));

        // The sky and the composite report the sky's detail rows, whichever recorder serves them: its field runs' rows and the
        // default look's gradient, whose evaluations are counted there, and a part that named none would drop them.
        string[] skyRows = [.. Enumerable.Range(count: SdfSkyDetails.Runs, start: 0).Select(selector: SdfSkyDetails.RunLabel), SdfSky.DefaultGradientLabel];

        foreach (var part in new[] { SdfWorldPackage.Parts.Sky, SdfWorldPackage.Parts.Composite }) {
            var pass = sample.PassLabels.IndexOf(value: $"{RenderGraphPackageCatalog.SdfWorld}${part}");

            foreach (var label in skyRows) {
                Assert.Contains(expected: new GpuWorkDetail(Detail: label, Pass: pass), collection: sample.Details.ToArray());
            }
        }
        Assert.DoesNotContain(collection: sample.Details.ToArray(), filter: detail => (detail.Pass == sample.PassLabels.IndexOf(value: $"{RenderGraphPackageCatalog.SdfWorld}${SdfWorldPackage.Parts.Shadow}")));

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
    // The host-visible bytes each part of a view's latest completed frame wrote, by part.
    private static Dictionary<string, long> HostBytesOf(SdfFrame frame) {
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

        TestLiveness.Until(
            step: () => view.Produce(context: in context),
            reason: () => view.NotReadyReason,
            wait: view.Residency.WaitPipelineBuilds
        );
        for (var frameIndex = 0; (frameIndex < 3); frameIndex++) {
            _ = view.Produce(context: in context);
        }

        var sample = new GpuWorkSample();

        Assert.True(condition: view.Runtime.Work(instance: 0).TryReadCompleted(sample: sample));

        var column = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.HostVisibleUploadBytes);
        var bytes = new Dictionary<string, long>();

        for (var pass = 0; (pass < sample.PassLabels.Length); pass++) {
            _ = sample.TryGetPassCount(column: column, pass: pass, value: out var value);
            bytes[sample.PassLabels[pass]] = value;
        }

        return bytes;
    }

    // A pass block's words that change every frame form one run each; the run list is bounded (GpuRegion.HostRunCapacity),
    // and a past-the-bound range merges neighbours across the words between them, re-sending them. The work counter row
    // and the first detail row sit side by side in the block, so a hit pass writing its row adds no run and no
    // coalescing: its bytes are the mask pass's plus the four of its row word.
    [Fact]
    public void ADetailRowBesideTheCounterRowAddsNoRunToAHitPassesBlock() {
        var bytes = HostBytesOf(frame: Frame());
        var mask = bytes["sdf.world$mask"];

        foreach (var part in new[] { SdfWorldPackage.Parts.Beam, SdfWorldPackage.Parts.CullArgs, SdfWorldPackage.Parts.Primary, SdfWorldPackage.Parts.Surface, SdfWorldPackage.Parts.Views }) {
            Assert.Equal(expected: (mask + 4L), actual: bytes[$"sdf.world${part}"]);
        }
    }
    [Fact]
    public void ZeroStableSlotsSkipShadowsEvenWithShadowCastingLights() {
        var frame = Frame();

        frame.Lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 0);
        Assert.Equal(expected: (true, false), actual: StagesOf(frame: frame));
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
            actual: StagesOf(frame: (frame with { Lights = new SdfLights() })),
            expected: (true, false)
        );
    }
}
