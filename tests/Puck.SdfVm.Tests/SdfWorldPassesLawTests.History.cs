using Puck.Abstractions.Gpu;
using Puck.Abstractions.Cameras;
using System.Numerics;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void ATemporalViewConvergesThenStandsAndResetsReuseItsHistoryStorage() {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var current = Frame();

        current = current with { Views = [current.Views[0] with { Temporal = true }] };
        using var view = new SdfTestView(device: gpu, extent: Extent, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);
        var node = view.Runtime.Node(instance: 0);

        Assert.True(condition: SpinWait.SpinUntil(condition: () => view.Produce(context: in context), timeout: TimeSpan.FromSeconds(seconds: 30)),
            userMessage: (node.LastSwapError?.ToString() ?? view.NotReadyReason));
        for (var warm = 0; (warm < 4); warm++) { _ = view.Produce(context: in context); }
        Assert.Equal(expected: 2, actual: node.Plan!.Storages.Count(predicate: static storage => storage.Declaration.History));
        Assert.Contains(collection: node.Plan.Storages, filter: static storage => storage.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: "$reactivity"));
        var buffers = gpu.BufferBytes;
        var pools = gpu.PoolsCreated.Count;
        var revision = node.WorkRevision;

        current = current with { EnableCadenceGate = true, Views = [current.Views[0] with { CutRevision = 1 }] };
        for (uint sample = 0; (sample < 8); sample++) {
            ProduceSample(expected: sample);
        }
        _ = view.Produce(context: in context);
        Assert.Equal(expected: RenderGraphInstanceStatus.Waiting, actual: view.Runtime.Latest!.Instances[0].Status);
        MoveCamera();
        ProduceSample(expected: 0);
        // A second content change while rendering retains correspondence but starts eight new stable samples.
        MoveCamera();
        for (uint sample = 1; (sample <= 8); sample++) { ProduceSample(expected: sample); }
        _ = view.Produce(context: in context);
        Assert.Equal(expected: RenderGraphInstanceStatus.Waiting, actual: view.Runtime.Latest!.Instances[0].Status);
        current = current with { Views = [current.Views[0] with { CutRevision = 2 }] };
        ProduceSample(expected: 0);
        ProduceSample(expected: 1);
        current = current with { Program = Frame().Program, ProgramChanged = true };
        ProduceSample(expected: 0);
        current = current with { ProgramChanged = false };
        ProduceSample(expected: 1);
        Assert.Equal(expected: buffers, actual: gpu.BufferBytes);
        Assert.Equal(expected: pools, actual: gpu.PoolsCreated.Count);
        Assert.Equal(expected: revision, actual: node.WorkRevision);
        Assert.Empty(collection: gpu.StateConflicts);

        void MoveCamera() {
            var camera = current.Views[0].Camera;

            current = current with {
                Views = [current.Views[0] with { Camera = new CameraSnapshot(
                Position: (camera.Position + Vector3.UnitX), Right: camera.Right, Up: camera.Up,
                Forward: camera.Forward, TanHalfFieldOfView: camera.TanHalfFieldOfView, AspectRatio: camera.AspectRatio) }],
            };
        }
        void ProduceSample(uint expected) {
            _ = view.Produce(context: in context);
            Assert.Equal(expected: RenderGraphInstanceStatus.Rendered, actual: view.Runtime.Latest!.Instances[0].Status);
            var block = gpu.Memory(bufferHandle: gpu.BufferAt(set: gpu.BoundSet(group: 3), binding: 0));

            Assert.Equal(expected: expected, actual: BitConverter.ToUInt32(value: block,
                startIndex: ((int)SdfWorldInterfaces.TemporalResolveParameters.BlockOffsetOf(member: SdfWorldPackage.HistoryFrames))));
        }
    }
}
