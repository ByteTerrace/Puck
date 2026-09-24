using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for the host-visible bytes <see cref="SdfWorldEngine"/> writes per frame, driven over
/// <see cref="UploadModelGpu"/>, which backs every buffer with bytes and runs the table uploader's copies: a still frame
/// writes only the viewport rows its presentation time moved; k dynamic transforms the producer's moved set owes write
/// k strides plus one run-table entry per run of adjacent slots beyond the first, in one copy dispatch however
/// scattered they are; a
/// change reaches the device-local table once and stays there through every frame in flight after it, whichever ring
/// slot those frames stage in; changes past the run bound still leave the table exact; a program edit writes only the
/// program words that changed; and an engine rebuilt after a device loss owes every table again and reads back exact.
/// </summary>
public sealed class SdfWorldEngineUploadLawTests {
    // The packed widths: a ViewportData row (sdf-world.hlsli) and a dynamic transform (sdf-vm.hlsli sdfDynamicTransforms).
    private const int DynamicTransformBytes = 48;
    private const uint Extent = 64;
    // A run-table entry, (table offset, prefix) in uints, staged only when a table owes two or more runs.
    private const int RunEntryBytes = 8;
    // SdfWorldEngine's run-table reserve at the front of each staging buffer: 256 runs × 2 uints.
    private const int RunTableReserveBytes = 2048;
    private const int ViewportBytes = 96;

    [Fact]
    public void ATablePastOneCopyDispatchIsRefusedByNameWhereItIsSized() {
        var fitting = checked((int)(SdfWorldEngine.MaxFrameUploadTableWords / (DynamicTransformBytes / sizeof(uint))));

        using (var rig = new Rig(slots: fitting)) {
            rig.Render(time: 0f);
        }

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => new Rig(slots: (fitting + 1)));

        Assert.Contains(
            expectedSubstring: "dynamic-transform",
            actualString: refusal.Message
        );
    }
    [Fact]
    public void AStillFrameWritesOnlyTheViewportRowsItsTimeMoved() {
        using var rig = new Rig(slots: 40);

        rig.Warm();
        rig.Render(time: 1f);
        Assert.Equal(expected: ((long)ViewportBytes), actual: rig.Gpu.HostBytes());
        Assert.Equal(expected: 1, actual: rig.Gpu.UploadCopies);

        rig.Render(time: 1f);
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());
        Assert.Equal(expected: 0, actual: rig.Gpu.UploadCopies);
    }
    [Fact]
    public void ChangingKTransformsWritesKStridesAndOneRunEntryPerRunInOneCopy() {
        using var rig = new Rig(slots: 40);
        ReadOnlySpan<int> changed = [3, 4, 5, 10, 20, 21];

        rig.Warm();

        foreach (var slot in changed) {
            rig.Move(slot: slot);
        }

        rig.Render(time: 0f);
        Assert.Equal(expected: ((long)((changed.Length * DynamicTransformBytes) + (3 * RunEntryBytes))), actual: rig.Gpu.HostBytes());
        Assert.Equal(expected: 1, actual: rig.Gpu.UploadCopies);
        rig.AssertDeviceTransforms();
    }
    [Fact]
    public void KScatteredChangesRecordOneCopyDispatchPerTable() {
        const int Changed = 12;

        using var rig = new Rig(slots: 40);

        rig.Warm();

        for (var slot = 1; (slot < (Changed * 3)); slot += 3) {
            rig.Move(slot: slot);
        }

        rig.Render(time: 1f);
        Assert.Equal(expected: 2, actual: rig.Gpu.UploadCopies);
        Assert.Equal(expected: ((long)(ViewportBytes + (Changed * (DynamicTransformBytes + RunEntryBytes)))), actual: rig.Gpu.HostBytes());
        rig.AssertDeviceTransforms();
    }
    [Fact]
    public void AChangeStaysOnTheDeviceThroughEveryFrameInFlightAfterIt() {
        using var rig = new Rig(slots: 40);

        rig.Warm();
        rig.Move(slot: 7);
        rig.Render(time: 0f);
        rig.AssertDeviceTransforms();

        // Every later frame stages in another ring slot, whose host buffer never received slot 7's change.
        for (var frame = 1; (frame <= SdfWorldEngine.FrameRingSize); frame++) {
            rig.Render(time: 0f);
            Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());
            rig.AssertDeviceTransforms();
        }

        // A change on the next frame lands beside the earlier one rather than over it.
        rig.Move(slot: 9);
        rig.Render(time: 0f);
        Assert.Equal(expected: ((long)DynamicTransformBytes), actual: rig.Gpu.HostBytes());
        rig.AssertDeviceTransforms();
        rig.Move(slot: 7);
        rig.Render(time: 0f);
        Assert.Equal(expected: ((long)DynamicTransformBytes), actual: rig.Gpu.HostBytes());
        rig.AssertDeviceTransforms();
    }
    [Fact]
    public void ChangesPastTheRunBoundStayExactInOneCopy() {
        // 300 separate runs, past SdfWorldEngine's 256-run bound, all within the first 600 of 1200 slots.
        const int Slots = 1200;

        using var rig = new Rig(slots: Slots);

        rig.Warm();

        for (var slot = 0; (slot < 600); slot += 2) {
            rig.Move(slot: slot);
        }

        rig.Render(time: 0f);
        Assert.Equal(expected: 1, actual: rig.Gpu.UploadCopies);
        Assert.InRange(actual: rig.Gpu.HostBytes(), high: ((600L * DynamicTransformBytes) + RunTableReserveBytes), low: (300L * DynamicTransformBytes));
        rig.AssertDeviceTransforms();
    }
    [Fact]
    public void ARebuiltEngineOwesEveryTableAndReadsBackExact() {
        using var rig = new Rig(slots: 40);

        rig.Warm();
        rig.Move(slot: 5);
        rig.Rebuild();
        rig.Render(time: 0f);

        // The rebuilt engine's first frame stages the whole dynamic table, into one ring slot's buffer.
        Assert.Equal(
            expected: (40L * DynamicTransformBytes),
            actual: rig.Gpu.HostWrites()
                .Where(predicate: write => (write.SizeBytes == ((ulong)(RunTableReserveBytes + (40 * DynamicTransformBytes)))))
                .Sum(selector: write => write.Written)
        );
        rig.AssertDeviceTransforms();
    }
    [Fact]
    public void ADeviceLossRebuildsTheNodesEngineOwingEveryTable() {
        const int Slots = 24;

        var gpu = new UploadModelGpu(reportVersion: SdfIsa.Version);
        var transforms = Transforms(slots: Slots);
        // One produced frame of a still producer: every later render owes nothing but what an engine rebuild owes.
        var moved = new SdfMovedTransforms();

        moved.Begin(
            everything: true,
            tableRows: Slots
        );

        var frame = Frame(
            program: Program(albedo: Vector3.One),
            time: 0f,
            transforms: transforms
        ) with {
            MovedTransforms = moved,
        };
        using var node = new SdfEngineNode(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: frame),
            height: Extent,
            kernels: Kernels(),
            services: new SdfViewGpuServices(
                Gpu: gpu,
                Pipelines: new SdfWorldPipelineCache()
            ),
            width: Extent
        );
        var context = new FrameContext(
            AccumulatorTicks: 0UL,
            DeltaTicks: 0UL,
            ElapsedTicks: 0UL,
            FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object> {
                [typeof(IGpuDeviceContext)] = gpu.Device,
            }),
            StepTicks: 0UL,
            TargetHeight: Extent,
            TargetWidth: Extent
        );

        _ = node.ProduceFirstFrame(context: in context);

        for (var produced = 0; (produced < SdfWorldEngine.FrameRingSize); produced++) {
            _ = node.ProduceFrame(context: in context);
        }

        gpu.ResetTallies();
        _ = node.ProduceFrame(context: in context);
        Assert.Equal(expected: 0L, actual: gpu.HostBytes());

        node.OnDeviceLost();
        gpu.ResetTallies();
        _ = node.ProduceFirstFrame(context: in context);
        Assert.Equal(
            expected: (((long)Slots) * DynamicTransformBytes),
            actual: gpu.HostWrites()
                .Where(predicate: write => (write.SizeBytes == ((ulong)(RunTableReserveBytes + (Slots * DynamicTransformBytes)))))
                .Sum(selector: write => write.Written)
        );
        Assert.Equal(
            expected: Packed(transforms: transforms),
            actual: gpu.DeviceLocal(sizeBytes: ((ulong)(Slots * DynamicTransformBytes)))
        );
    }
    [Fact]
    public void AProgramEditWritesOnlyTheProgramWordsThatChanged() {
        using var rig = new Rig(slots: 40);

        rig.Warm();

        var edited = Program(albedo: new Vector3(x: 0.25f, y: 0.5f, z: 0.75f));

        rig.Gpu.ResetTallies();
        rig.Engine.UploadProgram(program: edited);
        rig.Render(
            resetTallies: false,
            time: 0f
        );

        var programBytes = (((ulong)rig.Engine.ProgramWordCapacity) * sizeof(uint));
        var writes = rig.Gpu.HostWrites();

        Assert.Equal(expected: programBytes, actual: Assert.Single(collection: writes).SizeBytes);
        Assert.InRange(actual: writes[0].Written, high: (edited.Words.Length * sizeof(uint)), low: 1L);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: edited.Words).ToArray(),
            actual: rig.Gpu.HostVisible(sizeBytes: programBytes).AsSpan(
                length: (edited.Words.Length * sizeof(uint)),
                start: 0
            ).ToArray()
        );
    }

    // The upload kernel is the one the model GPU runs, so its copies land in the device-local tables.
    private static SdfWorldKernels Kernels() =>
        SdfTestPipelines.Kernels() with { FrameUpload = new byte[] { UploadModelGpu.FrameUploadBytecode } };
    // One 64×64 view looking at the origin, at the given time, carrying the given transforms.
    private static SdfFrame Frame(SdfProgram program, float time, DynamicTransform[] transforms) => new(
        Program: program,
        ProgramChanged: false,
        Time: time,
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
        )],
        WarpAmount: 0f
    ) {
        DynamicTransforms = transforms,
    };
    // The transforms packed as the device-local table holds them: position + shadow flag, quaternion, lanes.
    private static byte[] Packed(DynamicTransform[] transforms) {
        var packed = new float[(transforms.Length * (DynamicTransformBytes / sizeof(float)))];

        for (var slot = 0; (slot < transforms.Length); slot++) {
            var transform = transforms[slot];
            var b = (slot * 12);

            packed[(b + 0)] = transform.Position.X; packed[(b + 1)] = transform.Position.Y; packed[(b + 2)] = transform.Position.Z; packed[(b + 3)] = (transform.CastsSoftShadow ? 0f : 1f);
            packed[(b + 4)] = transform.Orientation.X; packed[(b + 5)] = transform.Orientation.Y; packed[(b + 6)] = transform.Orientation.Z; packed[(b + 7)] = transform.Orientation.W;
            packed[(b + 8)] = transform.Lanes.X; packed[(b + 9)] = transform.Lanes.Y; packed[(b + 10)] = transform.Lanes.Z; packed[(b + 11)] = transform.Lanes.W;
        }

        return MemoryMarshal.AsBytes(span: packed.AsSpan()).ToArray();
    }
    private static SdfProgram Program(Vector3 albedo) {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: albedo)),
            radius: 1f
        );

        return builder.Build();
    }
    // Distinct, nonzero poses: slot i sits at (i, 0, 0) under the identity rotation.
    private static DynamicTransform[] Transforms(int slots) {
        var transforms = new DynamicTransform[slots];

        for (var slot = 0; (slot < slots); slot++) {
            transforms[slot] = new DynamicTransform(
                Orientation: Quaternion.Identity,
                Position: new Vector3(x: slot, y: 0f, z: 0f)
            );
        }

        return transforms;
    }

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
    // A producer's moved set: every Move owes its slot on the next rendered frame, as an emitter would.
    private sealed class Rig : IDisposable {
        private readonly SdfMovedTransforms m_moved = new();
        private readonly List<int> m_pendingMoves = [];

        private readonly SdfProgram m_program;
        private readonly DynamicTransform[] m_transforms;

        private SdfWorldPipelines m_pipelines = null!;

        public Rig(int slots) {
            Gpu = new UploadModelGpu(reportVersion: SdfIsa.Version);
            m_program = Program(albedo: Vector3.One);
            m_transforms = Transforms(slots: slots);
            Engine = Build();
        }

        public SdfWorldEngine Engine { get; private set; }
        public UploadModelGpu Gpu { get; }

        // The device-local dynamic-transform table holds exactly the frame's packed transforms.
        public void AssertDeviceTransforms() => Assert.Equal(
            expected: Packed(transforms: m_transforms),
            actual: Gpu.DeviceLocal(sizeBytes: ((ulong)(m_transforms.Length * DynamicTransformBytes)))
        );
        public void Dispose() {
            Engine.Dispose();
            m_pipelines.Dispose();
        }
        // Moves one slot to a pose no earlier frame gave it.
        public void Move(int slot) {
            var transform = m_transforms[slot];

            m_transforms[slot] = transform with {
                Position = (transform.Position + new Vector3(x: 0f, y: 1f, z: 0f)),
            };
            m_pendingMoves.Add(item: slot);
        }
        // Drops the engine and builds another on the same device, as an owner does after a device loss.
        public void Rebuild() {
            Engine.Dispose();
            m_pipelines.Dispose();
            Engine = Build();
        }
        // Renders one frame at the given time, by default resetting the tallies first so they read that frame's writes.
        public void Render(float time, bool resetTallies = true) {
            if (resetTallies) {
                Gpu.ResetTallies();
            }

            m_moved.Begin(
                everything: false,
                tableRows: m_transforms.Length
            );

            foreach (var slot in m_pendingMoves) {
                m_moved.Owe(
                    count: 1,
                    start: slot
                );
            }

            m_pendingMoves.Clear();
            _ = Engine.RenderFrame(frame: Frame(
                program: m_program,
                time: time,
                transforms: m_transforms
            ) with {
                MovedTransforms = m_moved,
            });
        }
        // Renders until every ring slot has taken its first frame, so later frames read steady-state writes.
        public void Warm() {
            for (var frame = 0; (frame <= SdfWorldEngine.FrameRingSize); frame++) {
                Render(time: 0f);
            }
        }

        private SdfWorldEngine Build() {
            var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            );

            m_pipelines = SdfTestPipelines.Build(
                device: Gpu.Device,
                gpu: Gpu,
                kernels: Kernels(),
                ledger: ledger
            );

            return new SdfWorldEngine(
                device: Gpu.Device,
                gpu: Gpu,
                height: Extent,
                options: new SdfWorldEngineOptions(
                    BrickPoolVoxelCapacity: 0,
                    DynamicTransformCapacity: m_transforms.Length,
                    Program: m_program,
                    ViewportCapacity: 1,
                    WorkLedger: ledger
                ),
                pipelines: m_pipelines,
                width: Extent
            );
        }
    }
}
