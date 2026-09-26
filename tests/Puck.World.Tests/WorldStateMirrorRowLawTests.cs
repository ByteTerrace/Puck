using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the mirror's row slots (<see cref="WorldStateConversion.Row"/>), the read a pass's array binds: a keyed row
/// read whole, cell <c>i</c> at element <c>i</c> and an absent cell as zero, over the element count its shape states; a
/// tick moving the row re-reads it as one read and moves the slot's revision, a tick moving another row reads nothing,
/// and a steady refresh allocates nothing; one row's region reads the same bytes under every residency policy; and
/// a keyless token naming a keyed row joins the manifest as a row read.
/// </summary>
public sealed class WorldStateMirrorRowLawTests {
    private const int Capacity = 6;

    private static WorldDefinition Board(long third) => Fixtures.BuildDocument().WithWorldState(rows: [
        new WorldStateRow(
            Name: CellName.Parse(candidate: "tiles"),
            Kind: CellKind.Int,
            Min: 0,
            Max: 255,
            Capacity: Capacity,
            Domain: StateDomain.Keys.Instance,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "0"), Value: CellValue.Int(value: 7)),
                new StateCell(Key: CellName.Parse(candidate: "2"), Value: CellValue.Int(value: third)),
                new StateCell(Key: CellName.Parse(candidate: "5"), Value: CellValue.Int(value: 9)),
            ]
        ),
        new WorldStateRow(
            Name: CellName.Parse(candidate: "other"),
            Kind: CellKind.Int,
            Min: 0,
            Max: 1000,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: third))]
        ),
    ]);
    private static long Reads(WorldStateMirror mirror) {
        Assert.True(condition: ((IWorkCounterSource)mirror).TryRead(
            kind: WorldStateMirror.Reads,
            value: out var value
        ));

        return value;
    }
    private static WorldStateStamp Stamp(ulong tick, params int[] moved) => new(
        EngineTick: (tick * 1680UL),
        Everything: false,
        MovedRows: moved,
        Tick: tick
    );

    [Fact]
    public void ARowSlotReadsTheWholeRowAndATickMovingItReadsItOnce() {
        var definition = Board(third: 3);
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);
        var slot = mirror.Bind(
            binding: new StateBinding(
                Key: null,
                Row: "tiles",
                Target: false
            ),
            conversion: WorldStateConversion.Row
        );

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        Assert.Equal(
            actual: mirror.RowValues(slot: slot).ToArray(),
            expected: [7d, 0d, 3d, 0d, 0d, 9d]
        );
        Assert.True(condition: view.TryResolveRow(
            ordinal: out var tiles,
            rowName: "tiles"
        ));
        Assert.True(condition: view.TryResolveRow(
            ordinal: out var other,
            rowName: "other"
        ));

        var revision = mirror.Changed(slot: slot);

        definition = Board(third: 42);

        var before = Reads(mirror: mirror);

        mirror.Refresh(stamp: Stamp(
            moved: [other],
            tick: 1UL
        ));
        Assert.Equal(expected: before, actual: Reads(mirror: mirror));
        Assert.Equal(expected: revision, actual: mirror.Changed(slot: slot));

        mirror.Refresh(stamp: Stamp(
            moved: [tiles],
            tick: 2UL
        ));
        Assert.Equal(expected: (before + 1L), actual: Reads(mirror: mirror));
        Assert.NotEqual(expected: revision, actual: mirror.Changed(slot: slot));
        Assert.Equal(expected: 42d, actual: mirror.RowValues(slot: slot)[2]);

        int[] movedTiles = [tiles];
        var allocated = GC.GetAllocatedBytesForCurrentThread();

        for (var tick = 3UL; (tick < 67UL); tick++) {
            mirror.Refresh(stamp: Stamp(
                moved: movedTiles,
                tick: tick
            ));
        }

        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - allocated));
    }
    /// <summary>One row's region reads the same bytes under every residency policy: the row slot's values, written as an
    /// int array's elements (<see cref="ShaderPipelineParameterLayout.WriteArray"/>) into a region under each policy, read
    /// back identical frame by frame over the device-free memory model that runs the staged copy.</summary>
    [Fact]
    public void OneRowsRegionReadsTheSameBytesUnderEveryPolicy() {
        const int Slots = 3;
        var byteCount = (8 * 4);
        var readings = new Dictionary<GpuResidencyPolicy, List<byte[]>>();

        foreach (var policy in Enum.GetValues<GpuResidencyPolicy>()) {
            var third = 3L;
            var definition = Board(third: third);
            var view = new WorldDocumentStateView(definition: () => definition);
            var mirror = new WorldStateMirror(view: view);
            var slot = mirror.Bind(
                binding: new StateBinding(Key: null, Row: "tiles", Target: false),
                conversion: WorldStateConversion.Row
            );

            mirror.Install(engineTick: 0UL, tick: 0UL);
            Assert.True(condition: view.TryResolveRow(ordinal: out var tiles, rowName: "tiles"));

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

            for (var frame = 0; (frame < 12); frame++) {
                if ((frame % 2) == 1) {
                    third = ((third + 37L) % 256L);
                    definition = Board(third: third);
                    mirror.Refresh(stamp: Stamp(moved: [tiles], tick: ((ulong)frame)));
                }

                ShaderPipelineParameterLayout.WriteArray(
                    elements: block,
                    type: ShaderValueType.Int,
                    values: mirror.RowValues(slot: slot)
                );
                _ = region.Write(bytes: block, offset: 0);
                region.Flush(slot: (frame % Slots));

                // The owed copy is recorded as an owner records it, behind the barrier ordering earlier reads.
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
                Assert.Equal(expected: ((int)third), actual: System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(source: read.AsSpan(start: 8)));
                frames.Add(item: read);
            }

            readings[policy] = frames;
        }

        Assert.Equal(expected: readings[GpuResidencyPolicy.Staged], actual: readings[GpuResidencyPolicy.Ring]);
        Assert.Equal(expected: readings[GpuResidencyPolicy.Staged], actual: readings[GpuResidencyPolicy.InPlace]);
    }
    [Fact]
    public void AKeylessTokenNamingAKeyedRowJoinsTheManifestAsARowRead() {
        var definition = Board(third: 3);
        var document = (definition with {
            ViewsRaw = (definition.Views with {
                Graphs = [new WorldViewGraph(
                    Name: "board",
                    Parameters: new Dictionary<string, IReadOnlyDictionary<string, BindableScalar>>(comparer: StringComparer.Ordinal) {
                        ["draw"] = new Dictionary<string, BindableScalar>(comparer: StringComparer.Ordinal) {
                            ["tiles"] = new BindableScalar(binding: "state.tiles"),
                            ["level"] = new BindableScalar(binding: "state.other"),
                        },
                    },
                    Source: "graphs/board.graph.json"
                )],
            }),
        });
        var bindings = WorldPresentationManifest.Of(definition: document).Bindings.ToArray();

        Assert.Contains(
            collection: bindings,
            expected: new WorldPresentationBinding(
                Binding: new StateBinding(Key: null, Row: "tiles", Target: false),
                Conversion: WorldStateConversion.Row
            )
        );
        Assert.Contains(
            collection: bindings,
            expected: new WorldPresentationBinding(
                Binding: new StateBinding(Key: null, Row: "other", Target: false),
                Conversion: WorldStateConversion.Number
            )
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: document,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
}
