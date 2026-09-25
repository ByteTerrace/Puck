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
/// Laws for <see cref="SdfWorldPipelineCache"/> over <see cref="FakeGpuDevice"/>: leases on one device, kernel set and
/// brick choice share one set, created once and counted once in the cache's ledger; a different device, kernel set or
/// brick choice is a different set; the last release disposes the set and a later lease builds anew; two engine nodes
/// built from one services closure render with one set while their own ledgers count no pipeline; and a node whose set
/// another engine shares refuses a kernel reload rather than replacing pipelines that engine records with.
/// </summary>
public sealed class SdfWorldPipelineCacheLawTests {
    private const uint Extent = 32;
    // Every engine kernel but the two brick kernels, which the fake kernel set leaves empty.
    private const long PipelinesPerSet = 12L;

    [Fact]
    public void LeasesOnOneDeviceAndKernelSetShareOneSetCreatedOnce() {
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var cache = new SdfWorldPipelineCache();
        var first = cache.Acquire(
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels()
        );
        var second = cache.Acquire(
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels()
        );
        var set = Ready(lease: first);

        Assert.Same(expected: set, actual: Ready(lease: second));
        Assert.Equal(expected: 1, actual: cache.SharedSets);
        Assert.Equal(expected: PipelinesPerSet, actual: Created(cache: cache));

        first.Release();
        Assert.False(condition: set.IsDisposed);
        Assert.Same(expected: set, actual: second.Poll());

        second.Release();
        Assert.True(condition: set.IsDisposed);
        Assert.Equal(expected: 0, actual: cache.SharedSets);

        var later = cache.Acquire(
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels()
        );

        Assert.NotSame(expected: set, actual: Ready(lease: later));
        Assert.Equal(expected: (2L * PipelinesPerSet), actual: Created(cache: cache));
        later.Release();
    }
    [Fact]
    public void ADifferentDeviceKernelSetOrBrickChoiceIsADifferentSet() {
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var other = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var cache = new SdfWorldPipelineCache();
        SdfWorldPipelineLease[] leases = [
            cache.Acquire(device: gpu, includeBrickPipelines: false, kernels: SdfTestPipelines.Kernels()),
            cache.Acquire(device: other, includeBrickPipelines: false, kernels: SdfTestPipelines.Kernels()),
            cache.Acquire(device: gpu, includeBrickPipelines: false, kernels: SdfTestPipelines.Kernels(beam: 2)),
            cache.Acquire(device: gpu, includeBrickPipelines: true, kernels: SdfTestPipelines.Kernels()),
        ];
        var sets = leases.Select(selector: Ready).ToArray();

        Assert.Equal(expected: leases.Length, actual: sets.Distinct().Count());
        Assert.Equal(expected: leases.Length, actual: cache.SharedSets);

        foreach (var lease in leases) {
            lease.Release();
        }

        Assert.All(collection: sets, action: static set => Assert.True(condition: set.IsDisposed));
    }
    [Fact]
    public void TwoNodesOverOneCacheRenderWithOneSetTheCacheCounts() {
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var pipelines = new SdfWorldPipelineCache();
        var context = Context(gpu: gpu);

        using var first = Node(pipelines: pipelines);
        using var second = Node(pipelines: pipelines);

        _ = first.ProduceFirstFrame(context: in context);
        _ = second.ProduceFirstFrame(context: in context);

        Assert.Equal(expected: 1, actual: pipelines.SharedSets);
        Assert.Equal(expected: PipelinesPerSet, actual: Created(cache: pipelines));
        Assert.Equal(expected: 0L, actual: Read(kind: GpuWork.PipelinesCreated, source: first.WorkLifetime));
        Assert.Equal(expected: 0L, actual: Read(kind: GpuWork.PipelinesCreated, source: second.WorkLifetime));
        Assert.Equal(expected: 0L, actual: Read(kind: GpuWork.ShaderModulesCreated, source: first.WorkLifetime));

        first.Dispose();
        Assert.Equal(expected: 1, actual: pipelines.SharedSets);
        Assert.False(condition: second.ProduceFrame(context: in context).IsEmpty);

        second.Dispose();
        Assert.Equal(expected: 0, actual: pipelines.SharedSets);
    }
    [Fact]
    public void ANodeWhoseSetAnotherEngineSharesRefusesAKernelReload() {
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var pipelines = new SdfWorldPipelineCache();
        var context = Context(gpu: gpu);

        using var first = Node(pipelines: pipelines);
        using var second = Node(pipelines: pipelines);

        _ = first.ProduceFirstFrame(context: in context);
        _ = second.ProduceFirstFrame(context: in context);

        Assert.True(condition: first.RequestShaderReload(directory: AppContext.BaseDirectory));
        _ = first.ProduceFrame(context: in context);

        var status = first.ShaderReloadStatus;

        Assert.Equal(expected: "failed", actual: status.State);
        Assert.Contains(expectedSubstring: "shared", actualString: status.Error);
        Assert.Equal(expected: PipelinesPerSet, actual: Created(cache: pipelines));
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
    private static long Created(SdfWorldPipelineCache cache) =>
        Read(
            kind: GpuWork.PipelinesCreated,
            source: cache.Work
        );
    private static SdfEngineNode Node(SdfWorldPipelineCache pipelines) {
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

        return new SdfEngineNode(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: frame),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            pipelines: pipelines,
            width: Extent
        );
    }
    private static long Read(WorkKind kind, IWorkCounterSource source) {
        Assert.True(condition: source.TryRead(kind: kind, value: out var value));

        return value;
    }
    // Polls until the lease's set has built on the thread pool. The bound is liveness for a build over a fake device.
    private static SdfWorldPipelines Ready(SdfWorldPipelineLease lease) {
        SdfWorldPipelines? set = null;

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => ((set = lease.Poll()) is not null),
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        return set!;
    }

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
}
