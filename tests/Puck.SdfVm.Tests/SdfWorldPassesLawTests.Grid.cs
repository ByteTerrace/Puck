using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a view's render-scale ceiling alone chooses its graph and sizes its scratch, and the render grid
/// inside it moves every frame for free. A dip of the grid (a layout transition's) builds, allocates and swaps nothing,
/// and every frame of it renders, each pass of the graph at the grid that graph was built for; a view whose ceiling is
/// native reconstructs nothing, so it renders its output grid through the dip. And a reduced view builds its resolve
/// pipeline from the deployed kernel with no shader toolchain, as boot builds every other kernel.
/// </summary>
public sealed partial class SdfWorldPassesLawTests {
    // The resolve pipeline's creation is held for the whole dip, as a driver compiling it on a cold cache holds it, so a
    // dip that asked for another graph would leave frames to the installed one while that graph builds.
    [InlineData(0.5f)]
    [InlineData(1f)]
    [Theory]
    public void ADipInsideTheCeilingRendersEveryFrameWithoutABuildOrAnAllocation(float ceiling) {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var current = Frame();

        current = current with { Views = [current.Views[0] with { RenderScale = ceiling }] };
        using var view = GridView(gpu: gpu, pipelines: pipelines, capture: () => current);
        using var gate = new ManualResetEventSlim(initialState: false);
        var context = GridContext(gpu: gpu);

        TestLiveness.Until(step: () => view.Produce(context: in context), reason: () => (view.Runtime.Node(instance: 0).LastSwapError?.ToString() ?? view.NotReadyReason),
            wait: view.Residency.WaitPipelineBuilds);
        var node = view.Runtime.Node(instance: 0);
        var passes = ((ceiling < 1f) ? 11 : 10);

        TestLiveness.Until(step: () => (view.Produce(context: in context) && (node.Plan!.Passes.Count == passes) && !node.IsBuildingCandidate),
            reason: () => node.LastSwapError?.ToString(), wait: view.Residency.WaitPipelineBuilds);
        _ = view.Produce(context: in context);
        var buffers = gpu.BufferBytes;
        var pools = gpu.PoolsCreated.Count;
        var owned = node.OwnedBytes;
        var revision = node.WorkRevision;
        var created = CreatedPipelines(pipelines: pipelines);
        var latest = view.Runtime.Latest!.Instances[0].LatestFrame;

        gpu.ComputePipelineGateName = SdfKernelSet.StemOf(kernel: SdfKernel.Resolve);
        gpu.ComputePipelineGate = gate;
        gpu.SetBinds = [];
        try {
            // The dip holds the grid at half the ceiling for a transition's frames, then settles on the ceiling.
            foreach (var dip in new[] { 0.5f, 0.5f, 0.5f, 0.5f, 1f, 1f }) {
                current = current with { Views = [current.Views[0] with { ResolvedRenderScale = (ceiling * dip) }], Time = (current.Time + 1f) };
                gpu.SetBinds.Clear();
                Assert.True(condition: view.Produce(context: in context));
                var row = view.Runtime.Latest!.Instances[0];

                Assert.Equal(expected: RenderGraphInstanceStatus.Rendered, actual: row.Status);
                Assert.True(condition: (row.LatestFrame > latest));
                latest = row.LatestFrame;
                // Every pass of the frame records the one grid of the graph that renders it: the native graph its
                // output, the reconstructing graph the dipped grid.
                Assert.Equal(expected: passes, actual: node.Plan!.Passes.Count);
                Assert.Equal(expected: [((passes == 11) ? ((uint)((Extent * ceiling) * dip)) : Extent)], actual: Grids(gpu: gpu, passes: passes));
                Assert.Equal(expected: (buffers, pools, owned, revision, created), actual: (gpu.BufferBytes, gpu.PoolsCreated.Count, node.OwnedBytes, node.WorkRevision, CreatedPipelines(pipelines: pipelines)));
            }
            Assert.False(condition: node.IsBuildingCandidate);
        } finally {
            gate.Set();
        }
        Assert.Empty(collection: gpu.StateConflicts);
    }
    [Fact]
    public void AReducedViewBuildsItsDeployedResolveKernelWithoutReflectingIt() {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var current = Frame();
        // A deployed Direct3D 12 kernel's container, which only dxcompiler could reflect: the build must not ask it to.
        var container = new byte[64];

        "DXBC"u8.CopyTo(destination: container);
        current = current with { Views = [current.Views[0] with { RenderScale = 0.5f }] };
        using var view = GridView(gpu: gpu, pipelines: pipelines, capture: () => current,
            kernels: SdfTestPipelines.Kernels().With(bytecode: container, kernel: SdfKernel.Resolve));
        var context = GridContext(gpu: gpu);

        TestLiveness.Until(step: () => (view.Produce(context: in context) && (view.Runtime.Node(instance: 0).Plan!.Passes.Count == 11) &&
            !view.Runtime.Node(instance: 0).IsBuildingCandidate), reason: () => (view.Runtime.Node(instance: 0).LastSwapError?.ToString() ?? view.NotReadyReason),
            wait: view.Residency.WaitPipelineBuilds);
        Assert.Null(@object: view.Runtime.Node(instance: 0).LastSwapError);
        Assert.EndsWith(expectedEndString: "$resolve", actualString: view.Runtime.Node(instance: 0).Plan!.Passes[^1].Name);
    }

    private static SdfTestView GridView(UploadModelGpu gpu, SdfWorldPipelineCatalog pipelines, Func<SdfFrame> capture, SdfKernelSet? kernels = null) =>
        new(device: gpu, extent: Extent, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0, frameSource: new CapturingFrameSource(capture: capture), height: Extent,
                kernels: (kernels ?? SdfTestPipelines.Kernels()), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
    private static FrameContext GridContext(UploadModelGpu gpu) => new(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
        Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
        StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);
    // The distinct render grids the frame's passes recorded: each pass block's imageExtent, the world interface's for every
    // SDF pass and the resolve interface's for the reconstructing graph's last pass.
    private static uint[] Grids(UploadModelGpu gpu, int passes) {
        var sets = gpu.SetBinds!.Where(predicate: static bind => (bind.Group == ((uint)ShaderInterfaceGroup.Pass))).Select(selector: static bind => bind.Set).Distinct().ToArray();

        return [.. sets.Select(selector: (set, index) => {
            var block = gpu.Memory(bufferHandle: gpu.BufferAt(binding: 0, set: set));
            var layout = (((passes == 11) && (index == (sets.Length - 1))) ? SdfWorldInterfaces.ResolveParameters : SdfWorldInterfaces.WorldParameters);

            return BitConverter.ToUInt32(value: block, startIndex: ((int)layout.BlockOffsetOf(member: SdfWorldPackage.ImageExtent)));
        }).Distinct()];
    }
    private static long CreatedPipelines(SdfWorldPipelineCatalog pipelines) =>
        (pipelines.Pipelines.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var created) ? created : 0L);
}
