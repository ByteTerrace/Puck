using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for the host-visible bytes <see cref="SdfWorldEngine"/> writes per frame, driven over
/// <see cref="UploadModelGpu"/>, which backs every buffer with bytes and runs the region copies. Its default memory
/// profile stages every region, so each copy writes a four-word header, one run-table entry per run and the owed words:
/// a still frame writes only the viewport word its presentation time moved; k dynamic transforms the producer's moved
/// set owes write the words of each that changed, in one copy dispatch however scattered they are; a change reaches the
/// device-local buffer once and stays there through every frame in flight after it, whichever ring slot those frames
/// stage in; changes past the run bound still leave the table exact; a program edit writes only the program words that
/// changed; the program region holds the live program rather than the reserve and grows by half again past it; and an
/// engine rebuilt after a device loss owes every table again and reads back exact.
/// </summary>
public sealed class SdfWorldEngineUploadLawTests {
    // The packed width of a dynamic transform (sdf-vm.hlsli sdfDynamicTransforms).
    private const int DynamicTransformBytes = 48;
    private const uint Extent = 64;
    // A staged copy's header: count, run count, block base and destination word.
    private const int HeaderBytes = (GpuRegion.CopyHeaderWords * sizeof(uint));
    // A run-table entry, (block offset, first thread) in uints, staged for every run a copy carries.
    private const int RunEntryBytes = 8;
    // The header and run-table reserve at the front of each staging buffer: 256 runs × 2 uints.
    private const int StagingReserveBytes = (HeaderBytes + (GpuRegion.MaxCopyRuns * RunEntryBytes));

    [Fact]
    public void AProgramPastOneDispatchRowUploadsByteExact() {
        // Past one row of 65,535 groups of 64 threads, the copy dispatches a second row; the fake refuses a dispatch
        // that is not exactly GpuRegion.CopyGroups and runs the kernel's thread numbering.
        using var rig = new Rig(slots: 1);
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // Each sphere is one instruction of 12 words.
        for (var sphere = 0; (sphere < ((((int)GpuRegion.CopyRowThreads) / 12) + 4096)); sphere++) {
            builder.Sphere(
                material: material,
                radius: 1f
            );
        }

        var large = builder.Build();

        Assert.True(condition: (large.Words.Length > GpuRegion.CopyRowThreads));
        Assert.True(condition: (GpuRegion.CopyGroups(count: ((uint)large.Words.Length)).Y >= 2U));
        rig.Warm();
        rig.Engine.UploadProgram(program: large);
        rig.Render(time: 0f);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: large.Words).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: (((ulong)large.Words.Length) * sizeof(uint)))
        );
    }
    [Fact]
    public void RingRegionsLiveInTheApertureOnADiscreteAdapterAndInHostMemoryOnUnifiedMemory() {
        const ulong GiB = (1UL << 30);
        var discrete = new GpuMemoryProfile(
            CoherentUnifiedMemory: false,
            DeviceLocalBytes: (12UL * GiB),
            HostVisibleDeviceLocalBytes: (12UL * GiB),
            LargestDeviceLocalHeapBytes: (12UL * GiB),
            UnifiedMemory: false
        );
        var unified = new GpuMemoryProfile(
            CoherentUnifiedMemory: true,
            DeviceLocalBytes: (8UL * GiB),
            HostVisibleDeviceLocalBytes: (8UL * GiB),
            LargestDeviceLocalHeapBytes: (8UL * GiB),
            UnifiedMemory: true
        );

        // Eight per-frame tables and two blocks, the frame block and the one view's world block, each a ring of one buffer
        // per slot, and nothing staged or copied.
        using (var rig = new Rig(profile: discrete, slots: 40)) {
            rig.Warm();
            Assert.Equal(expected: (10 * SdfWorldEngine.FrameRingSize), actual: rig.Gpu.ApertureBuffers);
            rig.Move(slot: 3);
            rig.Render(time: 0f);
            Assert.Equal(expected: 0, actual: rig.Gpu.UploadCopies);
        }

        using (var rig = new Rig(profile: unified, slots: 40)) {
            rig.Warm();
            Assert.Equal(expected: 0, actual: rig.Gpu.ApertureBuffers);
            Assert.Equal(expected: 0, actual: rig.Gpu.UploadCopies);
        }

        Assert.Equal(expected: GpuHostVisibleMemory.DeviceLocal, actual: GpuResidency.RingMemory(profile: discrete));
        Assert.Equal(expected: GpuHostVisibleMemory.Host, actual: GpuResidency.RingMemory(profile: unified));
    }
    [Fact]
    public void AStillFrameWritesOnlyTheViewportWordItsTimeMoved() {
        using var rig = new Rig(slots: 40);

        rig.Warm();
        rig.Render(time: 1f);
        Assert.Equal(expected: ((long)((HeaderBytes + RunEntryBytes) + sizeof(float))), actual: rig.Gpu.HostBytes());
        Assert.Equal(expected: 1, actual: rig.Gpu.UploadCopies);
        // Every rendered frame sends its frame block whole, one constant-buffer view; the view's world block owes nothing
        // while no world value moves.
        Assert.Equal(expected: ((long)IGpuBindings.ConstantBufferAlignment), actual: rig.Gpu.BlockBytes());

        rig.Render(time: 1f);
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());
        Assert.Equal(expected: 0, actual: rig.Gpu.UploadCopies);
        Assert.Equal(expected: ((long)IGpuBindings.ConstantBufferAlignment), actual: rig.Gpu.BlockBytes());
    }
    [Fact]
    public void ChangingKTransformsWritesTheWordsThatMovedAndOneRunEntryPerRunInOneCopy() {
        using var rig = new Rig(slots: 40);
        ReadOnlySpan<int> changed = [3, 4, 5, 10, 20, 21];

        rig.Warm();

        foreach (var slot in changed) {
            rig.Move(slot: slot);
        }

        rig.Render(time: 0f);
        // Each move changes one word, a slot's position.y; slots a stride apart are separate runs.
        Assert.Equal(expected: ((long)(HeaderBytes + (changed.Length * (RunEntryBytes + sizeof(float))))), actual: rig.Gpu.HostBytes());
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
        Assert.Equal(expected: ((long)((2 * HeaderBytes) + ((Changed + 1) * (RunEntryBytes + sizeof(float))))), actual: rig.Gpu.HostBytes());
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
        Assert.Equal(expected: ((long)((HeaderBytes + RunEntryBytes) + sizeof(float))), actual: rig.Gpu.HostBytes());
        rig.AssertDeviceTransforms();
        rig.Move(slot: 7);
        rig.Render(time: 0f);
        Assert.Equal(expected: ((long)((HeaderBytes + RunEntryBytes) + sizeof(float))), actual: rig.Gpu.HostBytes());
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
        Assert.InRange(actual: rig.Gpu.HostBytes(), high: ((600L * DynamicTransformBytes) + StagingReserveBytes), low: (300L * sizeof(float)));
        rig.AssertDeviceTransforms();
    }
    [Fact]
    public void ARebuiltEngineOwesEveryTableAndReadsBackExact() {
        using var rig = new Rig(slots: 40);

        rig.Warm();
        rig.Move(slot: 5);
        rig.Rebuild();
        rig.Render(time: 0f);

        // The rebuilt engine's first frame stages the whole dynamic table as one run, into one ring slot's buffer.
        Assert.Equal(
            expected: (((40L * DynamicTransformBytes) + HeaderBytes) + RunEntryBytes),
            actual: rig.Gpu.HostWrites()
                .Where(predicate: write => (write.SizeBytes == ((ulong)(StagingReserveBytes + (40 * DynamicTransformBytes)))))
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
            kernels: SdfTestPipelines.Kernels(),
            pipelines: SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode),
            width: Extent
        );
        var context = new FrameContext(
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
            expected: (((((long)Slots) * DynamicTransformBytes) + HeaderBytes) + RunEntryBytes),
            actual: gpu.HostWrites()
                .Where(predicate: write => (write.SizeBytes == ((ulong)(StagingReserveBytes + (Slots * DynamicTransformBytes)))))
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

        // One ring slot's program staging buffer takes the changed words, their runs and the header, and the copy
        // leaves the device-local program exactly the edited words.
        Assert.Equal(expected: (programBytes + StagingReserveBytes), actual: Assert.Single(collection: writes).SizeBytes);
        Assert.InRange(actual: writes[0].Written, high: (StagingReserveBytes + (edited.Words.Length * sizeof(uint))), low: ((HeaderBytes + RunEntryBytes) + sizeof(uint)));
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: edited.Words).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: programBytes).AsSpan(
                length: (edited.Words.Length * sizeof(uint)),
                start: 0
            ).ToArray()
        );
    }
    [Fact]
    public void TheProgramRegionHoldsTheLiveProgramAndGrowsByHalfAgainPastIt() {
        const int Reserve = (1 << 20);
        using var rig = new Rig(
            programWordReserve: Reserve,
            slots: 1
        );
        var words = Program(albedo: Vector3.One).Words.Length;

        rig.Warm();

        // The reserve allocates nothing; the engine reports it as the words it is provisioned for.
        Assert.Equal(expected: Reserve, actual: rig.Engine.ProgramWordCapacity);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => rig.Gpu.DeviceLocal(sizeBytes: (Reserve * sizeof(uint))));
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: Program(albedo: Vector3.One).Words).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: ((ulong)(words * sizeof(uint))))
        );

        // A larger program grows the region by half again, and the grown region holds it whole.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        for (var sphere = 0; (sphere < 4); sphere++) {
            builder.Sphere(
                material: material,
                radius: (1f + sphere)
            );
        }

        var larger = builder.Build();
        var grown = Math.Max(
            val1: larger.Words.Length,
            val2: (words + (words / 2))
        );

        Assert.True(condition: (larger.Words.Length > words));
        rig.Engine.UploadProgram(program: larger);
        rig.Render(time: 0f);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: larger.Words).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: ((ulong)(grown * sizeof(uint))))[..(larger.Words.Length * sizeof(uint))]
        );
        Assert.Equal(expected: Reserve, actual: rig.Engine.ProgramWordCapacity);
    }
    [Fact]
    public void TheMeshRegionHoldsAKnownDrawSetAndOwesOnlyTheWordsANewSetChanges() {
        using var rig = new Rig(slots: 1);
        var quad = new SdfMesh(
            indices: new uint[] { 0, 1, 2, 0, 2, 3 },
            positions: new Vector3[] { new(x: 0f, y: 0f, z: 0f), new(x: 1f, y: 0f, z: 0f), new(x: 1f, y: 1f, z: 0f), new(x: 0f, y: 1f, z: 0f) }
        );
        var triangle = new SdfMesh(
            indices: new uint[] { 0, 1, 2 },
            positions: new Vector3[] { new(x: 0f, y: 0f, z: 0f), new(x: 0f, y: 0f, z: 1f), new(x: 1f, y: 0f, z: 0f) }
        );
        var moved = Matrix4x4.CreateTranslation(xPosition: 1f, yPosition: 2f, zPosition: 3f);
        SdfMeshDraw[] draws = [
            new(Material: 4, Mesh: quad, ObjectToWorld: moved),
            new(Material: 5, Mesh: triangle, ObjectToWorld: Matrix4x4.CreateScale(scale: 2f)),
            new(Material: 6, Mesh: quad, ObjectToWorld: Matrix4x4.Identity),
        ];
        var layout = new SdfMeshRegionLayout(
            DrawCount: 3,
            IndexCount: 9,
            VertexCount: 7
        );
        var expected = new List<uint>();

        // A draw's record: its matrix row by row, its material, then its mesh's first index, index count and base vertex.
        void Record(Matrix4x4 matrix, uint material, uint firstIndex, uint indexCount, uint baseVertex) {
            float[] rows = [
                matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                matrix.M41, matrix.M42, matrix.M43, matrix.M44,
            ];

            expected.AddRange(collection: rows.Select(selector: BitConverter.SingleToUInt32Bits));
            expected.AddRange(collection: [material, firstIndex, indexCount, baseVertex]);
        }

        Record(baseVertex: 0, firstIndex: 0, indexCount: 6, material: 4, matrix: moved);
        Record(matrix: Matrix4x4.CreateScale(scale: 2f), material: 5, firstIndex: 6, indexCount: 3, baseVertex: 4);
        Record(matrix: Matrix4x4.Identity, material: 6, firstIndex: 0, indexCount: 6, baseVertex: 0);

        foreach (var position in quad.Positions.ToArray().Concat(second: triangle.Positions.ToArray())) {
            expected.AddRange(collection: [
                BitConverter.SingleToUInt32Bits(value: position.X),
                BitConverter.SingleToUInt32Bits(value: position.Y),
                BitConverter.SingleToUInt32Bits(value: position.Z),
            ]);
        }

        expected.AddRange(collection: quad.Indices.ToArray().Concat(second: triangle.Indices.ToArray()));

        rig.Warm();
        rig.Render(time: 0f);

        var stillBytes = rig.Gpu.HostBytes();

        rig.Render(
            meshDraws: draws,
            time: 0f
        );

        Assert.Equal(expected: layout, actual: rig.Engine.MeshRegionLayout);
        Assert.Equal(expected: layout.Bytes, actual: SdfMeshRegion.BytesOf(draws: draws));
        Assert.Equal(expected: layout.Bytes, actual: rig.Engine.MeshRegionBytes);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: expected.ToArray().AsSpan()).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: layout.Bytes)
        );

        // The same list again repacks nothing, and a list moving one draw owes the one word its translation changed.
        rig.Render(
            meshDraws: draws,
            time: 0f
        );
        Assert.Equal(expected: stillBytes, actual: rig.Gpu.HostBytes());

        SdfMeshDraw[] shifted = [draws[0], draws[1], (draws[2] with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 0f, yPosition: 0f, zPosition: 7f) })];

        rig.Render(
            meshDraws: shifted,
            time: 0f
        );
        Assert.Equal(expected: (((stillBytes + HeaderBytes) + RunEntryBytes) + sizeof(uint)), actual: rig.Gpu.HostBytes());

        var shiftedWords = rig.Gpu.DeviceLocal(sizeBytes: layout.Bytes);

        Assert.Equal(
            expected: BitConverter.SingleToUInt32Bits(value: 7f),
            actual: BitConverter.ToUInt32(startIndex: ((((2 * SdfMeshRegion.DrawWords) + 14) * sizeof(uint))), value: shiftedWords)
        );
    }

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
        )]
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

    /// <summary>Every region's copy sets are reserved when the engine is built, beside its pool, so another owner taking
    /// every descriptor range left after that cannot refuse a region the frame thread creates or replaces: the first
    /// frame that draws a mesh, a frame that grows the mesh region, and program uploads that grow the program region and
    /// the instance grid all stage with no pool created after construction.</summary>
    [Fact]
    public void RegionsCreatedOrGrownAfterConstructionTakeNoDescriptorRangeAnotherOwnerCouldHaveFilled() {
        var heap = new GpuDescriptorHeapBudget(capabilities: (GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: 3,
            rootSignatureVersion: "1.1",
            samplerHeapSize: 0,
            shaderModel: "6.6",
            staticSamplerHeapSize: 0,
            viewHeapSize: 0
        ) with {
            ViewHeapSize = 65536U,
        }));

        using var rig = new Rig(
            heap: heap,
            slots: 1
        );

        // The engine created exactly the pools its admission states: its own and the one copy pool of every region.
        Assert.Equal(
            actual: rig.Gpu.PoolsCreated.Count,
            expected: 2
        );
        Assert.Equal(
            actual: rig.Gpu.PoolsCreated.CountBy(keySelector: static pool => pool).ToDictionary(),
            expected: SdfWorldEngine.DescriptorPools(brickPool: false, viewportCapacity: 1).CountBy(keySelector: static pool => pool).ToDictionary()
        );

        var pools = rig.Gpu.PoolsCreated.Count;

        // Another owner takes every range the engine left.
        if (heap.FreeViewDescriptors > 0U) {
            Assert.True(condition: heap.TryAdmit(
                admission: out _,
                owner: "another owner",
                pools: [new GpuDescriptorPoolSizes(
                    MaxSets: 1U,
                    StorageBufferCount: heap.FreeViewDescriptors,
                    StorageImageCount: 0U
                )],
                refusal: out var refusal
            ), userMessage: refusal);
        }

        var quad = new SdfMesh(
            indices: new uint[] { 0, 1, 2, 0, 2, 3 },
            positions: new Vector3[] { new(x: 0f, y: 0f, z: 0f), new(x: 1f, y: 0f, z: 0f), new(x: 1f, y: 1f, z: 0f), new(x: 0f, y: 1f, z: 0f) }
        );
        SdfMeshDraw[] one = [new(Material: 1, Mesh: quad, ObjectToWorld: Matrix4x4.Identity)];
        var many = Enumerable.Range(count: 8, start: 0).Select(selector: index => new SdfMeshDraw(
            Material: index,
            Mesh: quad,
            ObjectToWorld: Matrix4x4.CreateTranslation(xPosition: index, yPosition: 0f, zPosition: 0f)
        )).ToArray();

        rig.Render(
            meshDraws: one,
            time: 0f
        );
        Assert.Equal(expected: SdfMeshRegion.BytesOf(draws: one), actual: rig.Engine.MeshRegionBytes);

        rig.Render(
            meshDraws: many,
            time: 0f
        );
        Assert.True(condition: (rig.Engine.MeshRegionBytes >= SdfMeshRegion.BytesOf(draws: many)));

        // A program past the region's words grows the program region and stages whole into it; one with instances past
        // the engine's reserve grows the instance grid.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        for (var sphere = 0; (sphere < 4); sphere++) {
            builder.Sphere(
                material: material,
                radius: (1f + sphere)
            );
        }

        var larger = builder.Build();
        var words = Program(albedo: Vector3.One).Words.Length;
        var grown = Math.Max(
            val1: larger.Words.Length,
            val2: (words + (words / 2))
        );

        rig.Engine.UploadProgram(program: larger);
        rig.Render(time: 0f);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: larger.Words).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: ((ulong)(grown * sizeof(uint))))[..(larger.Words.Length * sizeof(uint))]
        );

        builder = new SdfProgramBuilder();

        var instanceMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        for (var instance = 0; (instance < 4); instance++) {
            _ = builder.Instance(
                boundCenter: new Vector3(x: (3f * instance), y: 0f, z: 0f),
                boundRadius: 1f,
                emit: emitter => emitter.Sphere(
                    material: instanceMaterial,
                    radius: 1f
                )
            );
        }

        var instanced = builder.Build();

        Assert.True(condition: (instanced.Instances.Count > larger.Instances.Count));
        rig.Engine.UploadProgram(program: instanced);
        rig.Render(time: 0f);
        Assert.Equal(expected: pools, actual: rig.Gpu.PoolsCreated.Count);
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
        private readonly int m_programWordReserve;
        private readonly DynamicTransform[] m_transforms;

        private SdfWorldPipelines m_pipelines = null!;
        private GpuPassPipeline m_regionCopy = null!;

        public Rig(int slots, int programWordReserve = 0, GpuMemoryProfile profile = default, GpuDescriptorHeapBudget? heap = null) {
            Gpu = new UploadModelGpu(reportVersion: SdfIsa.Version) {
                DescriptorHeap = heap,
                MemoryProfile = profile,
            };
            m_programWordReserve = programWordReserve;
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
            m_regionCopy.Dispose();
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
            m_regionCopy.Dispose();
            Engine = Build();
        }
        // Renders one frame at the given time, by default resetting the tallies first so they read that frame's writes.
        public void Render(float time, bool resetTallies = true, IReadOnlyList<SdfMeshDraw>? meshDraws = null) {
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
                MeshDraws = (meshDraws ?? []),
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

            m_regionCopy = SdfTestPipelines.RegionCopy(
                device: Gpu,
                kernel: UploadModelGpu.RegionCopyBytecode,
                ledger: ledger
            );
            m_pipelines = SdfTestPipelines.Build(
                device: Gpu,
                kernels: SdfTestPipelines.Kernels(),
                ledger: ledger
            );

            return new SdfWorldEngine(
                device: Gpu,
                height: Extent,
                options: new SdfWorldEngineOptions(
                    BrickPoolVoxelCapacity: 0,
                    DynamicTransformCapacity: m_transforms.Length,
                    Program: m_program,
                    ProgramWordCapacity: m_programWordReserve,
                    ViewportCapacity: 1,
                    WorkLedger: ledger
                ),
                pipelines: m_pipelines,
                regionCopy: m_regionCopy.Compute!,
                width: Extent
            );
        }
    }
}
