using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void PickingCopiesOnePixelOnlyOnDemandAndWaitsForCompletion() {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var current = Frame();
        using var view = new SdfTestView(device: gpu, extent: Extent, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);

        Assert.True(condition: SpinWait.SpinUntil(condition: () => view.Produce(context: in context),
            timeout: TimeSpan.FromSeconds(value: 30)), userMessage: view.NotReadyReason);
        var copies = 0;
        var reads = 0;

        gpu.OnBufferCopy = (offset, bytes) => {
            if (bytes == 16) {
                Assert.Equal(actual: offset, expected: (((24UL * 32) + 8) * 64));
                copies++;
            }
        };
        gpu.OnReadback = bytes => {
            if (bytes == 16) {
                reads++;
            }
        };
        _ = view.Produce(context: in context);
        Assert.Equal(actual: copies, expected: 0);
        var picker = view.Passes.PickerOf(instance: SdfTestView.Instance);
        var request = picker.Demand(x: 0.25f, y: 0.75f);

        Assert.Equal(expected: request, actual: picker.Demand(x: 0.25f, y: 0.75f));
        // A pending coordinate has not captured identity yet; the next frame may publish a new program before it records.
        current = Frame();
        _ = view.Produce(context: in context);
        Assert.Equal(actual: copies, expected: 1);
        var fence = Assert.IsType<FakeGpuDevice.Fence>(@object: gpu.LastSubmittedFence);

        _ = view.Produce(context: in context);
        Assert.Null(@object: picker.Result);
        Assert.Equal(actual: reads, expected: 0);
        fence.Completed = true;
        _ = view.Produce(context: in context);
        Assert.Equal(expected: request, actual: picker.Result!.Value.Request);
        Assert.Same(expected: current.Program, actual: picker.Result.Value.Program);
        Assert.Equal(actual: reads, expected: 1);
        Assert.Equal(actual: copies, expected: 1);
        // The pointer does not move, but a new rendered camera/pose frame must refresh the completed hover.
        current = current with { Time = 1, MeshDrawsRevision = 1 };
        var next = picker.Demand(x: 0.25f, y: 0.75f);

        Assert.True(condition: (next > request));
        Assert.Equal(expected: request, actual: picker.Result!.Value.Request);
        _ = view.Produce(context: in context);
        Assert.Equal(actual: copies, expected: 2);
        picker.Clear();
        ((FakeGpuDevice.Fence)gpu.LastSubmittedFence!).Completed = true;
        _ = view.Produce(context: in context);
        Assert.Null(@object: picker.Result);
        Assert.Equal(actual: copies, expected: 2);
        var lost = picker.Demand(x: 0.25f, y: 0.75f);

        view.Passes.OnDeviceLost();
        Assert.True(condition: (picker.Demand(x: 0.25f, y: 0.75f) > lost));
        Assert.Null(@object: picker.Result);
    }
}
