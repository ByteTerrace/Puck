using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.Overlays;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for a radial's drawn rings: their labels, icons and hub label read the wheel's label and icon rows through the
/// seat's routed state mirror, and they are rebuilt only when one of the cells they read moves, never for a state
/// delivery that moves some other row.
/// </summary>
public sealed class WorldWheelRingsLawTests {
    private const string LabelRow = "labels";
    private const int Repetitions = 64;

    private static WorldStateRow Labels(string north) => new(
        Name: CellName.Parse(candidate: LabelRow),
        Kind: CellKind.Text,
        Capacity: 8,
        Cells: [
            new StateCell(
                Key: CellName.Parse(candidate: "north"),
                Value: CellValue.Text(value: north)
            ),
            new StateCell(
                Key: CellName.Parse(candidate: BindingWheelDefinition.HubLabelKey),
                Value: CellValue.Text(value: "Back")
            ),
        ]
    );
    private static WorldStateRow Other(long value) => new(
        Name: CellName.Parse(candidate: "other"),
        Kind: CellKind.Int,
        Min: 0,
        Max: 1000,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: value)
            )]
    );
    private static WorldDefinition Document(string north, long other) => Fixtures.BuildDocument().WithWorldState(rows: [
        Labels(north: north),
        Other(value: other),
    ]);
    private static WorldStateStamp Stamp(ulong tick, int moved) => new(
        EngineTick: (tick * 1680UL),
        Everything: false,
        MovedRows: new[] { moved },
        Tick: tick
    );
    // One ring of two sectors: the first keyed into the label row, the second with no id, which draws its command.
    private static BindingProfileDocument WheelDocument() => new(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: "hold",
                        Entries: []
                    )
                )],
            Wheels: [new BindingWheelDefinition(
                    Id: "menu",
                    Group: "play",
                    HoldPages: ["hold"],
                    Rings: [new BindingPageDefinition(
                            Id: "actions",
                            Entries: [
                        new BindingPageEntryDefinition(
                                    Sources: null,
                                    Command: "act.north",
                                    Id: "north"
                                ),
                        new BindingPageEntryDefinition(
                                    Sources: null,
                                    Command: "act.south"
                                ),
                    ]
                        )],
                    LabelRow: $"state.{LabelRow}"
                )]
        );
    private static BindingWheelView Wheel() => new PagedInputBindings(profile: BindingProfile.Compile(document: WheelDocument())).WheelFor(slot: 0)!;
    // The seat registers its wheel's cells with the mirror it reads through, as WorldSeatBindings does on each route.
    private static void RegisterSeat(WorldStateMirror mirror, WorldDefinition definition) => mirror.Register(
        bindings: WorldPresentationManifest.SeatBindings(
            bar: WorldBindingBarAuthoring.Absent,
            bindings: WheelDocument(),
            bodyIndex: 0,
            definition: definition
        ),
        owner: mirror
    );

    [Fact]
    public void RingsRebuildOnlyWhenACellTheyReadMoves() {
        var definition = Document(
            north: "North",
            other: 0L
        );
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var rings = new WorldWheelRings(resolveIcon: static _ => OverlayResolvedGlyph.None);
        var wheel = Wheel();

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        RegisterSeat(
            definition: definition,
            mirror: mirror
        );

        var drawn = rings.Resolve(
            mirror: mirror,
            wheel: wheel
        );

        Assert.Equal(
            actual: rings.Builds,
            expected: 1
        );
        Assert.Equal(
            actual: drawn[0].Sectors.Span[0].Label,
            expected: "North"
        );
        Assert.Equal(
            actual: drawn[0].Sectors.Span[1].Label,
            expected: "act.south"
        );
        Assert.Equal(
            actual: rings.HubLabel,
            expected: "Back"
        );

        // A delivery that moves a row the wheel does not read leaves the rings built.
        definition = Document(
            north: "North",
            other: 7L
        );
        mirror.Refresh(stamp: Stamp(
            moved: 1,
            tick: 1UL
        ));
        Assert.Same(
            actual: rings.Resolve(
                mirror: mirror,
                wheel: wheel
            ),
            expected: drawn
        );
        Assert.Equal(
            actual: rings.Builds,
            expected: 1
        );

        // A delivery that moves the label row rebuilds them once, with the new text.
        definition = Document(
            north: "Up",
            other: 7L
        );
        mirror.Refresh(stamp: Stamp(
            moved: 0,
            tick: 2UL
        ));
        drawn = rings.Resolve(
            mirror: mirror,
            wheel: wheel
        );
        Assert.Equal(
            actual: rings.Builds,
            expected: 2
        );
        Assert.Equal(
            actual: drawn[0].Sectors.Span[0].Label,
            expected: "Up"
        );
        _ = rings.Resolve(
            mirror: mirror,
            wheel: wheel
        );
        Assert.Equal(
            actual: rings.Builds,
            expected: 2
        );

        // An installed document carries rows of its own, and another mirror is another authority's rows.
        mirror.Install(
            engineTick: (3UL * 1680UL),
            tick: 3UL
        );
        _ = rings.Resolve(
            mirror: mirror,
            wheel: wheel
        );
        Assert.Equal(
            actual: rings.Builds,
            expected: 3
        );
        _ = rings.Resolve(
            mirror: ClientFixtures.StateMirror(definition: definition),
            wheel: wheel
        );
        Assert.Equal(
            actual: rings.Builds,
            expected: 4
        );
    }
    [Fact]
    public void ResolvingUnmovedRingsAllocatesNothing() {
        var definition = Document(
            north: "North",
            other: 0L
        );
        var mirror = ClientFixtures.StateMirror(definition: definition);
        var rings = new WorldWheelRings(resolveIcon: static _ => OverlayResolvedGlyph.None);
        var wheel = Wheel();
        var sink = 0;

        RegisterSeat(
            definition: definition,
            mirror: mirror
        );

        void Frames() {
            for (var repetition = 0; (repetition < Repetitions); repetition++) {
                sink += rings.Resolve(
                    mirror: mirror,
                    wheel: wheel
                ).Length;
            }
        }

        Frames();
        Assert.Equal(
            expected: 0L,
            actual: AllocationWindow.Least(window: Frames)
        );
        Assert.Equal(
            actual: rings.Builds,
            expected: 1
        );
        Assert.True(condition: (sink > 0));
    }
}
