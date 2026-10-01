using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a view of a residency renders as a render-graph instance of the <c>sdf.world</c> fragment over
/// <c>FakeGpuDevice</c>, and allocates each of its scratch buffers once, whatever its frames in flight, and one
/// viewport-row region, the sky part's, which every later part binds.
/// </summary>
public sealed partial class SdfWorldPassesLawTests {
    private const uint Extent = 32;

    [GeneratedRegex(pattern: @"\[\d+\]$")]
    private static partial Regex SlotSuffix();

    [Fact]
    public void AViewAllocatesEachScratchBufferOnce() {
        var naming = new RecordingGpuObjectNaming(isEnabled: true);
        var gpu = new FakeGpuDevice(
            naming: naming
        );
        var pipelines = SdfTestPipelines.Cache();
        using var view = new SdfTestView(
            device: gpu,
            extent: Extent,
            pipelines: pipelines,
            residency: new SdfWorldResidency(
                brickPoolVoxelCapacity: 0,
                frameSource: new FixedFrameSource(frame: Frame()),
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

        for (var frame = 0; (frame < 4); frame++) {
            _ = view.Produce(context: in context);
        }

        var buffers = naming.Applied
            .Where(predicate: static entry => (entry.Kind == GpuObjectKind.Buffer))
            .Select(selector: static entry => SlotSuffix().Replace(input: entry.Name, replacement: string.Empty))
            .GroupBy(keySelector: static name => name)
            .ToDictionary(elementSelector: static group => group.Count(), keySelector: static group => group.Key);

        Assert.Equal(
            actual: new[] { "arguments", "cullBounds", "instanceMasks", "tiles", "visibility" }.Select(selector: scratch => buffers.GetValueOrDefault(key: $"{SdfTestView.Instance}/sdf.world${scratch}")),
            expected: [1, 1, 1, 1, 1]
        );

        var scheduled = new RenderGraphFrame(
            DisplayHeight: ((int)Extent),
            DisplayHertz: 60,
            DisplayWidth: ((int)Extent),
            Footprints: [],
            Index: view.Runtime.Latest!.Frame,
            Roots: [new RenderGraphRoot(Height: 1.0, Instance: SdfTestView.Instance, Width: 1.0)]
        );

        void Produce() {
            scheduled = scheduled with { Index = (scheduled.Index + 1) };
            _ = view.Runtime.ProduceFrame(context: in context, frame: in scheduled);
        }

        for (var warm = 0; (warm < 6); warm++) {
            Produce();
        }

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Produce));
    }
    [Fact]
    public void ACameraResolvedBeforeItsHostFilmsTheCurrentFrameOnce() {
        var pipelines = SdfTestPipelines.Cache();
        var frame = Frame();
        var captures = 0;
        using var host = new SdfWorldResidency(
            frameSource: new CapturingFrameSource(capture: () => frame with { Time = ++captures }),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            name: "world",
            pipelines: pipelines,
            width: Extent
        );
        SdfFrame? filmed = null;
        using var camera = new SdfWorldResidency(
            film: context => {
                filmed = host.HostFrame(context: in context);

                return true;
            },
            frameSource: new FixedFrameSource(frame: frame),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            name: "camera",
            pipelines: pipelines,
            width: Extent
        );
        var passes = new SdfWorldPasses(resolve: name => new SdfWorldView(
            Residency: ((name == "camera") ? camera : host),
            View: 0
        ));
        var context = new FrameContext(
            AccumulatorTicks: 0UL,
            DeltaTicks: 0UL,
            ElapsedTicks: 0UL,
            FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object>()),
            StepTicks: 0UL,
            TargetHeight: Extent,
            TargetWidth: Extent
        );

        _ = passes.CounterOf(instance: "camera");
        _ = passes.CounterOf(instance: "world");

        for (var produced = 1; (produced <= 2); produced++) {
            passes.BeginFrame(context: in context);

            Assert.Equal(actual: captures, expected: produced);
            Assert.Same(expected: host.Frame, actual: filmed);
        }
    }

    private static SdfFrame Frame() {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        return new SdfFrame(
            Program: builder.Build(),
            ProgramChanged: false,
            Time: 0f,
            Views: [new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(
                    fieldOfViewRadians: 1f,
                    position: new Vector3(x: 0f, y: 0f, z: -5f),
                    target: Vector3.Zero,
                    viewportHeight: Extent,
                    viewportWidth: Extent
                ),
                Region: new NormalizedRect(
                    Height: 1f,
                    Width: 1f,
                    X: 0f,
                    Y: 0f
                )
            )]
        );
    }

    private sealed class CapturingFrameSource(Func<SdfFrame> capture) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            capture();
    }
    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
}
