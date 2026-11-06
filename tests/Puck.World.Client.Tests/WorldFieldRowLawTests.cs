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
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// Laws for the field lattice as a row the state mirror reads: a field row's cells are the delivered lattice cells, cell
/// <c>i</c> at element <c>i</c>, refreshed as one read when a snapshot moves them and not at all when it does not, with
/// no allocation once warm; a field row reaches a pass's region with the same bytes under every residency policy; a
/// height field's sampled values are baked from the mirror's row slot, published once per move without moving the
/// program revision; and the colors a program bakes (a palette's
/// surface, fill, weathering and inset colors, a height field's color, a text screen's ink) are registered in the
/// mirror at install and followed through it.
/// </summary>
public sealed class WorldFieldRowLawTests {
    private const int Depth = 2;
    private const long One = 65536L;
    private const int Width = 4;

    private static WorldFieldsSection Fields() => new(Name: "world",
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
            new StateCell(Key: CellName.Parse(candidate: "fill"), Value: CellValue.Text(value: "#405060")),
            new StateCell(Key: CellName.Parse(candidate: "bleed"), Value: CellValue.Text(value: "#8090A0")),
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
    public void AFieldMaterialUsesThePaletteAndRegistersItsBoundColors() {
        var fields = Fields();
        var material = new PaletteEntryDocument(Color: "state.colors.body", Emissive: null, Specular: 0.5f,
            Roughness: 0.16f, Coat: 0.3f, Fill: "state.colors.fill", Glass: new PaletteGlassDocument(Depth: 2f, Ior: 1.333f));
        var definition = Fixtures.WithLattice(composite: fields with {
            Fields = [fields.Fields[0], fields.Fields[1] with { Material = material }],
        }, definition: Fixtures.BuildDocument());
        definition = definition.WithWorldState(rows: [.. definition.AuthoredState, Colors(bump: "#3FAF6F")]);
        var client = ClientFixtures.Client(definition: definition);
        client.DeliverDefinition(definition: definition, version: default);
        Assert.True(condition: client.StateMirror.SlotOf(conversion: WorldStateConversion.Color, token: "state.colors.fill") >= 0);
        var emitter = new WorldFieldEmitter(client: client);
        var builder = new SdfProgramBuilder();
        var context = new SdfEmitContext(true, 0f, Vector3.Zero, Vector3.Zero, 0);
        emitter.Emit(builder: builder, context: in context);
        var program = builder.Build();
        var actual = Assert.Single(collection: program.Appearance.Materials);
        Assert.Equal(expected: 0.16f, actual: actual.Roughness);
        Assert.Equal(expected: 0.3f, actual: actual.Coat);
        Assert.NotNull(@object: actual.Glass);
        Assert.True(condition: program.HasGlass);
        var range = Assert.Single(collection: program.Instances);
        Assert.True(condition: program.IsGlassRange(first: range.First, end: range.End));
    }
    [Fact]
    public void AFieldRowReadsTheDeliveredCellsAndASnapshotMovingThemReadsItOnce() {
        var definition = Document();
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        // The manifest registers the height field's row whole, which its sampled values are baked from; any other field reads through a
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
            deltas: [new FieldCellDelta(Region: "world", FieldName: "bump", Cell: 5, Field: 1, Raw: One), new FieldCellDelta(Region: "world", FieldName: "bump", Cell: 2, Field: 1, Raw: (One / 2L))],
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
                deltas: [new FieldCellDelta(Region: "world", FieldName: "bump", Cell: 5, Field: 1, Raw: One), new FieldCellDelta(Region: "world", FieldName: "bump", Cell: (Width * Depth), Field: 1, Raw: One), new FieldCellDelta(Region: "world", FieldName: "unknown", Cell: 0, Field: 7, Raw: One)],
                moved: moved
            )
        );

        // A snapshot moving the other field re-reads only its slot.
        bumpRevision = mirror.Changed(slot: bump);
        count = view.ApplyFieldCells(
            deltas: [new FieldCellDelta(Region: "world", FieldName: "heat", Cell: 0, Field: 0, Raw: (3L * One))],
            moved: moved
        );
        mirror.RefreshRows(ordinals: moved.AsSpan(length: count, start: 0));

        Assert.Equal(expected: Ordinal(row: "heat", view: view), actual: moved[0]);
        Assert.Equal(expected: bumpRevision, actual: mirror.Changed(slot: bump));
        Assert.Equal(expected: 3d, actual: mirror.RowValues(slot: heat)[0]);

        FieldCellDelta[][] writes = [
            [new FieldCellDelta(Region: "world", FieldName: "bump", Cell: 1, Field: 1, Raw: One)],
            [new FieldCellDelta(Region: "world", FieldName: "bump", Cell: 1, Field: 1, Raw: 0L)],
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
    [Fact]
    public void RegionalRowsKeepTheirOwnCellsAndSampledExtentsAcrossAnUnrelatedReshape() {
        var first = new WorldFieldsSection(Name: "first",
            Lattice: new WorldFieldLatticeDefinition(Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 2, Depth: 1),
            Fields: [new WorldFieldRow(Name: "first-level", Max: 1f, HeightScale: 1f, Color: "#204060")]);
        var second = new WorldFieldsSection(Name: "second",
            Lattice: new WorldFieldLatticeDefinition(Origin: new DocumentVector3(x: 12f, y: 3f, z: 0f), CellSize: 0.5f, Width: 3, Depth: 2),
            Fields: [new WorldFieldRow(Name: "second-level", Max: 1f, HeightScale: 1f, Color: "#604020")]);
        var definition = Fixtures.BuildDocument() with { StateRaw = WorldFieldsSection.ToStateSection(composite: [first, second]) };
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);

        mirror.Install(engineTick: 0UL, tick: 0UL);
        var firstSlot = mirror.SlotOf(binding: Whole(row: "first-level"), conversion: WorldStateConversion.Row);
        var secondSlot = mirror.SlotOf(binding: Whole(row: "second-level"), conversion: WorldStateConversion.Row);
        var writes = new[] {
            new FieldCellDelta(Region: "first", FieldName: "first-level", Cell: 1, Field: 0, Raw: One),
            new FieldCellDelta(Region: "second", FieldName: "second-level", Cell: 5, Field: 0, Raw: (One / 2)),
        };
        var moved = new int[StateCapacity.MaxRows];
        var count = view.ApplyFieldCells(deltas: writes, moved: moved);

        mirror.RefreshRows(ordinals: moved.AsSpan(length: count, start: 0));
        Assert.Equal(actual: count, expected: 2);
        Assert.Equal(expected: [0d, 1d], actual: mirror.RowValues(slot: firstSlot).ToArray());
        Assert.Equal(expected: [0d, 0d, 0d, 0d, 0d, 0.5d], actual: mirror.RowValues(slot: secondSlot).ToArray());
        // A valid local ordinal with the other region's row identity must never address that region's cells.
        Assert.Equal(expected: 0, actual: view.ApplyFieldCells(deltas: [
            new FieldCellDelta(Region: "first", FieldName: "second-level", Cell: 1, Field: 0, Raw: One),
        ], moved: moved));

        var client = ClientFixtures.Client(definition: definition);

        client.DeliverDefinition(definition: definition, version: default);
        client.DeliverSnapshot(snapshot: new WorldSnapshot(Tick: 0UL, Revision: 0, StepTicks: 1UL,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty, FieldCells: writes));
        var emitter = new WorldFieldEmitter(client: client);

        for (var step = 0; (step < 4); step++) { emitter.PrepareFrame(); }
        // Each region's reference (its columns plus a border, three rows tall whatever the heights), then each one's heights.
        Assert.Equal(expected: new[] { (4, 3, 3), (5, 3, 4), (2, 1, 1), (3, 1, 2) }, actual: Emitted(emitter: emitter).SampledFields.Select(selector: static field => (field.DimX, field.DimY, field.DimZ)).ToArray());

        first = first with { Lattice = first.Lattice with { Width = 4 } };
        definition = definition with { StateRaw = WorldFieldsSection.ToStateSection(composite: [first, second]) };
        mirror.Install(engineTick: 1UL, tick: 1UL);
        secondSlot = mirror.SlotOf(binding: Whole(row: "second-level"), conversion: WorldStateConversion.Row);
        Assert.Equal(expected: [0d, 0d, 0d, 0d, 0d, 0.5d], actual: mirror.RowValues(slot: secondSlot).ToArray());
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
            var gpu = new UploadModelGpu();
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
                        deltas: [new FieldCellDelta(Region: "world", FieldName: "bump", Cell: 6, Field: 1, Raw: raw)],
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
    /// <summary>A height field's sampled values are baked from the client's mirror and published as snapshots of one
    /// field: the first frame publishes the flat lattice, a frame with nothing moved publishes nothing, a snapshot raising
    /// a cell publishes one snapshot whose voxels see the raised column, and a snapshot writing the value a cell already
    /// holds publishes nothing.</summary>
    [Fact]
    public void AHeightFieldsValuesArePublishedFromItsMirrorRowOncePerMove() {
        var definition = Document();
        var client = ClientFixtures.Client(definition: definition);
        var emitter = new WorldFieldEmitter(client: client);

        client.DeliverDefinition(definition: definition, version: default);
        emitter.PrepareFrame();

        // The first frame publishes the zero lattice: no column raised, so every reference voxel is the empty-space bound.
        var field = Assert.Single(collection: Emitted(emitter: emitter).HeightFields).Field;
        var flat = field.Reference.Current;
        var level = field.Heights.Current;

        Assert.All(collection: flat.Values.ToArray(), action: static voxel => Assert.True(condition: (voxel > 0f)));

        emitter.PrepareFrame();
        emitter.PrepareFrame();
        Assert.Same(expected: flat, actual: field.Reference.Current);
        Assert.Same(expected: level, actual: field.Heights.Current);

        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: 1UL,
            Revision: 0,
            StepTicks: 1UL,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            FieldCells: new[] { new FieldCellDelta(Region: "world", FieldName: "bump", Cell: 5, Field: 1, Raw: One) }
        ));
        emitter.PrepareFrame();

        var raised = field.Reference.Current;
        var risen = field.Heights.Current;

        // A column raised from nothing keys a new reference; its height moves the heights.
        Assert.NotSame(expected: flat, actual: raised);
        Assert.NotSame(expected: level, actual: risen);
        Assert.Same(expected: field, actual: Assert.Single(collection: Emitted(emitter: emitter).HeightFields).Field);
        // Cell 5 is column (x 1, z 1), raised; the voxel at its centre on the origin layer is inside it.
        Assert.Contains(collection: raised.Values.ToArray(), filter: static voxel => (voxel < 0f));
        Assert.Contains(collection: risen.Values.ToArray(), filter: static height => (height > 0f));

        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: 2UL,
            Revision: 0,
            StepTicks: 1UL,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            FieldCells: new[] { new FieldCellDelta(Region: "world", FieldName: "bump", Cell: 5, Field: 1, Raw: One) }
        ));
        emitter.PrepareFrame();
        emitter.PrepareFrame();

        Assert.Same(expected: raised, actual: field.Reference.Current);
        Assert.Same(expected: risen, actual: field.Heights.Current);

        // A raised column's height moving publishes the heights alone: the reference, and so the mesh, stands.
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: 3UL,
            Revision: 0,
            StepTicks: 1UL,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            FieldCells: new[] { new FieldCellDelta(Region: "world", FieldName: "bump", Cell: 5, Field: 1, Raw: (One / 2)) }
        ));
        emitter.PrepareFrame();

        Assert.Same(expected: raised, actual: field.Reference.Current);
        Assert.NotSame(expected: risen, actual: field.Heights.Current);
    }
    /// <summary>The field emitter's program revision moves exactly when its live program changes: a world that holds no
    /// field never moves it, so the first frame's program is never rebuilt unchanged; a field's first bake, which makes the
    /// field the program names, moves it once; and a drawn height change, which publishes a new snapshot of that field,
    /// leaves it standing.</summary>
    [Fact]
    public void TheFieldProgramRevisionMovesOnlyWhenTheLiveProgramChanges() {
        var bare = Fixtures.BuildDocument();
        var bareClient = ClientFixtures.Client(definition: bare);
        var bareEmitter = new WorldFieldEmitter(client: bareClient);

        bareClient.DeliverDefinition(definition: bare, version: default);
        var unattached = Revision(emitter: bareEmitter);

        bareEmitter.PrepareFrame();
        Assert.Equal(expected: unattached, actual: Revision(emitter: bareEmitter));

        var definition = Document();
        var client = ClientFixtures.Client(definition: definition);
        var emitter = new WorldFieldEmitter(client: client);

        client.DeliverDefinition(definition: definition, version: default);
        var start = Revision(emitter: emitter);

        emitter.PrepareFrame();
        var made = Revision(emitter: emitter);

        Assert.NotEqual(actual: made, expected: start);
        client.DeliverSnapshot(snapshot: new WorldSnapshot(
            Tick: 1UL,
            Revision: 0,
            StepTicks: 1UL,
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            FieldCells: new[] { new FieldCellDelta(Region: "world", FieldName: "bump", Cell: 5, Field: 1, Raw: One) }
        ));
        emitter.PrepareFrame();
        Assert.Equal(expected: made, actual: Revision(emitter: emitter));
    }
    /// <summary>Every color a program or a decal bakes is registered in the mirror at install, beside the height
    /// field's row: a palette entry's color, fill and bleed, a weathering deposit's and an inset stop's color, the height
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
            Fill: "state.colors.fill",
            Bleed: "state.colors.bleed",
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

        foreach (var token in ((string[])["state.colors.body", "state.colors.fill", "state.colors.bleed", "state.colors.deposit", "state.colors.ink", "state.colors.bump", "state.colors.fg"])) {
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
    /// <summary>A field of the most cells a lattice may hold (<see cref="WorldFieldCapacity.MaxCells"/>) refreshes by
    /// index: once warm, a snapshot moving it and the refresh that re-reads its row allocate nothing, far past the keys
    /// a keyed row's cells are named by.</summary>
    [Fact]
    public void AMaximumFieldRefreshAllocatesNothingOnceWarm() {
        const int MaxWidth = WorldFieldCapacity.MaxExtent;
        const int MaxDepth = (WorldFieldCapacity.MaxCells / WorldFieldCapacity.MaxExtent);
        var definition = Fixtures.WithLattice(
            composite: new WorldFieldsSection(Name: "world",
                Lattice: new WorldFieldLatticeDefinition(
                    Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
                    CellSize: 1f,
                    Width: MaxWidth,
                    Depth: MaxDepth
                ),
                Fields: [new WorldFieldRow(Name: "heat", Max: 4f)]
            ),
            definition: Fixtures.BuildDocument()
        );
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);

        mirror.Install(engineTick: 0UL, tick: 0UL);

        var slot = mirror.Bind(binding: Whole(row: "heat"), conversion: WorldStateConversion.Row);
        var moved = new int[WorldFieldCapacity.MaxFields];
        FieldCellDelta[][] writes = [
            [new FieldCellDelta(Region: "world", FieldName: "heat", Cell: (WorldFieldCapacity.MaxCells - 1), Field: 0, Raw: One)],
            [new FieldCellDelta(Region: "world", FieldName: "heat", Cell: (WorldFieldCapacity.MaxCells - 1), Field: 0, Raw: 0L)],
        ];

        void Step(int step) {
            var count = view.ApplyFieldCells(
                deltas: writes[(step % 2)],
                moved: moved
            );

            mirror.RefreshRows(ordinals: moved.AsSpan(length: count, start: 0));
        }

        Assert.Equal(expected: WorldFieldCapacity.MaxCells, actual: mirror.RowValues(slot: slot).Length);
        Step(step: 0);
        Assert.Equal(expected: 1d, actual: mirror.RowValues(slot: slot)[(WorldFieldCapacity.MaxCells - 1)]);
        Step(step: 1);

        var allocated = GC.GetAllocatedBytesForCurrentThread();

        for (var step = 0; (step < 64); step++) {
            Step(step: step);
        }

        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - allocated));
    }
    /// <summary>A document's own colors (<see cref="WorldBakedColors.Of"/>) build no mirror for a build that bakes only
    /// literals, and resolving literals allocates nothing; the first bound color installs one and reads its cell.</summary>
    [Fact]
    public void ADocumentsOwnColorsBuildAMirrorOnlyForABoundColor() {
        var colors = WorldBakedColors.Of(definition: Document());

        colors.Begin();

        var allocated = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 64); index++) {
            _ = colors.Resolve(fallback: Vector3.One, value: "#ABCDEF");
            _ = colors.Resolve(fallback: Vector3.One, value: null);
        }

        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - allocated));
        Assert.False(condition: colors.IsMirrored);
        Assert.False(condition: colors.TryTakeMove());
        Assert.Equal(
            expected: HexColor.Parse(fallback: Vector3.Zero, value: "#3FAF6F"),
            actual: colors.Resolve(fallback: Vector3.One, value: "state.colors.bump")
        );
        Assert.True(condition: colors.IsMirrored);
    }

    // The emitter's one revision component.
    private static int Revision(WorldFieldEmitter emitter) {
        Span<int> revision = stackalloc int[1];

        emitter.WriteRevision(destination: revision);

        return revision[0];
    }

    // The program the emitter's live frame emits.
    private static SdfProgram Emitted(WorldFieldEmitter emitter) {
        var builder = new SdfProgramBuilder();

        emitter.Emit(builder: builder, context: new SdfEmitContext(Probe: false, Time: 0f, RenderOrigin: Vector3.Zero, ParkPosition: Vector3.Zero, SlotBase: 0));
        return builder.Build();
    }
}
