using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for the host-visible bytes <see cref="SdfWorldTables"/> writes per frame, driven over
/// <see cref="UploadModelGpu"/>, which backs every buffer with bytes and runs the region copies. Its default memory
/// profile stages every region, so each copy writes a four-word header, one run-table entry per run and the owed words:
/// a still frame writes nothing, since each view's viewport row is its passes' own; k dynamic transforms the producer's moved
/// set owes write the words of each that changed, in one copy dispatch however scattered they are; a change reaches the
/// device-local buffer once and stays there through every frame in flight after it, whichever ring slot those frames
/// stage in; changes past the run bound still leave the table exact; a program edit writes only the program words that
/// changed; the program region holds the live program rather than the reserve and grows by half again past it; a screen's
/// mapping reaches the device as its draw form and an unchanged one owes nothing; and an engine rebuilt after a device
/// loss owes every table again and reads back exact.
/// </summary>
public sealed partial class SdfWorldTablesUploadLawTests {
    // The packed width of a dynamic transform (isa/sdf-world.interface.hlsli sdfDynamicTransforms).
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

        // Eight per-frame tables, each a ring of one buffer per slot, and nothing copied; the mesh region is always staged,
        // so it takes no aperture buffer.
        using (var rig = new Rig(profile: discrete, slots: 40)) {
            rig.Warm();
            Assert.Equal(expected: (8 * SdfWorldTables.FrameRingSize), actual: rig.Gpu.ApertureBuffers);
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
    public void AStillFrameWritesNothing() {
        using var rig = new Rig(slots: 40);

        rig.Warm();

        // The presentation time rides each view's viewport row, which its passes write; the tables owe nothing for it.
        for (var frame = 0; (frame < 2); frame++) {
            rig.Render(time: (1f + frame));
            Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());
            Assert.Equal(expected: 0, actual: rig.Gpu.UploadCopies);
            Assert.Equal(expected: 0L, actual: rig.Gpu.BlockBytes());
        }
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
        Assert.Equal(expected: 1, actual: rig.Gpu.UploadCopies);
        Assert.Equal(expected: ((long)(HeaderBytes + (Changed * (RunEntryBytes + sizeof(float))))), actual: rig.Gpu.HostBytes());
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
        for (var frame = 1; (frame <= SdfWorldTables.FrameRingSize); frame++) {
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
        // 300 separate runs, past SdfWorldTables's 256-run bound, all within the first 600 of 1200 slots.
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
    public void ADeviceLossRebuildsTheResidencysTablesOwingEveryTable() {
        const int Slots = 24;

        var gpu = new UploadModelGpu();
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
        using var node = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: frame),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            name: "world",
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

        node.ProduceFirstFrame(context: in context);

        for (var produced = 0; (produced < SdfWorldTables.FrameRingSize); produced++) {
            node.ProduceFrame(context: in context);
        }

        gpu.ResetTallies();
        node.ProduceFrame(context: in context);
        Assert.Equal(expected: 0L, actual: gpu.HostBytes());

        node.OnDeviceLost();
        gpu.ResetTallies();
        node.ProduceFirstFrame(context: in context);
        Assert.Equal(
            expected: (((((long)Slots) * DynamicTransformBytes) + HeaderBytes) + RunEntryBytes),
            actual: gpu.HostWrites()
                .Where(predicate: write => (write.SizeBytes == ((ulong)(StagingReserveBytes + (Slots * DynamicTransformBytes)))))
                .Sum(selector: write => write.Written)
        );
        Assert.Equal(
            expected: Packed(transforms: transforms),
            actual: gpu.DeviceLocal(part: "dynamic-transforms", sizeBytes: ((ulong)(Slots * DynamicTransformBytes)))
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
    // A screen's mapping reaches the device as its draw form (frame/sdf-environment.hlsli's ScreenMappingData) at its
    // screen's place, every other screen's rows zero, with the sampler its filter names in the state row; the same
    // mapping supplied again owes nothing, another filter changes only the sampler, and clearing it zeroes its rows.
    [Fact]
    public void TheScreenMappingTableHoldsEachScreensDrawFormAndAnUnchangedMappingOwesNothing() {
        const int Screen = 3;
        const int Rows = 7;
        const float Border = 0.03f;
        using var rig = new Rig(slots: 1);
        var mapping = new SourceMapping(
            Crop: SourcePixelRect.Whole(height: 144, width: 160),
            Placement: new SourcePlacement.Surface(HalfHeight: 0.9f, HalfWidth: 1.2f, Origin: Vector3.Zero, Right: Vector3.UnitX, Up: Vector3.UnitY),
            Source: SourceHandle.Producer(name: "cabinet"),
            SourceHeight: 144,
            SourceWidth: 160,
            Warp: new SourceWarp(Inverse: SourceWarpInverse.Affine.Inset(border: Border), Pass: "glass")
        );
        var inset = ((SourceWarpInverse.Affine)mapping.Warp!.Inverse!);
        var table = new float[((SdfWorldTables.MaxScreenSurfaces * Rows) * 4)];
        // The warp's rows carry the face distance one warped unit spans, the inner width; the image rows the identity,
        // mapped and not letterboxing; then the whole crop, the crop inset by half a pixel, and the state row: unbound,
        // through the nearest sampler.
        float[] rows = [
            inset.M11, 0f, inset.M13, (1f / inset.M11),
            0f, inset.M22, inset.M23, (1f / inset.M22),
            1f, 0f, 0f, 1f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 1f,
            (0.5f / 160f), (0.5f / 144f), (1f - (0.5f / 160f)), (1f - (0.5f / 144f)),
            0f, ((float)GpuSamplerFilter.Nearest), 0f, 0f,
        ];

        byte[] Table() => rig.Gpu.DeviceLocal(sizeBytes: ((ulong)(table.Length * sizeof(float))));

        rig.Warm();
        rig.Engine.SetScreenMapping(mapping: mapping, screenIndex: Screen);
        rig.Render(time: 0f);
        rows.CopyTo(array: table, index: ((Screen * Rows) * 4));
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: table.AsSpan()).ToArray(),
            actual: Table()
        );

        rig.Engine.SetScreenMapping(mapping: mapping, screenIndex: Screen);
        rig.Render(time: 0f);
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());

        rig.Engine.SetScreenMapping(mapping: (mapping with { Filter = GpuSamplerFilter.Linear }), screenIndex: Screen);
        rig.Render(time: 0f);
        table[((((Screen * Rows) + 6) * 4) + 1)] = ((float)GpuSamplerFilter.Linear);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: table.AsSpan()).ToArray(),
            actual: Table()
        );

        rig.Engine.SetScreenMapping(mapping: null, screenIndex: Screen);
        rig.Render(time: 0f);
        Assert.Equal(
            expected: new byte[(table.Length * sizeof(float))],
            actual: Table()
        );
    }
    // A screen's glyph decal reaches the device's decal table: its descriptor (columns, rows, the first cell, the distance
    // range's bits) in the screen's descriptor row and its cells in the screen's run of the cell region. The same decal
    // again owes nothing, clearing it zeroes its descriptor alone, clearing a clear slot owes nothing, and a grid past the
    // per-screen budget is refused.
    [Fact]
    public void AScreensGlyphDecalReachesTheDecalTableAndAnUnchangedDecalOwesNothing() {
        const int Screen = 5;
        const int Columns = 3;
        const int Rows = 2;
        const float DistanceRange = 4f;
        using var rig = new Rig(slots: 1);
        var cells = new uint[((Columns * Rows) * 4)];

        for (var index = 0; (index < cells.Length); index++) {
            cells[index] = (0x01000000u + ((uint)index));
        }

        var table = new uint[((SdfWorldTables.MaxScreenSurfaces + (SdfWorldTables.MaxScreenSurfaces * SdfWorldTables.MaxScreenDecalCells)) * 4)];
        var cellBase = (SdfWorldTables.MaxScreenSurfaces + (Screen * SdfWorldTables.MaxScreenDecalCells));

        uint[] Table() => MemoryMarshal.Cast<byte, uint>(span: rig.Gpu.DeviceLocal(sizeBytes: ((ulong)(table.Length * sizeof(uint))))).ToArray();

        rig.Warm();
        rig.Engine.SetScreenDecal(
            cellWords: cells,
            columns: Columns,
            distanceRange: DistanceRange,
            rows: Rows,
            screenIndex: Screen
        );
        rig.Render(time: 0f);
        ((uint[])[Columns, Rows, ((uint)cellBase), BitConverter.SingleToUInt32Bits(value: DistanceRange)]).CopyTo(array: table, index: (Screen * 4));
        cells.CopyTo(array: table, index: (cellBase * 4));
        Assert.Equal(expected: table, actual: Table());

        rig.Engine.SetScreenDecal(
            cellWords: cells,
            columns: Columns,
            distanceRange: DistanceRange,
            rows: Rows,
            screenIndex: Screen
        );
        rig.Render(time: 0f);
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());

        rig.Engine.ClearScreenDecal(screenIndex: Screen);
        rig.Render(time: 0f);
        Array.Clear(array: table, index: (Screen * 4), length: 4);
        Assert.Equal(expected: table, actual: Table());

        rig.Engine.ClearScreenDecal(screenIndex: Screen);
        rig.Render(time: 0f);
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => rig.Engine.SetScreenDecal(
            cellWords: new uint[((SdfWorldTables.MaxScreenDecalCells + 1) * 4)],
            columns: (SdfWorldTables.MaxScreenDecalCells + 1),
            distanceRange: 0f,
            rows: 1,
            screenIndex: Screen
        ));
    }
    // A frame's bounded volumes reach the device's volume table, SdfVolume.VectorsPerEntry float4 rows each in list
    // order (shade/shade-volumes.hlsli's layout), every later entry zero. The same list again owes nothing, an emptied list
    // zeroes the table, and a list past MaxVolumes is refused.
    [Fact]
    public void AFramesBoundedVolumesReachTheVolumeTableRowForRow() {
        using var rig = new Rig(slots: 2);
        var flow = new SdfVolume(
            Axis: 1f,
            DynamicSlot: 1,
            Extinction: 0.5f,
            HalfExtent: new Vector3(x: 0.5f, y: 1f, z: 0.5f),
            Intensity: 3f,
            IntensityLane: 2,
            Kind: SdfVolumeKind.Flow,
            Position: new Vector3(x: 1f, y: 2f, z: 3f),
            PulseAmplitude: 0.25f,
            PulseFrequency: 2f,
            Ramp: [new SdfDensityStop(Color: new Vector3(x: 1f, y: 0.5f, z: 0f), Density: 0.5f)],
            Rotation: Quaternion.Identity,
            Seed: 7u,
            Speed: 2f,
            Steps: 16,
            Width: 0.25f
        );
        var cloud = new SdfVolume(
            Axis: 2f,
            Coverage: 0.4f,
            DynamicSlot: -1,
            Extinction: 2f,
            HalfExtent: new Vector3(x: 3f, y: 1f, z: 2f),
            Intensity: 1f,
            Kind: SdfVolumeKind.Cloud,
            Position: new Vector3(x: -4f, y: 5f, z: -6f),
            Ramp: [
                new SdfDensityStop(Color: new Vector3(x: 1f, y: 1f, z: 1f), Density: 0.1f),
                new SdfDensityStop(Color: new Vector3(x: 0.5f, y: 0.5f, z: 0.6f), Density: 0.9f),
            ],
            Rotation: new Quaternion(w: 0.8f, x: 0f, y: 0.6f, z: 0f),
            Seed: 11u,
            Softness: 0.3f,
            Speed: 0.5f,
            Steps: 32,
            Width: 1f
        );
        const int EntryFloats = (SdfVolume.VectorsPerEntry * 4);
        var table = new float[(SdfWorldTables.MaxVolumes * EntryFloats)];
        // Each entry's rows at presented tick zero (SdfVolumeMotion bakes the motion, SdfSkyClockLawTests its later
        // ticks): position and dynamic slot; rotation; half extent and axis; width, the advection (none yet), the seed's
        // bits and the steps; intensity, extinction and the pulse's gain (one at phase zero); the intensity lane, the
        // ramp's length, the kind and a cloud's Z advection; four ramp rows, color and density; then coverage and
        // softness.
        float[] flowRows = [
            1f, 2f, 3f, 1f,
            0f, 0f, 0f, 1f,
            0.5f, 1f, 0.5f, 1f,
            0.25f, 0f, BitConverter.UInt32BitsToSingle(value: 7u), 16f,
            3f, 0.5f, 1f, 0f,
            2f, 1f, ((float)SdfVolumeKind.Flow), 0f,
            1f, 0.5f, 0f, 0.5f,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            0.55f, 0.18f, 0f, 0f,
        ];
        float[] cloudRows = [
            -4f, 5f, -6f, -1f,
            0f, 0.6f, 0f, 0.8f,
            3f, 1f, 2f, 2f,
            1f, 0f, BitConverter.UInt32BitsToSingle(value: 11u), 32f,
            1f, 2f, 1f, 0f,
            -1f, 2f, ((float)SdfVolumeKind.Cloud), 0f,
            1f, 1f, 1f, 0.1f,
            0.5f, 0.5f, 0.6f, 0.9f,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            0.4f, 0.3f, 0f, 0f,
        ];

        byte[] Table() => rig.Gpu.DeviceLocal(sizeBytes: ((ulong)(table.Length * sizeof(float))));

        rig.Warm();
        rig.Render(time: 0f, volumes: [flow, cloud]);
        flowRows.CopyTo(array: table, index: 0);
        cloudRows.CopyTo(array: table, index: EntryFloats);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: table.AsSpan()).ToArray(),
            actual: Table()
        );

        rig.Render(time: 0f, volumes: [flow, cloud]);
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());

        rig.Render(time: 0f, volumes: []);
        Assert.Equal(
            expected: new byte[(table.Length * sizeof(float))],
            actual: Table()
        );

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => rig.Render(
            time: 0f,
            volumes: [.. Enumerable.Repeat(count: (SdfWorldTables.MaxVolumes + 1), element: cloud)]
        ));
    }
    // A host-baked brick (a height field's, WorldFieldEmitter) reaches the device's brick pool: queued, its slot reads
    // Baking at its serial; the next upload lands the voxels at the slot's word offset and reads Ready with the serial
    // moved on; two uploads to one slot before a frame land only the newer; and pool-less tables refuse an upload.
    [Fact]
    public void AHostBakedBrickLandsInItsPoolSlotAndTheSlotTurnsReady() {
        const int Slot = 1;
        const int Dim = 4;
        const int VoxelCount = ((Dim * Dim) * Dim);
        const int Capacity = (SdfBrickPoolLayout.VoxelsPerBrick + VoxelCount);
        using var rig = new Rig(brickPoolVoxelCapacity: Capacity, slots: 1);
        using var poolless = new Rig(slots: 1);

        static float[] Voxels(float bias) => [.. Enumerable.Range(count: VoxelCount, start: 0).Select(selector: index => (bias + (index * 0.125f)))];
        float[] SlotVoxels() => MemoryMarshal.Cast<byte, float>(span: rig.Gpu.DeviceLocal(sizeBytes: (Capacity * sizeof(float))).AsSpan(
            length: (VoxelCount * sizeof(float)),
            start: (SdfBrickPoolLayout.SlotWordOffset(slot: Slot) * sizeof(float))
        )).ToArray();

        rig.Warm();

        var before = rig.Engine.GetBrickState(slot: Slot);
        var first = Voxels(bias: 1f);

        Assert.True(condition: rig.Engine.BrickBakeAvailable);
        Assert.Equal(expected: BrickBakeState.Empty, actual: before.State);

        rig.Engine.UploadBrick(dimX: Dim, dimY: Dim, dimZ: Dim, slot: Slot, voxels: first);
        Assert.Equal(expected: new BrickBakeStatus(Serial: before.Serial, State: BrickBakeState.Baking), actual: rig.Engine.GetBrickState(slot: Slot));

        rig.Render(time: 0f);
        Assert.Equal(expected: new BrickBakeStatus(Serial: (before.Serial + 1), State: BrickBakeState.Ready), actual: rig.Engine.GetBrickState(slot: Slot));
        Assert.Equal(expected: first, actual: SlotVoxels());

        var newest = Voxels(bias: 20f);

        rig.Engine.UploadBrick(dimX: Dim, dimY: Dim, dimZ: Dim, slot: Slot, voxels: Voxels(bias: 10f));
        rig.Engine.UploadBrick(dimX: Dim, dimY: Dim, dimZ: Dim, slot: Slot, voxels: newest);
        rig.Render(time: 0f);
        Assert.Equal(expected: new BrickBakeStatus(Serial: (before.Serial + 2), State: BrickBakeState.Ready), actual: rig.Engine.GetBrickState(slot: Slot));
        Assert.Equal(expected: newest, actual: SlotVoxels());

        Assert.False(condition: poolless.Engine.BrickBakeAvailable);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => poolless.Engine.UploadBrick(dimX: Dim, dimY: Dim, dimZ: Dim, slot: 0, voxels: first));
    }
    [Fact]
    public void TheMeshRegionHoldsAKnownDrawSetAndOwesOnlyTheWordsANewSetChanges() {
        using var rig = new Rig(slots: 1);
        var quad = new SdfMesh(
            indices: new uint[] { 0, 1, 2, 0, 2, 3 },
            positions: new Vector3[] { new(x: 0f, y: 0f, z: 0f), new(x: 1f, y: 0f, z: 0f), new(x: 1f, y: 1f, z: 0f), new(x: 0f, y: 1f, z: 0f) }
        );
        // The triangle carries every attribute: a normal and a texture coordinate a vertex, and a palette entry.
        var triangle = new SdfMesh(
            indices: new uint[] { 0, 1, 2 },
            normals: new Vector3[] { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY },
            positions: new Vector3[] { new(x: 0f, y: 0f, z: 0f), new(x: 0f, y: 0f, z: 1f), new(x: 1f, y: 0f, z: 0f) },
            triangleMaterials: new uint[] { 2 },
            uvs: new Vector2[] { new(x: 0.25f, y: 0.5f), new(x: 0.5f, y: 0.5f), new(x: 0.25f, y: 0.75f) }
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
            MaterialCount: 1,
            VertexCount: 7
        );
        var expected = new List<uint>();

        // A draw's record: its matrix row by row, its material, then the word its mesh's first index sits at, its index
        // count, the word its first vertex sits at, its attribute flags, the word its first triangle material sits at, and
        // its normal matrix (the inverse transpose of its upper 3x3). The vertices start past the three records (word 93),
        // the triangle materials past the seven vertices (word 149), and the indices past the one triangle material
        // (word 150).
        void Record(Matrix4x4 matrix, uint material, uint indexWord, uint indexCount, uint vertexWord, uint flags) {
            float[] rows = [
                matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                matrix.M41, matrix.M42, matrix.M43, matrix.M44,
            ];

            expected.AddRange(collection: rows.Select(selector: BitConverter.SingleToUInt32Bits));
            expected.AddRange(collection: [material, indexWord, indexCount, vertexWord, flags, 149u]);
            Assert.True(condition: Matrix4x4.Invert(matrix: matrix with { M41 = 0f, M42 = 0f, M43 = 0f }, result: out var inverse));

            var normal = Matrix4x4.Transpose(matrix: inverse);

            expected.AddRange(collection: new[] { normal.M11, normal.M12, normal.M13, normal.M21, normal.M22, normal.M23, normal.M31, normal.M32, normal.M33 }.Select(selector: BitConverter.SingleToUInt32Bits));
        }
        // A vertex: its position, its normal and its texture coordinate, zeros for an attribute its mesh lacks.
        void Vertices(SdfMesh mesh) {
            for (var vertex = 0; (vertex < mesh.Positions.Length); vertex++) {
                var position = mesh.Positions.Span[vertex];
                var normal = (mesh.Normals.IsEmpty ? Vector3.Zero : mesh.Normals.Span[vertex]);
                var uv = (mesh.Uvs.IsEmpty ? Vector2.Zero : mesh.Uvs.Span[vertex]);

                expected.AddRange(collection: new[] { position.X, position.Y, position.Z, normal.X, normal.Y, normal.Z, uv.X, uv.Y }.Select(selector: BitConverter.SingleToUInt32Bits));
            }
        }

        Record(flags: 0u, indexCount: 6, indexWord: 150, material: 4, matrix: moved, vertexWord: 93);
        Record(flags: SdfMeshRegion.NormalsFlag | SdfMeshRegion.MaterialsFlag, indexCount: 3, indexWord: 156, material: 5, matrix: Matrix4x4.CreateScale(scale: 2f), vertexWord: 125);
        Record(flags: 0u, indexCount: 6, indexWord: 150, material: 6, matrix: Matrix4x4.Identity, vertexWord: 93);
        Vertices(mesh: quad);
        Vertices(mesh: triangle);
        expected.Add(item: 2u);
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

        // The tables created exactly the pools their admission states: their own and the one copy pool of every region.
        Assert.Equal(
            actual: rig.Gpu.PoolsCreated.Count,
            expected: 2
        );
        Assert.Equal(
            actual: rig.Gpu.PoolsCreated.CountBy(keySelector: static pool => pool).ToDictionary(),
            expected: SdfWorldTables.DescriptorPools(brickPool: false).CountBy(keySelector: static pool => pool).ToDictionary()
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

        private readonly int m_brickPoolVoxelCapacity;
        private readonly SdfProgram m_program;
        private readonly int m_programWordReserve;
        private readonly DynamicTransform[] m_transforms;

        private SdfWorldPipelines m_pipelines = null!;
        private GpuPassPipeline m_meshRaster = null!;
        private GpuPassPipeline m_regionCopy = null!;

        public Rig(int slots, int programWordReserve = 0, GpuMemoryProfile profile = default, GpuDescriptorHeapBudget? heap = null, int brickPoolVoxelCapacity = 0) {
            Gpu = new UploadModelGpu() {
                DescriptorHeap = heap,
                MemoryProfile = profile,
            };
            m_brickPoolVoxelCapacity = brickPoolVoxelCapacity;
            m_programWordReserve = programWordReserve;
            m_program = Program(albedo: Vector3.One);
            m_transforms = Transforms(slots: slots);
            Engine = Build();
        }

        public SdfWorldTables Engine { get; private set; }
        public UploadModelGpu Gpu { get; }

        // The device-local dynamic-transform table holds exactly the frame's packed transforms.
        public void AssertDeviceTransforms() => Assert.Equal(
            expected: Packed(transforms: m_transforms),
            actual: Gpu.DeviceLocal(sizeBytes: ((ulong)(m_transforms.Length * DynamicTransformBytes)), part: "dynamic-transforms")
        );
        public void Dispose() {
            Engine.Dispose();
            m_pipelines.Dispose();
            m_regionCopy.Dispose();
            m_meshRaster.Dispose();
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
            m_meshRaster.Dispose();
            Engine = Build();
        }
        // Renders one frame at the given time, by default resetting the tallies first so they read that frame's writes.
        public void Render(float time, bool resetTallies = true, IReadOnlyList<SdfMeshDraw>? meshDraws = null, IReadOnlyList<SdfVolume>? volumes = null) {
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
            Engine.Pack(frame: Frame(
                program: m_program,
                time: time,
                transforms: m_transforms
            ) with {
                MeshDraws = (meshDraws ?? []),
                MovedTransforms = m_moved,
                Volumes = (volumes ?? []),
            });
            Engine.SubmitUpload();
        }
        // Renders until every ring slot has taken its first frame, so later frames read steady-state writes.
        public void Warm() {
            for (var frame = 0; (frame <= SdfWorldTables.FrameRingSize); frame++) {
                Render(time: 0f);
            }
        }

        private SdfWorldTables Build() {
            var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldTables.FrameRingSize,
                name: "gpu.sdf-engine"
            );

            m_regionCopy = SdfTestPipelines.RegionCopy(
                device: Gpu,
                kernel: UploadModelGpu.RegionCopyBytecode,
                ledger: ledger
            );
            m_meshRaster = SdfTestPipelines.MeshRaster(
                device: Gpu,
                ledger: ledger
            );
            // A brick pool needs its bake pipeline, so its set is built with a one-byte brick kernel.
            m_pipelines = ((m_brickPoolVoxelCapacity == 0)
                ? SdfTestPipelines.Build(
                    device: Gpu,
                    kernels: SdfTestPipelines.Kernels(),
                    cache: new GpuPassPipelineCache()
                )
                : SdfTestPipelines.Build(
                    device: Gpu,
                    includeBrickPipelines: true,
                    kernels: SdfTestPipelines.Kernels().With(bytecode: new byte[] { 1 }, kernel: SdfKernel.BrickBake),
                    cache: new GpuPassPipelineCache()
                ));

            return new SdfWorldTables(
                device: Gpu,
                options: new SdfWorldTablesOptions(
                    BrickPoolVoxelCapacity: m_brickPoolVoxelCapacity,
                    DynamicTransformCapacity: m_transforms.Length,
                    Program: m_program,
                    ProgramWordCapacity: m_programWordReserve,
                    WorkLedger: ledger
                ),
                pipelines: m_pipelines,
                meshRaster: m_meshRaster,
                regionCopy: m_regionCopy.Compute!
            );
        }
    }
}
