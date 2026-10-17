using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// A view's frame runs the raster parts only when they draw: the mesh part a frame with a mesh draw, and a shadow cascade
// a frame with a draw whose shadows are on and whose first stable slot holds a directional light, the second cascade
// only for a second such slot. A part that skips records nothing, so its pass line binds no pipeline.
public sealed partial class SdfWorldPassesLawTests {
    // Whether the mesh part and the near and second cascades bound their pipelines in a view's latest completed frame.
    private static (bool Mesh, bool Near, bool Second) StagesOf(SdfFrame frame) {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        using var view = new SdfTestView(
            device: gpu,
            extent: Extent,
            hostsOnDirectX: false,
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

        // The views, sky and composite share the fixed rows and the default look's gradient.
        string[] skyRows = [.. Enumerable.Range(count: SdfSkyDetails.Runs, start: 0).Select(selector: SdfSkyDetails.RunLabel),
            SdfSkyDetails.Atmosphere, SdfSky.DefaultGradientLabel];

        foreach (var part in new[] { SdfWorldPackage.Parts.Views, SdfWorldPackage.Parts.Sky, SdfWorldPackage.Parts.Composite }) {
            var pass = sample.PassLabels.IndexOf(value: $"{RenderGraphPackageCatalog.SdfWorld}${part}");

            foreach (var label in skyRows) {
                Assert.Contains(expected: new GpuWorkDetail(Detail: label, Pass: pass), collection: sample.Details.ToArray());
            }
        }
        var binds = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.PipelineBinds);

        // A view that skips shadows runs a fragment with no cascade pass, so a cascade with no pass line did not run.
        bool Ran(string part) {
            var pass = sample.PassLabels.IndexOf(value: $"{RenderGraphPackageCatalog.SdfWorld}${part}");

            if ((pass < 0) && (part is SdfWorldPackage.Parts.ShadowNear or SdfWorldPackage.Parts.ShadowSecond)) {
                return false;
            }

            Assert.True(condition: (pass >= 0), userMessage: $"no {part} pass line");

            // A part the frame skips is counted skipped and has no counts; one that ran binds its pipeline.
            if (sample.GetPassState(pass: pass) == GpuPassState.Skipped) {
                return false;
            }

            Assert.True(condition: sample.TryGetPassCount(column: binds, pass: pass, value: out var bound));
            Assert.True(condition: (bound > 0L), userMessage: $"{part} ran but bound no pipeline");

            return true;
        }

        return (Ran(part: SdfWorldPackage.Parts.Mesh), Ran(part: SdfWorldPackage.Parts.ShadowNear), Ran(part: SdfWorldPackage.Parts.ShadowSecond));
    }
    // The host-visible bytes each part of a view's latest completed frame wrote, by part, over frames whose camera moves
    // along x each frame, so every pass block carries one run that changes every frame.
    private static Dictionary<string, long> HostBytesOf(SdfFrame frame) {
        var current = frame;
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        using var view = new SdfTestView(
            device: gpu,
            extent: Extent,
            hostsOnDirectX: false,
            pipelines: pipelines,
            residency: new SdfWorldResidency(
                brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current),
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
        for (var frameIndex = 1; (frameIndex <= 3); frameIndex++) {
            current = frame with {
                Views = [frame.Views[0] with {
                    Camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: new Vector3(x: (frameIndex * 0.125f), y: 0f, z: -5f),
                        target: Vector3.Zero, viewportHeight: Extent, viewportWidth: Extent),
                }],
            };
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
    // and the first detail row sit side by side in the block, so the views pass writing its detail row adds no run and no
    // coalescing: over frames that move only the camera, its bytes are the surface pass's, the camera's run alone.
    [Fact]
    public void ADetailRowBesideTheCounterRowAddsNoRunToAPassBlock() {
        var bytes = HostBytesOf(frame: (Frame() with { EnableCadenceGate = false }));

        Assert.True(condition: (bytes["sdf.world$surface"] > 0L));
        Assert.Equal(expected: bytes["sdf.world$surface"], actual: bytes["sdf.world$views"]);
    }
    [Fact]
    public void ZeroStableSlotsSkipTheCascadesEvenWithShadowCastingLights() {
        var frame = DrawnFrame();

        frame.Lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 0);
        Assert.Equal(expected: (true, false, false), actual: StagesOf(frame: frame));
    }
    [Fact]
    public void TheRasterPartsRunExactlyWhenTheyDraw() {
        var frame = DrawnFrame();

        Assert.Equal(actual: StagesOf(frame: Frame()), expected: (false, false, false));
        Assert.Equal(actual: StagesOf(frame: frame), expected: (true, true, false));
        // A view that skips shadows has no cascade pass to run.
        Assert.Equal(
            actual: StagesOf(frame: (frame with { Views = [(frame.Views[0] with { Quality = new SdfViewQuality { DisableSoftShadows = true } })] })),
            expected: (true, false, false)
        );
        // Soft shadows on, but no light casts them.
        Assert.Equal(actual: StagesOf(frame: (frame with { Lights = new SdfLights() })), expected: (true, false, false));
    }

    // The fixture's frame with one triangle drawn.
    private static SdfFrame DrawnFrame() => Frame() with {
        MeshDraws = [new SdfMeshDraw(Identity: "triangle", Material: 0, ObjectToWorld: System.Numerics.Matrix4x4.Identity,
            Mesh: new SdfMesh(indices: new uint[] { 0, 1, 2 }, positions: new System.Numerics.Vector3[] { System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY }))],
    };
}
