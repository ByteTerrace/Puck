using System.Numerics;
using System.Text.Json;

using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for reading the presentation manifest's registered slots (<see cref="WorldStateMirror.SlotOf(in StateBinding, WorldStateConversion)"/>):
/// a lookup registers, reads and allocates nothing, so after an install every consumer's first frame over the
/// flagship world reads no cell and adds no slot; a seat's own reads — its binding contexts, its radial wheel's label
/// and icon cells, its binding bar's icon, layout and model cells — are compiled from its composition
/// (<see cref="WorldPresentationManifest.SeatBindings"/>) and registered on the mirror its route reads through, and
/// withdrawn from a mirror it leaves; and a document swap retires every slot only the previous manifest registered and
/// no holder reads, keeps the index of every slot both manifests register, and never answers a lookup with a retired
/// slot.
/// </summary>
public sealed class WorldPresentationLookupLawTests {
    private const int Repetitions = 64;

    private static long Reads(WorldStateMirror mirror) {
        Assert.True(condition: ((IWorkCounterSource)mirror).TryRead(
            kind: WorldStateMirror.Reads,
            value: out var value
        ));

        return value;
    }
    private static WorldPresentationBinding Number(string row, string? key = null, bool target = false) => new(
        Binding: new StateBinding(
            Key: key,
            Row: row,
            Target: target
        ),
        Conversion: WorldStateConversion.Number
    );
    private static WorldStateRow Keyed(string name, CellKind kind, params string[] keys) => new(
        Name: CellName.Parse(candidate: name),
        Kind: kind,
        Min: ((kind == CellKind.Int)
            ? 0
            : null),
        Max: ((kind == CellKind.Int)
            ? 9
            : null),
        Capacity: 8,
        Cells: [.. keys.Select(selector: key => new StateCell(
            Key: CellName.Parse(candidate: key),
            Value: ((kind == CellKind.Int)
                ? CellValue.Int(value: 2)
                : CellValue.Text(value: key))
        ))]
    );
    private static WorldStateRow Slot(string name, long value) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Min: 0,
        Max: 9,
        Cells: [new StateCell(
            Key: WorldStateRow.SlotKey,
            Value: CellValue.Int(value: value)
        )]
    );
    // A seat's binding document: one page whose entries key the bar's icon row by action and by id, two state-backed
    // context families (one on a slot row, one on a keyed row), and a wheel whose ring keys its label and icon rows by
    // the one sector carrying an id.
    private static BindingProfileDocument SeatDocument() => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [],
        Chords: [new BindingChordDefinition(
            Group: "play",
            Chord: [],
            Page: new BindingPageDefinition(
                Id: "hold",
                Entries: [
                    new BindingPageEntryDefinition(
                        Command: "jump",
                        Sources: ["gamepad.a"]
                    ),
                    new BindingPageEntryDefinition(
                        Command: "dash",
                        Id: "sprint",
                        Sources: ["gamepad.b"]
                    ),
                ]
            )
        )],
        Contexts: [
            new BindingContextDefinition(
                Family: "state:mode",
                Group: "play",
                State: "1"
            ),
            new BindingContextDefinition(
                Family: "state:stance",
                Group: "play",
                State: "2"
            ),
        ],
        Wheels: [new BindingWheelDefinition(
            Group: "play",
            HoldPages: ["hold"],
            IconRow: "state.icons",
            Id: "menu",
            LabelRow: "state.labels",
            Rings: [new BindingPageDefinition(
                Id: "actions",
                Entries: [
                    new BindingPageEntryDefinition(
                        Command: "act.north",
                        Id: "north",
                        Sources: null
                    ),
                    new BindingPageEntryDefinition(
                        Command: "act.south",
                        Sources: null
                    ),
                ]
            )]
        )]
    );
    private static WorldBindingBarAuthoring SeatBar() => (WorldBindingBarAuthoring.Absent with {
        IconRow = "state.icons",
        LayoutCell = "state.layout",
        ModelCell = "state.model",
    });
    private static WorldDefinition Rows() => Fixtures.BuildDocument().WithWorldState(rows: [
        Slot(name: "mode", value: 1),
        Keyed(name: "stance", kind: CellKind.Int, keys: "0"),
        Keyed(name: "labels", kind: CellKind.Text, keys: ["north", BindingWheelDefinition.HubLabelKey]),
        Keyed(name: "icons", kind: CellKind.Text, keys: ["jump", "sprint", "north"]),
    ]);
    private static WorldDefinition SeatWorld() => Rows() with {
        BindingOverlaysRaw = [new WorldBindingOverlay(
            BindingBar: SeatBar(),
            Document: SeatDocument(),
            Id: "seat"
        )],
    };
    // The first frame every document-level consumer draws after an install: the theme, the render cycle, every
    // camera program the document carries, and every bindable the manifest records read through Scalar and Color.
    private static void FirstFrame(WorldDefinition definition, WorldStateMirror mirror) {
        _ = new WorldThemeResolve().Resolve(
            definition: definition,
            mirror: mirror,
            revision: 1
        );
        _ = new WorldRenderCycleTrack().Resolve(
            definition: definition,
            mirror: mirror,
            revision: 1
        );

        var anchor = new SdfAnchor(
            Orientation: Quaternion.Identity,
            Position: Vector3.Zero
        );
        var clock = new SdfCameraClock(
            AuthoritativeTick: 0UL,
            PresentationSeconds: 0f
        );

        foreach (var camera in definition.Cameras) {
            _ = WorldCameraRigCompiler.Compile(
                definition: definition,
                mirror: mirror,
                program: camera.Rig
            ).Resolve(
                anchor: in anchor,
                clock: in clock
            );
        }

        foreach (ref readonly var entry in mirror.Manifest.Bindings) {
            var slot = mirror.SlotOf(
                binding: entry.Binding,
                conversion: entry.Conversion
            );

            Assert.True(condition: (slot >= 0));
            _ = mirror.TryValue(
                slot: slot,
                value: out _
            );
            _ = mirror.TryColor(
                slot: slot,
                value: out _
            );
        }
    }

    [Fact]
    public void EveryConsumersFirstFrameOverTheFlagshipReadsNoCellAndAddsNoSlot_AndALookupOfAnUnrecordedBindingRegistersNothing() {
        var definition = AuthoredGameFixtures.Nexus;
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        var slots = mirror.SlotCount;
        var reads = Reads(mirror: mirror);

        Assert.Equal(
            actual: slots,
            expected: mirror.Manifest.Bindings.Length
        );

        FirstFrame(
            definition: definition,
            mirror: mirror
        );
        Assert.Equal(
            actual: mirror.SlotOf(
                conversion: WorldStateConversion.Number,
                token: "state.nothingBindsThis"
            ),
            expected: -1
        );
        Assert.Equal(
            actual: (mirror.SlotCount, (Reads(mirror: mirror) - reads)),
            expected: (slots, 0L)
        );
    }
    [Fact]
    public void ASeatsReadsAreItsContextsWheelCellsAndBarCells_KeyedByItsBody() {
        var bindings = WorldPresentationManifest.SeatBindings(
            bar: SeatBar(),
            bindings: SeatDocument(),
            bodyIndex: 3,
            definition: SeatWorld(),
            hud: null
        );

        Assert.Equal(
            actual: bindings.Select(selector: static entry => entry.ToString()).Order(),
            expected: new[] {
                Number(row: "mode", target: true),
                Number(key: "3", row: "stance", target: true),
                Number(key: BindingWheelDefinition.HubLabelKey, row: "labels"),
                Number(key: "north", row: "labels"),
                Number(key: "north", row: "icons"),
                Number(key: "jump", row: "icons"),
                Number(key: "sprint", row: "icons"),
                Number(row: "layout"),
                Number(row: "model"),
            }.Select(selector: static entry => entry.ToString()).Order()
        );

        // Without a body a keyed family reads nothing, and a slot family still reads its row.
        var bodiless = WorldPresentationManifest.SeatBindings(
            bar: SeatBar(),
            bindings: SeatDocument(),
            bodyIndex: -1,
            definition: SeatWorld(),
            hud: null
        );

        Assert.DoesNotContain(
            collection: bodiless,
            filter: static entry => (entry.Binding.Row == "stance")
        );
        Assert.Contains(
            collection: bodiless,
            expected: Number(row: "mode", target: true)
        );
    }
    [Fact]
    public void ASeatRegistersItsReadsOnTheMirrorItsRouteReads_AndWithdrawsThemFromAMirrorItLeaves() {
        var definition = SeatWorld();
        var bindings = new WorldSeatBindings(definition: definition);
        var home = ClientFixtures.StateMirror(definition: definition);
        var away = ClientFixtures.StateMirror(definition: definition);
        var homeSlots = home.SlotCount;
        var mode = Number(row: "mode", target: true);
        var stance = Number(key: "0", row: "stance", target: true);

        bindings.SyncSeat(
            definition: definition,
            engineTick: 0UL,
            entityIndex: 0,
            nextInputTick: 1UL,
            slot: 0,
            state: home
        );

        Assert.True(condition: (home.SlotOf(binding: mode.Binding, conversion: mode.Conversion) >= 0));
        Assert.True(condition: (home.SlotOf(binding: stance.Binding, conversion: stance.Conversion) >= 0));

        // An unchanged route registers nothing again and reads nothing.
        var registered = home.SlotCount;
        var reads = Reads(mirror: home);

        bindings.SyncSeat(
            definition: definition,
            engineTick: 0UL,
            entityIndex: 0,
            nextInputTick: 1UL,
            slot: 0,
            state: home
        );
        Assert.Equal(
            actual: (home.SlotCount, (Reads(mirror: home) - reads)),
            expected: (registered, 0L)
        );

        // The seat crosses to another authority's mirror: its reads move there and retire at home.
        bindings.SyncSeat(
            definition: definition,
            engineTick: 0UL,
            entityIndex: 0,
            nextInputTick: 1UL,
            slot: 0,
            state: away
        );
        Assert.True(condition: (away.SlotOf(binding: stance.Binding, conversion: stance.Conversion) >= 0));
        Assert.Equal(
            actual: home.SlotOf(binding: stance.Binding, conversion: stance.Conversion),
            expected: -1
        );
        Assert.Equal(
            actual: home.SlotCount,
            expected: homeSlots
        );
    }
    [Fact]
    public void RegisteringAnOwnersSetReadsEachNewSlotOnce_ReRegisteringItAllocatesNothing_AndWithdrawingItRetiresIt() {
        var definition = Rows();
        var mirror = ClientFixtures.StateMirror(definition: definition);
        var baseline = (mirror.SlotCount, Reads(mirror: mirror));
        var owner = new object();
        var set = WorldPresentationManifest.SeatBindings(
            bar: SeatBar(),
            bindings: SeatDocument(),
            bodyIndex: 0,
            definition: definition,
            hud: null
        );

        mirror.Register(
            bindings: set,
            owner: owner
        );

        // The document registers none of the seat's reads, so each is a new slot read once.
        Assert.Equal(
            actual: (mirror.SlotCount, Reads(mirror: mirror)),
            expected: ((baseline.SlotCount + set.Length), (baseline.Item2 + set.Length))
        );
        Assert.Equal(
            actual: AllocationWindow.Least(window: () => {
                for (var repetition = 0; (repetition < Repetitions); repetition++) {
                    mirror.Register(
                        bindings: set,
                        owner: owner
                    );
                }
            }),
            expected: 0L
        );

        var generation = mirror.Generation;

        mirror.Unregister(owner: owner);
        Assert.Equal(
            actual: mirror.SlotCount,
            expected: baseline.SlotCount
        );
        Assert.NotEqual(
            actual: mirror.Generation,
            expected: generation
        );
    }
    [Fact]
    public void ADocumentSwapRetiresWhatOnlyTheOldManifestRegistered_KeepsWhatBothRegister_AndNeverAnswersWithARetiredSlot() {
        var withGauge = Rows() with {
            MarkersRaw = [Marker(alpha: "state.mode", id: "a"), Marker(alpha: "state.stance.0", id: "b"), Marker(alpha: "state.labels.north", id: "d")],
        };
        var withoutGauge = Rows() with {
            MarkersRaw = [Marker(alpha: "state.mode", id: "a"), Marker(alpha: "state.icons.jump", id: "c")],
        };
        var definition = withGauge;
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var holder = new WorldStateLease();

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        var kept = mirror.SlotOf(
            conversion: WorldStateConversion.Number,
            token: "state.mode"
        );
        var dropped = mirror.SlotOf(
            conversion: WorldStateConversion.Number,
            token: "state.stance.0"
        );

        Assert.True(condition: ((kept >= 0) && (dropped >= 0)));

        // A holder still reads the binding the next document drops.
        holder.Bind(
            bodyIndex: 0,
            mirror: mirror
        );
        Assert.Equal(
            actual: holder.Slot(
                reference: "state.stance.$body",
                truth: false
            ),
            expected: dropped
        );

        var generation = mirror.Generation;
        var reads = Reads(mirror: mirror);

        definition = withoutGauge;
        mirror.Install(
            engineTick: 0UL,
            tick: 1UL
        );

        // The kept binding keeps its index, the dropped one nothing holds retires, and the dropped one a holder reads
        // answers no lookup although its holder has not let it go; the install read every live slot once: the two
        // registered and the one held.
        Assert.Equal(
            actual: (
                mirror.SlotOf(conversion: WorldStateConversion.Number, token: "state.mode"),
                mirror.SlotOf(conversion: WorldStateConversion.Number, token: "state.stance.0"),
                mirror.SlotCount,
                (Reads(mirror: mirror) - reads)
            ),
            expected: (kept, -1, 3, 3L)
        );
        Assert.NotEqual(
            actual: mirror.Generation,
            expected: generation
        );
        Assert.True(condition: (mirror.SlotOf(conversion: WorldStateConversion.Number, token: "state.icons.jump") >= 0));

        // Once its holder lets go, the dropped slot retires.
        holder.Release();
        Assert.Equal(
            actual: mirror.SlotCount,
            expected: 2
        );

        // Swapping between the two documents from here on allocates nothing.
        var swaps = 0;

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => {
                for (var repetition = 0; (repetition < Repetitions); repetition++) {
                    definition = (((swaps++ % 2) == 0)
                        ? withGauge
                        : withoutGauge);
                    mirror.Install(
                        engineTick: 0UL,
                        tick: 2UL
                    );
                }
            }),
            expected: 0L
        );
    }

    private static WorldMarkerRow Marker(string id, string alpha) => (JsonSerializer.Deserialize<WorldMarkerRow>(
        json: $$"""
            { "id": "{{id}}", "source": { "$type": "point", "position": [0, 0, 0] }, "icon": "dot", "style": { "chipAlpha": "{{alpha}}", "size": 8 } }
            """,
        options: WorldJsonContext.Default.Options
    ) ?? throw new InvalidOperationException(message: "The marker parsed to null."));
}
