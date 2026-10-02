using System.Diagnostics;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldDynamicResolutionFrameLawTests {
    [Fact]
    public void AControllerSweepFromTheNativeCeilingCreatesNoGpuObjectsAndCountsTheActiveGrid() {
        var extent = 32u;
        var builder = new SdfProgramBuilder();

        builder.Sphere(material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), radius: 1);
        var native = new SdfFrame(Program: builder.Build(), ProgramChanged: false, Time: 0,
            Views: [new SdfViewSnapshot(Camera: CameraSnapshot.LookAt(fieldOfViewRadians: 1,
                position: new Vector3(x: 0, y: 0, z: -5), target: Vector3.Zero, viewportHeight: extent, viewportWidth: extent),
                Region: new NormalizedRect(Height: 1, Width: 1, X: 0, Y: 0))]);
        var controller = new WorldDynamicResolutionController();
        var timing = new Timing();

        timing.Advance();
        var scale = 1f;
        var source = new ResolutionFrameSource(capture: () => {
            scale = controller.Update(timing: timing, work: null, displayHertz: 60, stepBudget: 100, floor: 0.5f, ceiling: 1);
            return native with { Views = [native.Views[0] with { ResolvedRenderScale = scale }] };
        });
        var gpu = new FakeGpuDevice(trackObjects: true);
        var pipelines = SdfTestPipelines.Cache();
        using var view = new SdfTestView(device: gpu, extent: extent, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0, frameSource: source,
                height: extent, kernels: SdfTestPipelines.Kernels(), name: "world", pipelines: pipelines, width: extent));
        var context = new FrameContext(Host: new HostContext(capabilities: new Dictionary<Type, object> {
            [typeof(IGpuDeviceContext)] = gpu,
        }), ElapsedTicks: 0, DeltaTicks: 0, FrameDeltaTicks: 0, AccumulatorTicks: 0, StepTicks: 0,
            TargetWidth: extent, TargetHeight: extent, DisplayHertz: 60);

        Assert.True(condition: SpinWait.SpinUntil(condition: () => view.Produce(context: in context),
            timeout: TimeSpan.FromSeconds(seconds: 30)), userMessage: view.NotReadyReason);
        for (var warm = 0; (warm < 8); warm++) { _ = view.Produce(context: in context); }
        var created = gpu.Created.Count;
        var node = view.Runtime.Node(instance: 0);
        var revision = node.WorkRevision;
        var minimum = scale;

        foreach (var load in new double[] { 4, 4, 4, 4, 4, 4, 4, 4,
            0.25, 0.25, 0.25, 0.25, 0.25, 0.25, 0.25, 0.25,
            0.25, 0.25, 0.25, 0.25, 0.25, 0.25, 0.25, 0.25 }) {
            timing.LastPresentTiming = new(PresentCount: (timing.LastPresentTiming.PresentCount + 1),
                PresentTimestampTicks: (timing.LastPresentTiming.PresentTimestampTicks + ((long)((Stopwatch.Frequency * load) / 60))));
            Assert.True(condition: view.Produce(context: in context), userMessage: view.NotReadyReason);
            minimum = MathF.Min(x: minimum, y: scale);
            Assert.Equal(expected: created, actual: gpu.Created.Count);
            Assert.Equal(expected: revision, actual: node.WorkRevision);
            Assert.Equal(expected: (extent, extent), actual: node.Extent);
            var pixels = ((long)(scale * extent));
            var row = view.Runtime.Latest!.Instances[0];

            Assert.Equal(expected: 11, actual: row.Passes);
            Assert.Equal(expected: (((10 * pixels) * pixels) + (extent * extent)), actual: row.PassPixels);
        }
        Assert.Equal(actual: minimum, expected: 0.5f);
        Assert.Equal(actual: scale, expected: 1);
    }

    private sealed class ResolutionFrameSource(Func<SdfFrame> capture) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => capture();
    }
}
