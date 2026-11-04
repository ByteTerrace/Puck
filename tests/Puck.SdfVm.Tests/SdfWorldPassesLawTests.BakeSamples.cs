using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void ViewsBorrowOneBakeDestinationPerResidencyAndIsolatedResidenciesKeepTheirOwn() {
        var gpu = new FakeGpuDevice(trackObjects: true) { DistinctBuffers = true };
        var pipelines = SdfTestPipelines.Cache();
        SdfTestView Make(string name) => new(device: gpu, extent: Extent, hostsOnDirectX: false, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0, frameSource: new FixedFrameSource(Frame()),
                height: Extent, kernels: SdfTestPipelines.Kernels(), name: name, pipelines: pipelines, width: Extent));
        using var first = Make("first");
        using var second = Make("second");
        var frame = ContextOf(gpu);
        foreach (var view in new[] { first, second }) {
            TestLiveness.Until(() => view.Produce(in frame), reason: () => view.NotReadyReason, wait: view.Residency.WaitPipelineBuilds);
        }
        var buffer = first.Residency.Tables!.MeshBakeSamples;
        var other = second.Residency.Tables!.MeshBakeSamples;
        Assert.NotSame(buffer, other);
        var parameters = first.Runtime.Node(instance: 0).Plan!.Passes[0].Parameters;
        var context = new RenderGraphPackageRecorderContext(Instance: SdfTestView.Instance, Pass: "bake",
            Package: RenderGraphPackageCatalog.SdfWorld, Device: gpu, Services: gpu.Services, Pipelines: pipelines.Pipelines,
            HostsOnDirectX: false, InFlightFrames: SdfWorldTables.FrameRingSize, Width: Extent, Height: Extent,
            Inputs: [], Outputs: [], Parameters: parameters, Part: SdfWorldPackage.Parts.Bake);
        var declaration = new ShaderPipelineResource(Name: "samples", Kind: ShaderPipelineResourceKind.Buffer,
            SizeBytes: SdfWorldTables.MeshBakeSampleBytes);
        _ = first.Passes.CounterOf(instance: "second-view");
        var a = first.Passes.BorrowedBuffer(context, built: null, declaration, previous: other)!.Value;
        var b = first.Passes.BorrowedBuffer(context with { Instance = "second-view" }, built: null, declaration, previous: other)!.Value;
        try {
            Assert.Same(buffer, a.Buffer);
            Assert.Same(buffer, b.Buffer);
            Assert.NotNull(a.Ownership);
            Assert.NotNull(b.Ownership);
            var standing = first.Passes.BorrowedBuffer(context, built: null, declaration, previous: buffer)!.Value;
            Assert.Same(buffer, standing.Buffer);
            Assert.Null(standing.Ownership);
        } finally { a.Ownership?.Dispose(); b.Ownership?.Dispose(); }
        // The graph's owned allocation gauge excludes the borrowed destination; Tables owns it exactly once.
        Assert.True(first.Residency.TableBytes.DeviceLocal >= buffer.SizeBytes);
        Assert.Equal(0UL, first.Runtime.Node(instance: 0).ResourceStatus.Single(item => item.Name.EndsWith("bakeSamples", StringComparison.Ordinal)).AllocationBytes);
        var firstAllocation = Assert.Single(gpu.Created, item => item.Handle == buffer.BufferHandle);
        var secondAllocation = Assert.Single(gpu.Created, item => item.Handle == other.BufferHandle);
        first.Dispose();
        Assert.Equal(1, firstAllocation.DisposeCount);
        Assert.Equal(0, secondAllocation.DisposeCount);
        second.Dispose();
        Assert.Equal(1, secondAllocation.DisposeCount);
    }
}
