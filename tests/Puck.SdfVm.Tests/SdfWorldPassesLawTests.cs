using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Abstractions.Cameras;
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
    public void AViewAllocatesEachScratchBufferOnceAndOneViewportRegion() {
        var naming = new RecordingGpuObjectNaming(isEnabled: true);
        var gpu = new FakeGpuDevice(
            naming: naming,
            reportVersion: SdfIsa.Version
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

        // The bound is liveness for a build over a fake device; it decides nothing.
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => view.Produce(context: in context),
            timeout: TimeSpan.FromSeconds(value: 30)
        ), userMessage: view.NotReadyReason);

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
        Assert.Equal(
            actual: buffers.Keys.Where(predicate: static name => (name.StartsWith(comparisonType: StringComparison.Ordinal, value: $"{SdfTestView.Instance}/") && name.EndsWith(comparisonType: StringComparison.Ordinal, value: "/viewports"))).Distinct().Order(comparer: StringComparer.Ordinal),
            expected: [$"{SdfTestView.Instance}/sdf.world$sky/viewports"]
        );
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

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
}
