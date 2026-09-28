using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Cameras;
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
        gpu.WriteReadback = bytes => {
            if (bytes.Length == 16) {
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[12..], value: (0x123456U << 8) | 173U);
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
        Assert.Equal(expected: 173U, actual: picker.Result.Value.Steps);
        Assert.Equal(expected: 0x123456U, actual: picker.Result.Value.Queries);
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
    [Fact]
    public void InspectorCapturesSurfaceWithItsCameraAndDiscardsCuts() {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var current = Frame();
        var captured = current.Views[0].Camera;
        using var view = new SdfTestView(device: gpu, extent: Extent, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);

        Assert.True(condition: SpinWait.SpinUntil(condition: () => view.Produce(context: in context),
            timeout: TimeSpan.FromSeconds(value: 30)), userMessage: view.NotReadyReason);
        var copies = new List<ulong>();

        gpu.OnBufferCopy = (_, bytes) => { if (bytes is 16 or 48) { copies.Add(item: bytes); } };
        gpu.WriteReadback = bytes => {
            if (bytes.Length != 48) { return; }
            BinaryPrimitives.WriteSingleLittleEndian(destination: bytes, value: 4);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[4..], value: 0x40000001);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[44..], value: 32767);
        };
        var picker = view.Passes.PickerOf(instance: SdfTestView.Instance);

        _ = picker.Demand(surface: true, x: 0.5f, y: 0.5f);
        _ = view.Produce(context: in context);
        var fence = Assert.IsType<FakeGpuDevice.Fence>(@object: gpu.LastSubmittedFence);

        current = current with {
            Views = [current.Views[0] with { Camera = CameraSnapshot.LookAt(
            position: new Vector3(x: 100, y: 0, z: -5), target: Vector3.Zero,
            fieldOfViewRadians: 1, viewportWidth: Extent, viewportHeight: Extent) }],
        };
        _ = view.Produce(context: in context);
        Assert.Null(@object: picker.Result);
        fence.Completed = true;
        _ = view.Produce(context: in context);
        var result = picker.Result!.Value;

        Assert.Equal(actual: copies, expected: new ulong[] { 48 });
        Assert.Equal(expected: captured, actual: result.Sample!.Value.Camera);
        Assert.Equal(expected: Vector3.UnitX, actual: result.Normal);
        var tangent = captured.TanHalfFieldOfView;
        var direction = Vector3.Normalize(value: ((captured.Forward + ((tangent / 32) * captured.Right)) - ((tangent / 32) * captured.Up)));

        Assert.True(condition: (Vector3.Distance(value1: (captured.Position + (4 * direction)), value2: result.Point!.Value) < 0.00001f));
        // Ordinary hover switches back to the small copy even at the same coordinate.
        _ = picker.Demand(x: 0.5f, y: 0.5f);
        _ = view.Produce(context: in context);
        ((FakeGpuDevice.Fence)gpu.LastSubmittedFence!).Completed = true;
        _ = view.Produce(context: in context);
        Assert.Equal(actual: copies, expected: new ulong[] { 48, 16 });
        Assert.Null(@object: picker.Result!.Value.Sample);
        Assert.Equal(expected: Vector3.Zero, actual: picker.Result.Value.Normal);
        _ = picker.Demand(surface: true, x: 0.5f, y: 0.5f);
        _ = view.Produce(context: in context);
        var stale = ((FakeGpuDevice.Fence)gpu.LastSubmittedFence!);

        current = current with { Views = [current.Views[0] with { CutRevision = 1 }] };
        _ = view.Produce(context: in context);
        stale.Completed = true;
        _ = view.Produce(context: in context);
        Assert.Null(@object: picker.Result);
        _ = picker.Demand(surface: true, x: 0.5f, y: 0.5f);
        view.Passes.OnDeviceLost();
        Assert.Null(@object: picker.Result);
    }


}
