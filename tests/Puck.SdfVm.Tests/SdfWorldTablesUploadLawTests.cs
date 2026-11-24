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
    // The frames a landing may take at the upload budget's rate: ten seconds of the stepped clock.
    private const int MostLandingFrames = 600;
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

        Assert.True(condition: (large.WordCount > GpuRegion.CopyRowThreads));
        Assert.True(condition: (GpuRegion.CopyGroups(count: ((uint)large.WordCount)).Y >= 2U));
        rig.Warm();
        rig.Engine.UploadProgram(program: large);
        rig.Render(time: 0f);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: large.CopyWords()).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: (((ulong)large.WordCount) * sizeof(uint)))
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

        // Twelve per-frame tables; mesh and shadow handoffs are staged. The other ten (program, transforms, screen
        // surfaces and mappings, volumes, decals, lights, sky, sky layers, frame-sampled) each use one aperture buffer
        // per ring slot, and a transform move copies nothing.
        using (var rig = new Rig(profile: discrete, slots: 40)) {
            rig.Warm();
            Assert.Equal(expected: (10 * SdfWorldTables.FrameRingSize), actual: rig.Gpu.ApertureBuffers);
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

        // The rebuilt engine's new tables start cleared on the GPU and stage only the words that differ from zero, and the
        // device reads back every transform exact.
        rig.AssertDeviceTransforms();
    }
    [Fact]
    public void ADeviceLossRebuildsTheResidencysTablesOwingEveryTable() {
        const int Slots = 24;

        // The device's clock stands still, so no background work is metered in beyond the budget's first credit.
        var gpu = new UploadModelGpu(clock: VirtualClock.ForDevice());
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
        // The rebuilt tables start cleared on the GPU and owe what differs from zero; the device holds every transform.
        Assert.NotEqual(expected: 0L, actual: gpu.HostBytes());
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
        Assert.InRange(actual: writes[0].Written, high: (StagingReserveBytes + (edited.WordCount * sizeof(uint))), low: ((HeaderBytes + RunEntryBytes) + sizeof(uint)));
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: edited.CopyWords()).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: programBytes).AsSpan(
                length: (edited.WordCount * sizeof(uint)),
                start: 0
            ).ToArray()
        );
    }
    // A program uploaded with the words it differs in from the program it was built after writes only those words when
    // that program is the live one, and compares itself with the live program when it is not: either way the device
    // holds the new program word for word.
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AProgramBuiltAfterTheLiveOneUploadsTheWordsItsDeltaNames(bool fromLive) {
        using var rig = new Rig(slots: 40);

        rig.Warm();

        var edited = Program(albedo: new Vector3(x: 0.25f, y: 0.5f, z: 0.75f));
        var delta = SdfProgramDelta.Between(
            from: (fromLive ? rig.LiveProgram : Program(albedo: Vector3.Zero, radius: 3f)),
            to: edited
        );

        Assert.InRange(actual: delta.Runs.Count, high: GpuRegion.MaxCopyRuns, low: 1);
        rig.Gpu.ResetTallies();
        rig.Engine.UploadProgram(delta: delta, program: edited);
        rig.Render(
            resetTallies: false,
            time: 0f
        );

        var programBytes = (((ulong)rig.Engine.ProgramWordCapacity) * sizeof(uint));

        Assert.InRange(actual: Assert.Single(collection: rig.Gpu.HostWrites()).Written, high: (StagingReserveBytes + (edited.WordCount * sizeof(uint))), low: ((HeaderBytes + RunEntryBytes) + sizeof(uint)));
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: edited.CopyWords()).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: programBytes).AsSpan(
                length: (edited.WordCount * sizeof(uint)),
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
        var words = Program(albedo: Vector3.One).WordCount;

        rig.Warm();

        // The reserve allocates nothing; the engine reports it as the words it is provisioned for.
        Assert.Equal(expected: Reserve, actual: rig.Engine.ProgramWordCapacity);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => rig.Gpu.DeviceLocal(sizeBytes: (Reserve * sizeof(uint))));
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: Program(albedo: Vector3.One).CopyWords()).ToArray(),
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
            val1: larger.WordCount,
            val2: (words + (words / 2))
        );

        // The delta names the words the larger program differs in from the live one, but the grown region holds none of
        // the live program's words, so the upload writes the program whole.
        Assert.True(condition: (larger.WordCount > words));
        rig.Engine.UploadProgram(
            delta: SdfProgramDelta.Between(from: rig.LiveProgram, to: larger),
            program: larger
        );
        rig.Render(time: 0f);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: larger.CopyWords()).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: ((ulong)(grown * sizeof(uint))))[..(larger.WordCount * sizeof(uint))]
        );
        Assert.Equal(expected: Reserve, actual: rig.Engine.ProgramWordCapacity);
    }
    // A screen's mapping reaches the device as its draw form (frame/sdf-environment.hlsli's ScreenMappingData) at its
    // screen's place, every other screen's rows zero, with the sampler its filter names and its face in the state row;
    // the same mapping and face supplied again owe nothing, another filter changes only the sampler, another face only
    // the face, and clearing it zeroes its rows.
    [Fact]
    public void TheScreenMappingTableHoldsEachScreensDrawFormAndAnUnchangedMappingOwesNothing() {
        const int Screen = 3;
        // A screen's nine mapping rows, then its ripple's rows, all zero for a screen that does not ripple.
        const int Rows = (9 + SdfScreenRipple.Rows);
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
        // through the nearest sampler, a display's face.
        float[] rows = [
            inset.M11, 0f, inset.M13, (1f / inset.M11),
            0f, inset.M22, inset.M23, (1f / inset.M22),
            1f, 0f, 0f, 1f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 1f,
            (0.5f / 160f), (0.5f / 144f), (1f - (0.5f / 160f)), (1f - (0.5f / 144f)),
            0f, ((float)GpuSamplerFilter.Nearest), 0f, ((float)SdfScreenFace.Display),
            // No treatment: both treatment rows zero.
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
        ];

        byte[] Table() => rig.Gpu.DeviceLocal(sizeBytes: ((ulong)(table.Length * sizeof(float))));

        rig.Warm();
        rig.Engine.SetScreenMapping(face: SdfScreenFace.Display, mapping: mapping, screenIndex: Screen);
        rig.Render(time: 0f);
        rows.CopyTo(array: table, index: ((Screen * Rows) * 4));
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: table.AsSpan()).ToArray(),
            actual: Table()
        );

        rig.Engine.SetScreenMapping(face: SdfScreenFace.Display, mapping: mapping, screenIndex: Screen);
        rig.Render(time: 0f);
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());

        var linear = (mapping with { Filter = GpuSamplerFilter.Linear });

        rig.Engine.SetScreenMapping(face: SdfScreenFace.Display, mapping: linear, screenIndex: Screen);
        rig.Render(time: 0f);
        table[((((Screen * Rows) + 6) * 4) + 1)] = ((float)GpuSamplerFilter.Linear);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: table.AsSpan()).ToArray(),
            actual: Table()
        );

        rig.Engine.SetScreenMapping(face: SdfScreenFace.Display, mapping: linear, screenIndex: Screen);
        rig.Render(time: 0f);
        table[((((Screen * Rows) + 6) * 4) + 3)] = ((float)SdfScreenFace.Display);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: table.AsSpan()).ToArray(),
            actual: Table()
        );

        // A screen with no mapping (one showing a glyph decal) clears its mapping rows and keeps its face.
        rig.Engine.SetScreenMapping(face: SdfScreenFace.Display, mapping: null, screenIndex: Screen);
        rig.Render(time: 0f);

        var unmapped = new float[table.Length];

        unmapped[((((Screen * Rows) + 6) * 4) + 3)] = ((float)SdfScreenFace.Display);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: unmapped.AsSpan()).ToArray(),
            actual: Table()
        );
    }
    // A tier view's codes ride each slot's position row and each screen's ripple state row, a ripple rewrite keeping the
    // screen's; leaving the view clears every slot's once, and a frame after it owes nothing.
    [Fact]
    public void ATierViewsCodesRideTheSlotsAndScreensAndClearOnceItEnds() {
        const int Screen = 3;
        const int Rows = (9 + SdfScreenRipple.Rows);
        using var rig = new Rig(slots: 4);

        float W(int slot) => MemoryMarshal.Cast<byte, float>(span: rig.Gpu.DeviceLocal(part: "dynamic-transforms", sizeBytes: (4 * DynamicTransformBytes)))[((slot * 12) + 3)];
        float Rate() => MemoryMarshal.Cast<byte, float>(span: rig.Gpu.DeviceLocal(sizeBytes: ((ulong)((SdfWorldTables.MaxScreenSurfaces * Rows) * 16))))[((((Screen * Rows) + 9) * 4) + 2)];

        rig.Warm();
        rig.Engine.DebugMode = DebugViewModes.Rates;
        rig.Engine.SetScreenRate(code: SdfUpdateRateCode.Half, screenIndex: Screen);
        rig.Engine.SetScreenRipple(ripple: new SdfScreenRipple(Clock: 1f, Contact0: default, Contact1: default, Contact2: default, Contact3: default), screenIndex: Screen);
        rig.Render(slotRateCodes: new byte[] { 0, 7, 3 }, time: 0f);
        Assert.Equal(expected: [0f, 7f, 3f, 0f], actual: [W(slot: 0), W(slot: 1), W(slot: 2), W(slot: 3)]);
        Assert.Equal(expected: ((float)SdfUpdateRateCode.Half), actual: Rate());

        rig.Engine.DebugMode = 0;
        rig.Engine.SetScreenRate(code: SdfUpdateRateCode.None, screenIndex: Screen);
        rig.Render(slotRateCodes: new byte[] { 0, 7, 3 }, time: 0f);
        Assert.Equal(expected: [0f, 0f, 0f, 0f], actual: [W(slot: 0), W(slot: 1), W(slot: 2), W(slot: 3)]);
        Assert.Equal(expected: 0f, actual: Rate());
        rig.Render(time: 0f);
        Assert.Equal(expected: 0L, actual: rig.Gpu.HostBytes());
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

        // The descriptor band (a row a screen, then a row a surface text), then the screens' cell pool, whose first run the
        // only grid claims.
        var cellBase = (SdfWorldTables.MaxScreenSurfaces + SdfSurfaceTextLayout.MaxTexts);
        var table = new uint[((cellBase + (SdfWorldTables.MaxScreenDecalScreens * SdfWorldTables.MaxScreenDecalCells)) * 4)];

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
            Width: 0.25f,
            Spread: 2.5f,
            Fade: 2f,
            NoiseScale: 0.5f,
            NoiseDepth: 0.25f
        );
        var cloud = new SdfVolume(
            Axis: 2f,
            Coverage: 0.4f,
            DynamicSlot: SdfProgram.NoDynamicTransformSlot,
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
            Scatter: 0.65f,
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
        // ramp's length, the kind and a cloud's Z advection; four ramp rows, color and density; then coverage, softness
        // and the share of the bodies' light it scatters; then a flow's spread and fade, the noise cell (the width where
        // none is authored) and the noise's depth.
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
            2.5f, 2f, 0.5f, 0.25f,
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
            0.4f, 0.3f, 0.65f, 0f,
            0f, 1f, 1f, 1f,
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
    // A relocation that leaves the mesh region holding more than half again what its draws need shrinks it to the need
    // plus an eighth, and the shrunk region holds the words of a region that never grew.
    [Fact]
    public void ARegionWhosePoolHasNoRoomKeepsItsBufferAndRefusesTheMeshesPastItWithoutThrowing() {
        const int BigVertices = ((40 << 20) / (SdfMeshRecord.VertexWords * sizeof(uint)));
        var account = new Puck.Hosting.Jobs.MemoryAccount(availablePhysicalBytes: static () => null, installedPhysicalBytes: (64L << 30));
        var clock = VirtualClock.ForDevice();
        var selector = new SdfResidencySelector(account: account, pool: Puck.Abstractions.Memory.PhysicalMemory.Device, time: clock);
        var quad = new SdfMesh(
            indices: new uint[] { 0, 1, 2, 0, 2, 3 },
            positions: new Vector3[] { new(x: 0f, y: 0f, z: 0f), new(x: 1f, y: 0f, z: 0f), new(x: 1f, y: 1f, z: 0f), new(x: 0f, y: 1f, z: 0f) }
        );
        var big = new SdfMesh(
            indices: new uint[] { 0, 1, 2 },
            positions: Enumerable.Range(count: BigVertices, start: 0).Select(selector: static index => new Vector3(x: index, y: index & 1, z: 0f)).ToArray()
        );
        var small = new SdfMeshDraw(identity: "quad", material: 1, mesh: quad, objectToWorld: Matrix4x4.Identity);
        var large = new SdfMeshDraw(identity: "big", material: 1, mesh: big, objectToWorld: Matrix4x4.CreateTranslation(xPosition: 4f, yPosition: 0f, zPosition: 0f));
        using var rig = new Rig(clock: clock, selector: selector, slots: 1);

        rig.Warm();
        rig.Land(meshDraws: [small], time: 0f);

        var held = rig.Engine.MeshRegionBytes;

        // The pool has no room for a replacement beside the standing buffer: the region keeps the buffer it holds, the
        // mesh that does not fit under it is refused, its draw waits, and nothing throws.
        account.SetBudget(bytes: 1L, pool: Puck.Abstractions.Memory.PhysicalMemory.Device);
        rig.Render(meshDraws: [small, large], time: 0f);
        rig.Render(meshDraws: [small, large], time: 0f);
        Assert.Equal(expected: (held, held, 1), actual: (rig.Engine.MeshRegionBytes, rig.Engine.MeshRegionLimitBytes, rig.Engine.MeshRegionRefused));
        Assert.Equal(expected: 2U, actual: rig.Engine.MeshDrawCount);
        Assert.Equal(expected: 0L, actual: account.ReadAdmission(pool: Puck.Abstractions.Memory.PhysicalMemory.Device).OverLimitGrants);
        // With room again the next list places the refused mesh and the region grows under its reservation.
        account.SetBudget(bytes: null, pool: Puck.Abstractions.Memory.PhysicalMemory.Device);
        rig.Land(meshDraws: [small, large], time: 0f);
        Assert.Equal(expected: (0, 2U), actual: (rig.Engine.MeshRegionRefused, rig.Engine.MeshDrawCount));
        Assert.True(condition: (rig.Engine.MeshRegionBytes > (40UL << 20)));
        Assert.Contains(expectedSubstring: "residencies=1", actualString: selector.Describe());
        // The region's staging is created under the staging owner, apart from the region the account reserves, and
        // nothing host-visible is created under the region's own.
        Assert.Contains(expected: (SdfResidencySelector.RegionStagingOwner + "/mesh-region"), collection: rig.Gpu.HostVisibleFamilies);
        Assert.DoesNotContain(expected: (SdfResidencySelector.RegionOwner + "/mesh-region"), collection: rig.Gpu.HostVisibleFamilies);
    }

    // One 64×64 view looking at the origin, at the given time, carrying the given transforms.
    internal static SdfFrame Frame(SdfProgram program, float time, DynamicTransform[] transforms) => new(
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

    // The transforms packed as the device-local table holds them: position and material scale, quaternion, lanes.
    private static byte[] Packed(DynamicTransform[] transforms) {
        var packed = new float[(transforms.Length * (DynamicTransformBytes / sizeof(float)))];

        for (var slot = 0; (slot < transforms.Length); slot++) {
            var transform = transforms[slot];
            var b = (slot * 12);

            packed[(b + 0)] = transform.Position.X; packed[(b + 1)] = transform.Position.Y; packed[(b + 2)] = transform.Position.Z; packed[(b + 3)] = transform.MaterialScale;
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

        // Construction creates exactly the pools admission declares, including compact-history copies.
        Assert.Equal(
            actual: rig.Gpu.PoolsCreated.CountBy(keySelector: static pool => pool).ToDictionary(),
            // The final two admitted pools belong to the residency's bake and probe clients, absent from standalone tables.
            expected: SdfWorldTables.DescriptorPools[..^2].CountBy(keySelector: static pool => pool).ToDictionary()
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
        SdfMeshDraw[] one = [new(identity: 0, material: 1, mesh: quad, objectToWorld: Matrix4x4.Identity)];
        var many = Enumerable.Range(count: 8, start: 0).Select(selector: index => new SdfMeshDraw(
            identity: index,
            material: index,
            mesh: quad,
            objectToWorld: Matrix4x4.CreateTranslation(xPosition: index, yPosition: 0f, zPosition: 0f)
        )).ToArray();

        rig.Render(
            meshDraws: one,
            time: 0f
        );
        Assert.True(condition: (rig.Engine.MeshRegionBytes >= SdfMeshRegion.BytesOf(draws: one)));

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
        var words = Program(albedo: Vector3.One).WordCount;
        var grown = Math.Max(
            val1: larger.WordCount,
            val2: (words + (words / 2))
        );

        rig.Engine.UploadProgram(program: larger);
        rig.Render(time: 0f);
        Assert.Equal(
            expected: MemoryMarshal.AsBytes(span: larger.CopyWords()).ToArray(),
            actual: rig.Gpu.DeviceLocal(sizeBytes: ((ulong)(grown * sizeof(uint))))[..(larger.WordCount * sizeof(uint))]
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

    private static SdfProgram Program(Vector3 albedo, float radius = 1f) {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: albedo)),
            radius: radius
        );

        return builder.Build();
    }

    // Distinct, nonzero poses: slot i sits at (i, 0, 0) under the identity rotation.
    internal static DynamicTransform[] Transforms(int slots) {
        var transforms = new DynamicTransform[slots];

        for (var slot = 0; (slot < slots); slot++) {
            transforms[slot] = new DynamicTransform(
                Orientation: Quaternion.Identity,
                Position: new Vector3(x: slot, y: 0f, z: 0f)
            );
        }

        return transforms;
    }

    // A producer's moved set: every Move owes its slot on the next rendered frame, as an emitter would.
    internal sealed class Rig : IDisposable {
        private readonly SdfMovedTransforms m_moved = new();
        private readonly List<int> m_pendingMoves = [];
        private readonly List<(int Slot, DynamicTransform Previous)> m_pendingReseats = [];

        private readonly SdfProgram m_program;
        private readonly int m_programWordReserve;
        private readonly DynamicTransform[] m_transforms;

        private SdfWorldPipelines m_pipelines = null!;
        private GpuPassPipeline m_shadowRaster = null!;
        private GpuPassPipeline m_glassRaster = null!;
        private GpuPassPipeline m_occlusion = null!;
        private GpuPassPipeline m_meshRaster = null!;
        private GpuPassPipeline m_regionCopy = null!;

        private readonly SdfResidencySelector? m_selector;

        // The device's clock, which each frame the rig renders moves one display period, so the frames it renders, never the
        // wall time they take, decide when the background work they meter lands. A law that hands the rig a selector
        // builds the selector on this clock (the clock argument).
        private readonly VirtualClock m_clock;

        public Rig(int slots, int programWordReserve = 0, GpuMemoryProfile profile = default, GpuDescriptorHeapBudget? heap = null, SdfResidencySelector? selector = null, GpuBufferPages? meshPages = null, VirtualClock? clock = null) {
            m_clock = (clock ?? VirtualClock.ForDevice());
            Gpu = new UploadModelGpu(clock: m_clock) {
                DescriptorHeap = heap,
                MemoryProfile = profile,
                DestinationPages = (part, _) => part == "mesh-region" ? meshPages : null,
            };
            m_selector = selector;
            m_programWordReserve = programWordReserve;
            m_program = Program(albedo: Vector3.One);
            m_transforms = Transforms(slots: slots);
            Engine = Build();
        }

        public SdfWorldTables Engine { get; private set; }
        public UploadModelGpu Gpu { get; }
        // The program the tables were built with, the live one until a test uploads another.
        public SdfProgram LiveProgram => m_program;

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
            m_shadowRaster.Dispose();
            m_glassRaster.Dispose();
            m_occlusion.Dispose();
        }
        // Moves one slot to a pose no earlier frame gave it.
        public void Move(int slot) {
            var transform = m_transforms[slot];

            m_transforms[slot] = transform with {
                Position = (transform.Position + new Vector3(x: 0f, y: 1f, z: 0f)),
            };
            m_pendingMoves.Add(item: slot);
        }
        // Moves one slot as a new owner taking it would: its repack is committed as a reseat.
        public void Reseat(int slot) {
            m_pendingReseats.Add(item: (slot, m_transforms[slot]));
            m_transforms[slot] = m_transforms[slot] with {
                Position = (m_transforms[slot].Position + new Vector3(x: 0f, y: 0f, z: 1f)),
            };
        }
        // Uploads the program again into the same tables, as a rebuilt scene does.
        public void UploadProgram(bool changedGeometry = false) => Engine.UploadProgram(program: (changedGeometry ? Program(albedo: Vector3.One, radius: 2f) : m_program));
        // Drops the engine and builds another on the same device, as an owner does after a device loss.
        public void Rebuild() {
            Engine.Dispose();
            m_pipelines.Dispose();
            m_regionCopy.Dispose();
            m_meshRaster.Dispose();
            m_shadowRaster.Dispose();
            m_glassRaster.Dispose();
            m_occlusion.Dispose();
            Engine = Build();
        }
        // Renders one frame at the given time, by default resetting the tallies first so they read that frame's writes.
        public void Render(float time, bool resetTallies = true, IReadOnlyList<SdfMeshDraw>? meshDraws = null, IReadOnlyList<SdfVolume>? volumes = null, ReadOnlyMemory<byte> slotRateCodes = default) {
            if (resetTallies) {
                Gpu.ResetTallies();
            }

            m_clock.Advance(by: VirtualClock.Frame);
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

            foreach (var (slot, previous) in m_pendingReseats) {
                _ = m_moved.Commit(
                    previous: [previous],
                    reseat: true,
                    slots: m_transforms,
                    start: slot
                );
            }

            m_pendingReseats.Clear();
            // As the residency stages a frame: the background work, through which placed meshes land, is pumped at the
            // top of the frame and after the upload.
            Engine.GpuWork.Pump(tables: Engine);
            Engine.Pack(frame: Frame(
                program: m_program,
                time: time,
                transforms: m_transforms
            ) with {
                MeshDraws = (meshDraws ?? []),
                MovedTransforms = m_moved,
                SlotRateCodes = slotRateCodes,
                Volumes = (volumes ?? []),
            });
            Engine.SubmitUpload();
            Engine.GpuWork.Pump(tables: Engine);
        }
        // Renders the draws until the mesh region owes none of their meshes: the background work lands them over the
        // frames, and the staging after the last landing names every draw.
        public void Land(float time, IReadOnlyList<SdfMeshDraw> meshDraws) {
            Render(meshDraws: meshDraws, time: time);
            // The upload budget is a rate of the device's clock (SdfGpuWork.UploadBytesPerSecond), which each frame steps, so
            // the bound is in frames: ten seconds of them.
            var frames = 0;

            while (Engine.MeshesLanding || Engine.DrawsAwaitMeshes) {
                Assert.True(condition: (frames++ < MostLandingFrames), userMessage: "The mesh region never landed its meshes.");
                Render(meshDraws: meshDraws, time: time);
            }
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
            m_shadowRaster = SdfTestPipelines.ShadowRaster(
                device: Gpu,
                ledger: ledger
            );
            m_glassRaster = SdfTestPipelines.GlassRaster(
                device: Gpu,
                ledger: ledger
            );
            m_occlusion = SdfTestPipelines.Occlusion(
                device: Gpu,
                ledger: ledger
            );
            m_pipelines = SdfTestPipelines.Build(
                device: Gpu,
                kernels: SdfTestPipelines.Kernels(),
                cache: new GpuPassPipelineCache()
            );

            return new SdfWorldTables(
                device: Gpu,
                options: new SdfWorldTablesOptions(
                    DynamicTransformCapacity: m_transforms.Length,
                    Program: m_program,
                    ProgramWordCapacity: m_programWordReserve,
                    Selector: m_selector,
                    WorkLedger: ledger
                ),
                pipelines: m_pipelines,
                shadowEntryRaster: m_shadowRaster,
                shadowRaster: m_shadowRaster,
                shadowExitRaster: m_shadowRaster,
                shadowGlassRaster: m_shadowRaster,
                glassRaster: m_glassRaster,
                occlusion: m_occlusion,
                meshRaster: m_meshRaster,
                regionCopy: m_regionCopy.Compute!
            );
        }
    }
}
