using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // A pick copies V through L.x, an inspector V through N, and either one the frame's dispatch box after them.
    private const int PickRecordBytes = 32;
    private const int SurfaceRecordBytes = 48;
    private const int BoxBytes = 16;

    [Fact]
    public void PickingCopiesOnePixelOnlyOnDemandAndWaitsForCompletion() {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var current = Frame();
        using var view = new SdfTestView(device: gpu, extent: Extent, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = ContextOf(gpu: gpu);

        SdfTestPipelines.ProduceUntil(frame: () => view.Produce(context: in context),
            reason: () => view.NotReadyReason, wait: view.Residency.WaitPipelineBuilds);
        var copies = 0;
        var boxes = 0;
        var reads = 0;

        gpu.OnBufferCopy = (offset, bytes) => {
            if (bytes == PickRecordBytes) {
                Assert.Equal(actual: offset, expected: (((24UL * 32) + 8) * 64));
                copies++;
            } else if ((bytes == BoxBytes) && (offset == 0)) {
                boxes++;
            }
        };
        gpu.OnReadback = bytes => {
            if (bytes == (PickRecordBytes + BoxBytes)) {
                reads++;
            }
        };
        gpu.WriteReadback = bytes => {
            if (bytes.Length == (PickRecordBytes + BoxBytes)) {
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[12..], value: (0x123456U << 8) | 173U);
                WriteBox(box: WholeBox, bytes: bytes);
            }
        };
        _ = view.Produce(context: in context);
        Assert.Equal(actual: copies, expected: 0);
        var picker = view.Passes.PickerOf(instance: SdfTestView.Instance);

        Assert.False(condition: picker.Pending);
        var request = picker.Demand(x: 0.25f, y: 0.75f);

        Assert.Equal(expected: request, actual: picker.Demand(x: 0.25f, y: 0.75f));
        // A pending coordinate has not captured identity yet; the next frame may publish a new program before it records.
        current = Frame();
        _ = view.Produce(context: in context);
        Assert.Equal(actual: (copies, boxes), expected: (1, 1));
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
        var context = ContextOf(gpu: gpu);

        SdfTestPipelines.ProduceUntil(frame: () => view.Produce(context: in context),
            reason: () => view.NotReadyReason, wait: view.Residency.WaitPipelineBuilds);
        var copies = new List<ulong>();

        gpu.OnBufferCopy = (_, bytes) => { if (bytes is PickRecordBytes or SurfaceRecordBytes) { copies.Add(item: bytes); } };
        gpu.WriteReadback = bytes => {
            if (bytes.Length != (SurfaceRecordBytes + BoxBytes)) { return; }
            BinaryPrimitives.WriteSingleLittleEndian(destination: bytes, value: 4);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[4..], value: SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Sdf, source: 1));
            BinaryPrimitives.WriteInt32LittleEndian(destination: bytes[28..], value: SdfProgram.NoDynamicTransformSlot);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[44..], value: 32767);
            WriteBox(box: WholeBox, bytes: bytes);
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

        Assert.Equal(actual: copies, expected: new ulong[] { SurfaceRecordBytes });
        Assert.Equal(expected: captured, actual: result.Sample!.Value.Camera);
        Assert.Equal(expected: Vector3.UnitX, actual: result.Normal);
        Assert.Null(@object: result.TransformSlot);
        var tangent = captured.TanHalfFieldOfView;
        var direction = Vector3.Normalize(value: ((captured.Forward + ((tangent / 32) * captured.Right)) - ((tangent / 32) * captured.Up)));

        Assert.True(condition: (Vector3.Distance(value1: (captured.Position + (4 * direction)), value2: result.Point!.Value) < 0.00001f));
        // Ordinary hover switches back to the small copy even at the same coordinate.
        _ = picker.Demand(x: 0.5f, y: 0.5f);
        _ = view.Produce(context: in context);
        ((FakeGpuDevice.Fence)gpu.LastSubmittedFence!).Completed = true;
        _ = view.Produce(context: in context);
        Assert.Equal(actual: copies, expected: new ulong[] { SurfaceRecordBytes, PickRecordBytes });
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
    // A pointer moving every frame keeps at most one copy in flight, gets an answer every round trip, each labelled with
    // the pixel its copy sampled, and forces no render while it waits; a still pointer and no demand cost nothing.
    [Fact]
    public void AMovingPointerGetsAnAnswerEveryRoundTripWithOneCopyInFlight() {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var current = Frame();
        using var view = new SdfTestView(device: gpu, extent: Extent, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = ContextOf(gpu: gpu);

        SdfTestPipelines.ProduceUntil(frame: () => view.Produce(context: in context),
            reason: () => view.NotReadyReason, wait: view.Residency.WaitPipelineBuilds);
        var copied = new List<(uint X, uint Y)>();
        var answered = new List<(uint X, uint Y)>();
        var picker = view.Passes.PickerOf(instance: SdfTestView.Instance);

        gpu.OnBufferCopy = (offset, bytes) => {
            if (bytes == PickRecordBytes) {
                var pixel = (offset / 64UL);

                copied.Add(item: (((uint)(pixel % Extent)), ((uint)(pixel / Extent))));
            }
        };
        gpu.WriteReadback = bytes => WriteBox(box: WholeBox, bytes: bytes);
        for (var idle = 0; (idle < 4); idle++) {
            _ = view.Produce(context: in context);
        }
        Assert.Empty(collection: copied);
        Assert.False(condition: picker.Pending);
        var published = 0L;
        const int Frames = 36;

        void Observe() {
            if ((picker.Result is { } result) && ((answered.Count == 0) || (result.Request != published))) {
                published = result.Request;
                answered.Add(item: (result.X, result.Y));
            }
            Assert.InRange(actual: (copied.Count - answered.Count), high: 1, low: 0);
            Assert.False(condition: (picker.InFlight && picker.Pending));
        }

        for (var frame = 0; (frame < Frames); frame++) {
            _ = picker.Demand(x: (((frame % 30) + 0.5f) / 32f), y: 0.5f);
            _ = view.Produce(context: in context);
            Observe();
        }
        // A frame slot comes around once every frame in flight, and its fence has signaled by then.
        Assert.True(condition: (answered.Count >= ((Frames / ((int)(RenderGraphRuntime.DefaultInFlightFrames + 1))) - 1)), userMessage: $"{answered.Count} answers over {Frames} frames");
        Assert.Equal(expected: copied.Take(count: answered.Count), actual: answered);
        // The pointer stops, and demand stops: the last answer arrives and nothing more records.
        for (var frame = 0; (frame < 8); frame++) {
            _ = view.Produce(context: in context);
            Observe();
        }
        var settled = copied.Count;

        Assert.Equal(expected: settled, actual: answered.Count);
        Assert.False(condition: picker.Pending);
        Assert.False(condition: picker.InFlight);
        for (var frame = 0; (frame < 4); frame++) {
            _ = view.Produce(context: in context);
        }
        Assert.Equal(expected: settled, actual: copied.Count);
    }
    [Fact]
    public void AOneShotRequestSurvivesARecorderReinstallAndCompletes() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var current = Frame();
        using var view = new SdfTestView(device: gpu, extent: Extent, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = ContextOf(gpu: gpu);

        SdfTestPipelines.ProduceUntil(frame: () => view.Produce(context: in context),
            reason: () => view.NotReadyReason, wait: view.Residency.WaitPipelineBuilds);
        gpu.WriteReadback = bytes => WriteBox(box: WholeBox, bytes: bytes);
        var node = view.Runtime.Node(instance: 0);
        var installed = node.Plan;
        var picker = view.Passes.PickerOf(instance: SdfTestView.Instance);

        // A separate render grid adds the resolve pass, so the node installs a new graph with new recorders and retires
        // the ones the request was made against.
        current = current with { Views = [current.Views[0] with { RenderScale = 0.5f, ResolvedRenderScale = 0.5f }] };
        var request = picker.Request(x: 0.25f, y: 0.75f);

        SdfTestPipelines.ProduceUntil(frame: () => {
            _ = view.Produce(context: in context);

            return (!ReferenceEquals(objA: node.Plan, objB: installed) && !node.IsBuildingCandidate);
        }, reason: () => node.LastSwapError?.ToString(), wait: view.Residency.WaitPipelineBuilds);
        for (var frame = 0; (frame < 4); frame++) {
            _ = view.Produce(context: in context);
        }
        Assert.Equal(expected: request, actual: picker.Result?.Request);
    }
    // A content revision between requests withdraws the answer that named the older content but cancels nothing: the
    // next request is the next identity, however many revisions arrived while nothing was asked.
    [Fact]
    public void AContentRevisionBetweenRequestsCancelsNothing() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var current = Frame();
        using var view = new SdfTestView(device: gpu, extent: Extent, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = ContextOf(gpu: gpu);

        SdfTestPipelines.ProduceUntil(frame: () => view.Produce(context: in context),
            reason: () => view.NotReadyReason, wait: view.Residency.WaitPipelineBuilds);
        gpu.WriteReadback = bytes => WriteBox(box: WholeBox, bytes: bytes);
        var picker = view.Passes.PickerOf(instance: SdfTestView.Instance);
        var first = picker.Request(x: 0.25f, y: 0.75f);

        _ = view.Produce(context: in context);
        _ = view.Produce(context: in context);
        Assert.Equal(expected: first, actual: picker.Result?.Request);
        for (var revision = 0; (revision < 3); revision++) {
            current = Frame();
            _ = view.Produce(context: in context);
        }
        Assert.Null(@object: picker.Result);
        var second = picker.Request(x: 0.25f, y: 0.75f);

        Assert.Equal(actual: second, expected: (first + 1));
        _ = view.Produce(context: in context);
        _ = view.Produce(context: in context);
        Assert.Equal(expected: second, actual: picker.Result?.Request);
    }

    private static readonly uint[] WholeBox = [0, 0, (Extent / 8), (Extent / 8)];

    private static FrameContext ContextOf(IGpuDeviceContext gpu) => new(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
        Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
        StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);
    // The dispatch box a readback carries after its record.
    private static void WriteBox(Span<byte> bytes, ReadOnlySpan<uint> box) {
        for (var word = 0; (word < box.Length); word++) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[((bytes.Length - BoxBytes) + (word * sizeof(uint)))..], value: box[word]);
        }
    }
}
