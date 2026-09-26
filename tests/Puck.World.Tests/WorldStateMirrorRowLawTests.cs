using Puck.Abstractions.Counting;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the mirror's row slots (<see cref="WorldStateConversion.Row"/>), the read a pass's array binds: a keyed row
/// read whole, cell <c>i</c> at element <c>i</c> and an absent cell as zero, over the element count its shape states; a
/// tick moving the row re-reads it as one read and moves the slot's revision, a tick moving another row reads nothing,
/// and a steady refresh allocates nothing; and a keyless token naming a keyed row joins the manifest as a row read.
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
