using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void VariableRenderGridKeepsOutputAndCeilingStorageWhilePickingUsesTheActiveStride() {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var current = Frame();

        current = current with { Views = [current.Views[0] with { RenderScale = 0.75f, ResolvedRenderScale = 0.5f, UpscaleSharpness = 0.4f }] };
        using var view = new SdfTestView(device: gpu, extent: Extent, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);

        TestLiveness.Until(step: () => view.Produce(context: in context), reason: () => (view.Runtime.Node(instance: 0).LastSwapError?.ToString() ?? view.NotReadyReason),
            wait: view.Residency.WaitPipelineBuilds);
        var node = view.Runtime.Node(instance: 0);

        Assert.Equal(expected: (Extent, Extent), actual: node.Extent);
        Assert.Contains(collection: node.Plan!.Passes, filter: static pass => pass.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: "$resolve"));
        var storage = gpu.DeviceLocal(part: "sdf.world$visibility", sizeBytes: ((24UL * 24) * 64));

        Assert.Equal(expected: ((24 * 24) * 64), actual: storage.Length);
        // No kernel runs on the model, so the frame's dispatch box is written here: the whole grid is current.
        Buffer.BlockCopy(count: BoxBytes, dst: gpu.DeviceLocal(part: "sdf.world$cullBounds", sizeBytes: BoxBytes), dstOffset: 0, src: WholeBox, srcOffset: 0);
        var picker = view.Passes.PickerOf(instance: SdfTestView.Instance);
        // Picking allocates its tiny readback only on demand; warm every frame slot before measuring scale changes.
        for (var warm = 0; (warm < 4); warm++) {
            _ = picker.Request(x: 0.25f, y: 0.75f);
            _ = view.Produce(context: in context);
        }
        _ = view.Produce(context: in context);
        _ = view.Produce(context: in context);
        var createdBuffers = gpu.BufferBytes;
        var createdPools = gpu.PoolsCreated.Count;
        var revision = node.WorkRevision;

        foreach (var (scale, pixels) in new[] { (0.5f, 16u), (0.625f, 20u), (0.75f, 24u), (0.5f, 16u) }) {
            current = current with { Views = [current.Views[0] with { ResolvedRenderScale = scale }], Time = (current.Time + 1f) };
            gpu.SetBinds = [];
            _ = view.Produce(context: in context);
            var row = view.Runtime.Latest!.Instances[0];

            // The nine passes through views and the sky at the active grid, the resolve and the composite at the output.
            Assert.Equal(expected: 12, actual: row.Passes);
            Assert.Equal(expected: (((10L * pixels) * pixels) + (2 * (Extent * Extent))), actual: row.PassPixels);
            // The resolve's pass set, the third from the frame's last: the sky and the composite follow it.
            var set = gpu.SetBinds.Where(predicate: static bind => (bind.Group == ((uint)ShaderInterfaceGroup.Pass))).Select(selector: static bind => bind.Set).Distinct().ToArray()[^3];

            gpu.SetBinds = null;
            var block = gpu.Memory(bufferHandle: gpu.BufferAt(binding: 0, set: set));
            var parameters = SdfWorldInterfaces.ResolveParameters;

            Assert.Equal(expected: Extent, actual: BitConverter.ToUInt32(startIndex: 0, value: block));
            Assert.Equal(expected: pixels, actual: BitConverter.ToUInt32(value: block, startIndex: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.ImageExtent))));
            Assert.Equal(expected: 0.4f, actual: BitConverter.ToSingle(value: block, startIndex: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.UpscaleSharpness))));
            Assert.Equal(expected: createdBuffers, actual: gpu.BufferBytes);
            Assert.Equal(expected: createdPools, actual: gpu.PoolsCreated.Count);
            Assert.Equal(expected: revision, actual: node.WorkRevision);
            // The picker copies one record from the active packed grid, independently of output and allocation size.
            Array.Clear(array: storage);
            var offset = checked((int)(((((pixels * 3) / 4) * pixels) + (pixels / 4)) * 64));

            BitConverter.GetBytes(value: 123f).CopyTo(array: storage, index: offset);
            _ = picker.Demand(x: 0.25f, y: 0.75f);
            _ = view.Produce(context: in context);
            _ = view.Produce(context: in context);
            Assert.Equal(expected: 123f, actual: picker.Result!.Value.Distance);
        }
        // Changing the ceiling rebuilds allocation while preserving the published output. Switching off the separate
        // grid removes the resolve and its scratch; switching back selects the same declared resolve topology.
        foreach (var (ceiling, active, passes, renderPixels) in new[] {
            (0.5f, 0.25f, 12, 8u), (1f, 0f, 11, 32u), (0.75f, 0.5f, 12, 16u),
        }) {
            current = current with { Views = [current.Views[0] with { RenderScale = ceiling, ResolvedRenderScale = active }], Time = (current.Time + 1f) };
            TestLiveness.Until(step: () => {
                if (!view.Produce(context: in context) || (node.Plan!.Passes.Count != passes)) {
                    return false;
                }
                var passSet = gpu.BoundSet(group: ((uint)ShaderInterfaceGroup.Pass));
                var passBlock = gpu.Memory(bufferHandle: gpu.BufferAt(binding: 0, set: passSet));
                // The frame's last pass, the composite, binds the sky interface in either graph.
                var layout = SdfWorldInterfaces.SkyParameters;

                return (BitConverter.ToUInt32(value: passBlock, startIndex: ((int)layout.BlockOffsetOf(member: SdfWorldPackage.ImageExtent))) == renderPixels);
            }, reason: () => node.LastSwapError?.ToString(), wait: view.Residency.WaitPipelineBuilds);
            Assert.Equal(expected: (Extent, Extent), actual: node.Extent);
            Assert.Equal(expected: (passes == 12), actual: node.Plan!.Storages.Any(predicate: static item => item.Versions.Any(predicate: static version => version.EndsWith(comparisonType: StringComparison.Ordinal, value: $"${SdfWorldPackage.CurrentColor}"))));
        }
        current = current with { EnableCadenceGate = true };
        _ = view.Produce(context: in context);
        _ = view.Produce(context: in context);
        Assert.Equal(expected: RenderGraphInstanceStatus.Waiting, actual: view.Runtime.Latest!.Instances[0].Status);
        // The resolve-only value is outside the native pass signature, but changing it must still render once.
        current = current with { Views = [current.Views[0] with { UpscaleSharpness = 0.9f }] };
        _ = view.Produce(context: in context);
        Assert.Equal(expected: RenderGraphInstanceStatus.Rendered, actual: view.Runtime.Latest!.Instances[0].Status);
        _ = view.Produce(context: in context);
        Assert.Equal(expected: RenderGraphInstanceStatus.Waiting, actual: view.Runtime.Latest!.Instances[0].Status);
        Assert.Empty(collection: gpu.StateConflicts);
    }
}
