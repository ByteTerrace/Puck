using System.Numerics;
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
/// Laws for <see cref="SdfWorldPipelineCatalog"/> over <see cref="FakeGpuDevice"/>: every pipeline a residency records
/// with is an entry of the catalog's pass-pipeline cache, so two residencies on one device and kernel set render with one
/// creation per pipeline, the cache's counts, while their own ledgers count none; an entry outlives the first residency
/// that releases it and goes with the last; and a set whose kernel set differs by one kernel shares every other entry.
/// </summary>
public sealed class SdfWorldPipelineCatalogLawTests {
    private const uint Extent = 32;
    // One shadow kernel and three views kernels serve every fade capacity; the receiver and the three environment kernels
    // join the thirteen base kernels. The fake brick baker is empty and resolve is acquired on demand.
    private const long KernelPipelines = 17L;
    // The kernels, the region copy, the mesh pass and its impostor card pass.
    private const int ResidencyPipelines = 20;

    [Fact]
    public void TwoResidenciesOverOneCatalogRenderWithOneCreationPerPipeline() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var context = Context(gpu: gpu);

        using var first = Node(pipelines: pipelines);
        using var second = Node(pipelines: pipelines);

        first.ProduceFirstFrame(context: in context);
        second.ProduceFirstFrame(context: in context);

        Assert.Equal(
            actual: (pipelines.Pipelines.SharedPipelines, Created(catalog: pipelines)),
            expected: (ResidencyPipelines, ((long)ResidencyPipelines))
        );
        Assert.Equal(expected: 0L, actual: Read(kind: GpuWork.PipelinesCreated, source: first.WorkLifetime));
        Assert.Equal(expected: 0L, actual: Read(kind: GpuWork.PipelinesCreated, source: second.WorkLifetime));
        Assert.Equal(expected: 0L, actual: Read(kind: GpuWork.ShaderModulesCreated, source: first.WorkLifetime));

        first.Dispose();
        Assert.Equal(expected: ResidencyPipelines, actual: pipelines.Pipelines.SharedPipelines);
        Assert.True(condition: second.Produce(context: in context));

        second.Dispose();
        Assert.Equal(expected: 0, actual: pipelines.Pipelines.SharedPipelines);
    }
    [Fact]
    public void ASetDifferingByOneKernelSharesEveryOtherEntry() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();

        using var first = SdfTestPipelines.Build(
            cache: pipelines.Pipelines,
            device: gpu,
            kernels: SdfTestPipelines.Kernels(beam: 1)
        );
        using var second = SdfTestPipelines.Build(
            cache: pipelines.Pipelines,
            device: gpu,
            kernels: SdfTestPipelines.Kernels(beam: 2)
        );

        Assert.Equal(
            actual: (pipelines.Pipelines.SharedPipelines, Created(catalog: pipelines)),
            expected: ((((int)KernelPipelines) + 1), (KernelPipelines + 1L))
        );
    }

    private static FrameContext Context(FakeGpuDevice gpu) => new(
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
    private static long Created(SdfWorldPipelineCatalog catalog) =>
        Read(
            kind: GpuWork.PipelinesCreated,
            source: catalog.Pipelines.Work
        );
    private static SdfWorldResidency Node(SdfWorldPipelineCatalog pipelines) {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        var frame = new SdfFrame(
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

        return new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: frame),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            name: "world",
            pipelines: pipelines,
            width: Extent
        );
    }
    private static long Read(WorkKind kind, IWorkCounterSource source) {
        Assert.True(condition: source.TryRead(kind: kind, value: out var value));

        return value;
    }

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
}
