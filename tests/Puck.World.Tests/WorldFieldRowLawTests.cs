using System.Buffers.Binary;
using System.Numerics;

using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Shaders;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the field lattice as a row the state mirror reads: a field row's cells are the delivered lattice cells, cell
/// <c>i</c> at element <c>i</c>, refreshed as one read when a snapshot moves them and not at all when it does not, with
/// no allocation once warm; a field row reaches a pass's region with the same bytes under every residency policy; a
/// height field's brick is baked from the mirror's row slot, once per move; and the colors a program bakes (a palette's
/// surface, bounce, weathering and inset colors, a height field's color, a text screen's ink) are registered in the
/// mirror at install and followed through it.
/// </summary>
public sealed class WorldFieldRowLawTests {
    private const int Depth = 2;
    private const long One = 65536L;
    private const int Width = 4;

    private static WorldFieldsSection Fields() => new(
        Lattice: new WorldFieldLatticeDefinition(
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: Width,
            Depth: Depth
        ),
        Fields: [
            new WorldFieldRow(Name: "heat", Max: 4f),
            new WorldFieldRow(Name: "bump", Max: 1f, HeightScale: 2f, Color: "state.colors.bump"),
        ]
    );
    private static WorldStateRow Colors(string bump) => new(
        Name: CellName.Parse(candidate: "colors"),
        Kind: CellKind.Text,
        Capacity: 8,
        Domain: StateDomain.Keys.Instance,
        Cells: [
            new StateCell(Key: CellName.Parse(candidate: "bump"), Value: CellValue.Text(value: bump)),
            new StateCell(Key: CellName.Parse(candidate: "body"), Value: CellValue.Text(value: "#102030")),
            new StateCell(Key: CellName.Parse(candidate: "bounce"), Value: CellValue.Text(value: "#405060")),
            new StateCell(Key: CellName.Parse(candidate: "deposit"), Value: CellValue.Text(value: "#708090")),
            new StateCell(Key: CellName.Parse(candidate: "ink"), Value: CellValue.Text(value: "#A0B0C0")),
            new StateCell(Key: CellName.Parse(candidate: "fg"), Value: CellValue.Text(value: "#D0E0F0")),
        ]
    );
    private static WorldDefinition Document(string bump = "#3FAF6F") {
        var lattice = Fixtures.WithLattice(
            composite: Fields(),
            definition: Fixtures.BuildDocument()
        );

        return lattice.WithWorldState(rows: [.. lattice.AuthoredState, Colors(bump: bump)]);
    }
    private static long Reads(WorldStateMirror mirror) {
        Assert.True(condition: ((IWorkCounterSource)mirror).TryRead(
            kind: WorldStateMirror.Reads,
            value: out var value
        ));

        return value;
    }
    private static int Ordinal(IWorldStateView view, string row) {
        Assert.True(condition: view.TryResolveRow(
            ordinal: out var ordinal,
            rowName: row
        ));

        return ordinal;
    }
    private static StateBinding Whole(string row) => new(
        Key: null,
        Row: row,
        Target: false
    );

    [Fact]
    public void AFieldRowReadsTheDeliveredCellsAndASnapshotMovingThemReadsItOnce() {
        var definition = Document();
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        // The manifest registers the height field's row whole, which its brick reads; any other field reads through a
        // binding its reader registers, as a pass's array does.
        var bump = mirror.SlotOf(
            binding: Whole(row: "bump"),
            conversion: WorldStateConversion.Row
        );
        var heat = mirror.Bind(
            binding: Whole(row: "heat"),
            conversion: WorldStateConversion.Row
        );

        Assert.True(condition: (bump >= 0));
        Assert.Equal(expected: new double[(Width * Depth)], actual: mirror.RowValues(slot: bump).ToArray());

        var moved = new int[WorldFieldCapacity.MaxFields];
        var bumpRevision = mirror.Changed(slot: bump);
        var before = Reads(mirror: mirror);
        var count = view.ApplyFieldCells(
            deltas: [new FieldCellDelta(Cell: 5, Field: 1, Raw: One), new FieldCellDelta(Cell: 2, Field: 1, Raw: (One / 2L))],
            moved: moved
        );

        Assert.Equal(actual: count, expected: 1);
        Assert.Equal(expected: Ordinal(row: "bump", view: view), actual: moved[0]);

        mirror.RefreshRows(ordinals: moved.AsSpan(length: count, start: 0));

        Assert.Equal(expected: (before + 1L), actual: Reads(mirror: mirror));
        Assert.NotEqual(expected: bumpRevision, actual: mirror.Changed(slot: bump));
        Assert.Equal(expected: [0d, 0d, 0.5d, 0d, 0d, 1d, 0d, 0d], actual: mirror.RowValues(slot: bump).ToArray());

        // A write of the value a cell already holds, and a write the lattice does not hold, move nothing.
        Assert.Equal(
            expected: 0,
            actual: view.ApplyFieldCells(
                deltas: [new FieldCellDelta(Cell: 5, Field: 1, Raw: One), new FieldCellDelta(Cell: (Width * Depth), Field: 1, Raw: One), new FieldCellDelta(Cell: 0, Field: 7, Raw: One)],
                moved: moved
            )
        );

        // A snapshot moving the other field re-reads only its slot.
        bumpRevision = mirror.Changed(slot: bump);
        count = view.ApplyFieldCells(
            deltas: [new FieldCellDelta(Cell: 0, Field: 0, Raw: (3L * One))],
            moved: moved
        );
        mirror.RefreshRows(ordinals: moved.AsSpan(length: count, start: 0));

        Assert.Equal(expected: Ordinal(row: "heat", view: view), actual: moved[0]);
        Assert.Equal(expected: bumpRevision, actual: mirror.Changed(slot: bump));
        Assert.Equal(expected: 3d, actual: mirror.RowValues(slot: heat)[0]);

        FieldCellDelta[][] writes = [
            [new FieldCellDelta(Cell: 1, Field: 1, Raw: One)],
            [new FieldCellDelta(Cell: 1, Field: 1, Raw: 0L)],
        ];
        var allocated = GC.GetAllocatedBytesForCurrentThread();

        for (var step = 0; (step < 64); step++) {
            count = view.ApplyFieldCells(
                deltas: writes[(step % 2)],
                moved: moved
            );
            mirror.RefreshRows(ordinals: moved.AsSpan(length: count, start: 0));
        }

        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - allocated));
    }
    /// <summary>A field row reaches a pass through the region path: the row slot's values, written as a float array's
    /// elements (<see cref="ShaderPipelineParameterLayout.WriteArray"/>) into a region under each residency policy, read
    /// back identical frame by frame over the device-free memory model that runs the staged copy.</summary>
    [Fact]
    public void AFieldRowReachesAPassRegionWithTheSameBytesUnderEveryPolicy() {
        const int Slots = 3;
        var byteCount = ((Width * Depth) * 4);
        var readings = new Dictionary<GpuResidencyPolicy, List<byte[]>>();

        foreach (var policy in Enum.GetValues<GpuResidencyPolicy>()) {
            var definition = Document();
            var view = new WorldDocumentStateView(definition: () => definition);
            var mirror = new WorldStateMirror(view: view);

            mirror.Install(engineTick: 0UL, tick: 0UL);

            var slot = mirror.SlotOf(binding: Whole(row: "bump"), conversion: WorldStateConversion.Row);
            var moved = new int[WorldFieldCapacity.MaxFields];
            var gpu = new UploadModelGpu(reportVersion: 0);
            using var module = gpu.Services.ShaderModuleFactory.Create(
                bytecode: new byte[] { UploadModelGpu.RegionCopyBytecode },
                stage: GpuShaderStage.Compute
            );
            using var copy = gpu.Services.PipelineFactory.Create(
                computeShaderModule: module,
                description: GpuRegion.CopyPipeline,
                name: default
            );
            using var region = new GpuRegion(
                bindings: gpu.Services.Bindings,
                buffers: gpu.Services.BufferFactory,
                byteCount: byteCount,
                copyPipeline: copy,
                memory: GpuHostVisibleMemory.Host,
                name: default,
                policy: policy,
                recorder: gpu.Services.Recorder,
                slotCount: Slots
            );
            var block = new byte[byteCount];
            var frames = new List<byte[]>();
            var raw = 0L;

            for (var frame = 0; (frame < 12); frame++) {
                if ((frame % 2) == 1) {
                    raw = ((raw + 24_577L) % One);

                    var count = view.ApplyFieldCells(
                        deltas: [new FieldCellDelta(Cell: 6, Field: 1, Raw: raw)],
                        moved: moved
                    );

                    mirror.RefreshRows(ordinals: moved.AsSpan(length: count, start: 0));
                }

                ShaderPipelineParameterLayout.WriteArray(
                    elements: block,
                    type: ShaderValueType.Float,
                    values: mirror.RowValues(slot: slot)
                );
                _ = region.Write(bytes: block, offset: 0);
                region.Flush(slot: (frame % Slots));

                var recording = new GpuRegionCopyRecording(
                    begin: static () => 2,
                    readers: GpuStage.ComputeShader,
                    recorder: gpu.Services.Recorder
                );

                recording.Record(
                    handsToReaders: true,
                    region: region,
                    slot: (frame % Slots)
                );
                _ = recording.Finish();

                var read = gpu.Memory(bufferHandle: region.Buffer(slot: (frame % Slots)).BufferHandle)[..byteCount];

                Assert.Equal(actual: read, expected: block);
                Assert.Equal(
                    expected: ((float)((double)FixedQ4816.FromRawBits(value: raw))),
                    actual: BinaryPrimitives.ReadSingleLittleEndian(source: read.AsSpan(start: (6 * 4)))
                );
                frames.Add(item: read);
            }

            readings[policy] = frames;
        }

        Assert.Equal(expected: readings[GpuResidencyPolicy.Staged], actual: readings[GpuResidencyPolicy.Ring]);
        Assert.Equal(expected: readings[GpuResidencyPolicy.Staged], actual: readings[GpuResidencyPolicy.InPlace]);
    }
    /// <summary>A height field's brick is baked from the client's mirror: a snapshot raising a cell uploads one brick
    /// whose voxels see the raised column, a frame with nothing moved uploads nothing, and a snapshot writing the value a
    /// cell already holds uploads nothing.</summary>
    [Fact]
    public void AHeightFieldsBrickIsBakedFromItsMirrorRowOncePerMove() {
        var definition = Document();
        var client = ClientFixtures.Client(definition: definition);
        var emitter = new WorldFieldEmitter(client: client);
        var bakes = new RecordingBrickBakes();

        client.DeliverDefinition(definition: definition);
        emitter.AdvanceBricks(bakes: bakes);

        // The first advance bakes the zero lattice: a flat field, every voxel the empty-space bound.
        Assert.Single(collection: bakes.Uploads);
        Assert.All(
            collection: bakes.Uploads[0],
            action: static voxel => Assert.True(condition: (voxel > 0f))
        );

        emitter.AdvanceBricks(bakes: bakes);
        emitter.AdvanceBricks(bakes: bakes);
        Assert.Single(collection: bakes.Uploads);

        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: 1UL,
            Revision: 0,
            StepTicks: 1UL,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            FieldCells: new[] { new FieldCellDelta(Cell: 5, Field: 1, Raw: One) }
        ));
        emitter.AdvanceBricks(bakes: bakes);

        Assert.Equal(expected: 2, actual: bakes.Uploads.Count);
        // Cell 5 is column (x 1, z 1), raised two units; the voxel at its centre on the origin layer is inside it.
        Assert.Contains(
            collection: bakes.Uploads[1],
            filter: static voxel => (voxel < 0f)
        );

        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: 2UL,
            Revision: 0,
            StepTicks: 1UL,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            FieldCells: new[] { new FieldCellDelta(Cell: 5, Field: 1, Raw: One) }
        ));
        emitter.AdvanceBricks(bakes: bakes);
        emitter.AdvanceBricks(bakes: bakes);

        Assert.Equal(expected: 2, actual: bakes.Uploads.Count);
    }
    /// <summary>Every color a program or a decal bakes is registered in the mirror at install, beside the height
    /// field's row: a palette entry's color and bounce, a weathering deposit's and an inset stop's color, the height
    /// field's color and a text screen's ink.</summary>
    [Fact]
    public void BakedMaterialsAppearInTheMirrorAtInstall() {
        var palette = new PaletteEntryDocument(
            Color: "state.colors.body",
            Emissive: null,
            Specular: null,
            Roughness: null,
            Weathering: new PaletteWeatheringDocument(Deposit: new PaletteSurfaceDocument(
                Color: "state.colors.deposit",
                Metal: 0f,
                Roughness: 0.5f
            )),
            Bounce: "state.colors.bounce",
            Inset: new PaletteInsetDocument(
                Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
                Rotation: new DocumentQuaternion(value: Quaternion.Identity),
                Depth: 0.1f,
                Ior: 1.3f,
                Paint: new PaletteRadialPaintDocument(Stops: [new PaletteRadialStopDocument(Color: "state.colors.ink", Radius: 1f)])
            )
        );
        var prototype = CreationFixtures.Prototype(document: CreationFixtures.Document(
            name: "painted",
            palette: [palette],
            shapes: [CreationFixtures.UnitSphereShape]
        ));
        var document = Document();
        var screens = document.Screens;
        var definition = (document with {
            CreationsRaw = [prototype],
            ScreensRaw = [(screens[0] with { Source = new WorldScreenSource.Text(Lines: ["ink"], Foreground: "state.colors.fg") }), .. screens.Skip(count: 1)],
        });
        var mirror = ClientFixtures.StateMirror(definition: definition);

        foreach (var token in ((string[])["state.colors.body", "state.colors.bounce", "state.colors.deposit", "state.colors.ink", "state.colors.bump", "state.colors.fg"])) {
            Assert.True(
                condition: (mirror.SlotOf(conversion: WorldStateConversion.Color, token: token) >= 0),
                userMessage: token
            );
        }

        Assert.True(condition: (mirror.SlotOf(binding: Whole(row: "bump"), conversion: WorldStateConversion.Row) >= 0));
        Assert.Equal(expected: -1, actual: mirror.SlotOf(binding: Whole(row: "heat"), conversion: WorldStateConversion.Row));

        var colors = new WorldBakedColors(mirror: mirror);

        colors.Begin();
        Assert.Equal(
            expected: HexColor.Parse(fallback: Vector3.Zero, value: "#102030"),
            actual: colors.Resolve(fallback: Vector3.One, value: "state.colors.body")
        );
    }
    /// <summary>A bound color a build baked is read through the mirror and followed through it: a delivery moving its
    /// Text cell is answered as one move and the next resolve reads the new color; a delivery moving another row is no
    /// move, and a literal is never followed.</summary>
    [Fact]
    public void ABakedColorFollowsItsCellThroughTheMirror() {
        var definition = Document(bump: "#3FAF6F");
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);

        mirror.Install(engineTick: 0UL, tick: 0UL);

        var colors = new WorldBakedColors(mirror: mirror);
        var ordinal = Ordinal(row: "colors", view: view);

        colors.Begin();
        Assert.Equal(
            expected: HexColor.Parse(fallback: Vector3.Zero, value: "#3FAF6F"),
            actual: colors.Resolve(fallback: Vector3.One, value: "state.colors.bump")
        );
        Assert.Equal(
            expected: HexColor.Parse(fallback: Vector3.Zero, value: "#ABCDEF"),
            actual: colors.Resolve(fallback: Vector3.One, value: "#ABCDEF")
        );
        Assert.False(condition: colors.TryTakeMove());

        mirror.Refresh(stamp: new WorldStateStamp(EngineTick: 1UL, Everything: false, MovedRows: new[] { Ordinal(row: "heat", view: view) }, Tick: 1UL));
        Assert.False(condition: colors.TryTakeMove());

        definition = Document(bump: "#112233");
        mirror.Refresh(stamp: new WorldStateStamp(EngineTick: 2UL, Everything: false, MovedRows: new[] { ordinal }, Tick: 2UL));

        Assert.True(condition: colors.TryTakeMove());
        Assert.False(condition: colors.TryTakeMove());

        colors.Begin();
        Assert.Equal(
            expected: HexColor.Parse(fallback: Vector3.Zero, value: "#112233"),
            actual: colors.Resolve(fallback: Vector3.One, value: "state.colors.bump")
        );
    }

    // A brick service that completes every upload at once and keeps each upload's voxels.
    private sealed class RecordingBrickBakes : ISdfBrickBakeService {
        private readonly ulong[] m_serials = new ulong[SdfBrickPoolLayout.MaxBricks];

        public List<float[]> Uploads { get; } = [];

        public bool BrickBakeAvailable => true;

        public BrickBakeStatus GetBrickState(int slot) => new(
            Serial: m_serials[slot],
            State: ((m_serials[slot] == 0UL)
                ? BrickBakeState.Empty
                : BrickBakeState.Ready)
        );
        public void RequestBrickBake(int slot, BrickBakeRequest request) => throw new InvalidOperationException(message: "A field brick is uploaded, never requested.");
        public void UploadBrick(int slot, int dimX, int dimY, int dimZ, ReadOnlySpan<float> voxels) {
            Uploads.Add(item: voxels.ToArray());
            m_serials[slot]++;
        }
    }
}
