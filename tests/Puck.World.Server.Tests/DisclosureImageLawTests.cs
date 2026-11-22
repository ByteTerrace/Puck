using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The row images a stream is disclosed: what a steady tick costs, that an image is shared only between
/// audiences disclosed the same cells, that its identity moves exactly when its content does, and that a cell policy
/// equal to one the store already holds still re-deals.</summary>
public sealed class DisclosureImageLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static string Token(int seat) => Principal.Seat(slot: seat).Describe();
    private static StateVisibility Readers(params int[] seats) => new(Readers: [.. seats.Select(selector: Token)]);

    // A score every recipient reads whole, and a hand whose cells differ in who reads them: `open` everyone, `ours`
    // seats 0 and 1, `mine` seat 0 alone. A reader the hand's policy admits is told how many cells it may not read.
    private static WorldDefinition Table(HiddenCells hidden = HiddenCells.Count) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [
            new WorldStateRow(Name: Name(value: "score"), Kind: CellKind.Int, Capacity: 4,
                Cells: [StateFixtures.Cell(key: "home", value: 1), StateFixtures.Cell(key: "away", value: 2)]),
            new WorldStateRow(Name: Name(value: "hand"), Kind: CellKind.Int, Capacity: 8, CellVisibility: true, Visibility: new StateVisibility(Hidden: hidden),
                Cells: [
                    StateFixtures.Cell(key: "open", value: 1) with { Observation = new StateObservation(Tick: 0L, Visible: true) },
                    StateFixtures.Cell(key: "ours", value: 2) with { Visibility = Readers(0, 1), Advance = new StateAdvance(PerSecondNumerator: 1L, PerSecondDenominator: 1L) },
                    StateFixtures.Cell(key: "mine", value: 3) with { Visibility = Readers(0) },
                ]),
        ]),
        Rules = [],
    };
    private static WorldRecipientView View(WorldDefinition definition, StateArena arena, WorldDisclosureImages images, int seat,
        WorldDisclosureTier tier = WorldDisclosureTier.Replica) => new(() => definition, () => arena, Principal.Seat(slot: seat), tier,
        static () => true, images, "test", static () => ArenaTime.Origin);
    private static int Row(StateArena arena, string name) {
        Assert.True(condition: arena.Catalog.TryResolve(lane: StateLane.Document, name: name, handle: out var handle));
        return handle.Ordinal;
    }
    private static CellKey Key(StateArena arena, string name) {
        Assert.True(condition: arena.Keys.TryResolve(name: Name(value: name), key: out var key));
        return key;
    }
    private static void Commit(StateArena arena, Action write) {
        var mark = arena.BeginScope();

        write();
        arena.Commit(mark: mark);
    }
    private static long Read(WorldDisclosureImages images, WorkKind kind) {
        Assert.True(condition: images.Work.TryRead(kind: kind, value: out var value));
        return value;
    }
    // Everything a recipient is handed of a row, spelled by the document's own serializer: the reference an image's
    // digest is checked against.
    private static string Content(WorldRecipientImage image) => JsonSerializer.Serialize(
        value: new WorldStateRow(Name: Name(value: "row"), Kind: CellKind.Int, Capacity: 64, Cells: image.Cells),
        jsonTypeInfo: WorldJsonContext.Default.WorldStateRow) + "|" +
        ((image.Observation is { } observation) ? JsonSerializer.Serialize(value: observation, jsonTypeInfo: WorldJsonContext.Default.For<WorldObservedRow>()) : "-");

    // A tick that writes a row its stream reads whole and a cell its stream may not read moves the stream's document
    // identity and builds nothing: no image, no export, no pin. The row a stream reads whole is digested once from the
    // store, and the hidden write is proven to leave the stream's reading of the hand as it was.
    [Fact]
    public void ASteadyTickAdvancesAStreamWithoutAllocating() {
        var definition = Table();
        var arena = Fixtures.Store(definition: definition);
        var images = new WorldDisclosureImages();
        var view = View(definition: definition, arena: arena, images: images, seat: 2);
        var cursor = new WorldDeliveryCursor();
        var (score, hand) = (Row(arena: arena, name: "score"), Row(arena: arena, name: "hand"));
        var (home, mine) = (Key(arena: arena, name: "home"), Key(arena: arena, name: "mine"));
        var changes = new ArenaChangedKeys();
        var flags = new bool[arena.Rows.Count];
        var rows = new int[arena.Rows.Count];
        var value = 10L;
        var replacements = 0;
        var scenes = new HashSet<Puck.Assets.ContentPin>();

        Assert.NotNull(@object: cursor.DeliverDefinition(view: view));
        Assert.Single(collection: view.Advance(cursor: cursor, rows: []));
        void Tick() {
            changes.Clear();
            images.BeginTick(changes: changes);
            value++;
            var mark = arena.BeginScope();

            if (!arena.TryWrite(rowOrdinal: score, key: home, value: CellValue.Int(value: value), reason: out _) ||
                !arena.TryWrite(rowOrdinal: hand, key: mine, value: CellValue.Int(value: value), reason: out _)) { throw new InvalidOperationException(message: "The write was refused."); }
            arena.Commit(mark: mark);
            changes.Include(other: arena.ChangedKeys);
            Array.Clear(array: flags);
            arena.CopyChangedRows(rows: flags, clear: true);
            var count = 0;

            for (var row = 0; row < flags.Length; row++) { if (flags[row]) { rows[count++] = row; } }
            replacements += view.Advance(cursor: cursor, rows: rows.AsSpan(start: 0, length: count)).Count;
            cursor.DeliveredRows(view: view);
            if (cursor.FrameMoved(view: view) || !cursor.Holds(view: view)) { throw new InvalidOperationException(message: "The stream fell out of step."); }
            scenes.Add(item: view.SceneIdentity!.Value);
        }

        for (var warm = 0; warm < 8; warm++) { Tick(); }
        var compositions = Read(images: images, kind: WorldDisclosureImages.RowCompositions);
        var skips = Read(images: images, kind: WorldDisclosureImages.RowSkips);
        var built = Read(images: images, kind: WorldDisclosureImages.RowImages);
        var ticks = value;

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Tick));
        ticks = (value - ticks);
        Assert.Equal(expected: 0, actual: replacements);
        Assert.Single(collection: scenes);
        // Each tick digested the score once, proved the hand's hidden write irrelevant, and built no image.
        Assert.Equal(expected: (compositions + ticks), actual: Read(images: images, kind: WorldDisclosureImages.RowCompositions));
        Assert.Equal(expected: (skips + ticks), actual: Read(images: images, kind: WorldDisclosureImages.RowSkips));
        Assert.Equal(expected: built, actual: Read(images: images, kind: WorldDisclosureImages.RowImages));
        // The stream still follows what it may read: a write to `open` is delivered, with the count of what it may not.
        Commit(arena: arena, write: () => Assert.True(condition: arena.TryWrite(rowOrdinal: hand, key: Key(arena: arena, name: "open"), value: CellValue.Int(value: 77L), reason: out _)));
        var replacement = Assert.Single(collection: view.Advance(cursor: cursor, rows: [hand]));

        Assert.Equal(expected: 77L, actual: Assert.Single(collection: replacement.Image.Cells).Value.Raw);
        Assert.Equal(expected: 2, actual: replacement.Image.Observation!.HiddenCount);
    }

    // Seats 1 and 2 differ in one cell of the hand: seat 1 reads `ours` and seat 2 does not. Each is handed its own
    // image and its own document identity. Seats 2 and 3 are disclosed the same cells, and share one image though no
    // policy names either: the image is found by what an audience is disclosed, never by who it is.
    [Theory]
    [InlineData(HiddenCells.Omit)]
    [InlineData(HiddenCells.Count)]
    [InlineData(HiddenCells.Placeholder)]
    public void AudiencesThatDifferInOneHiddenCellAreHandedDifferentImagesAndIdentities(HiddenCells hidden) {
        var definition = Table(hidden: hidden);
        var arena = Fixtures.Store(definition: definition);
        var images = new WorldDisclosureImages();
        var hand = Row(arena: arena, name: "hand");
        var views = Enumerable.Range(start: 0, count: 4).Select(selector: seat => View(definition: definition, arena: arena, images: images, seat: seat)).ToArray();
        var held = views.Select(selector: view => view.Image(row: hand)).ToArray();
        string Keys(WorldRecipientImage image) => string.Join(separator: ",", values: image.Cells.Select(selector: cell => cell.Key.Value));

        Assert.Equal(expected: "open,ours,mine", actual: Keys(image: held[0]));
        Assert.Equal(expected: "open,ours", actual: Keys(image: held[1]));
        Assert.Equal(expected: "open", actual: Keys(image: held[2]));
        Assert.NotSame(expected: held[1], actual: held[2]);
        Assert.NotEqual(expected: held[1].Identity, actual: held[2].Identity);
        Assert.NotEqual(expected: held[0].Identity, actual: held[1].Identity);
        Assert.Same(expected: held[2], actual: held[3]);
        // No image carries a cell its audience may not read, in its replica cells or in its observation.
        for (var seat = 1; seat < 4; seat++) {
            Assert.DoesNotContain(expectedSubstring: "\"mine\"", actualString: Content(image: held[seat]), comparisonType: StringComparison.Ordinal);
            Assert.Equal(expected: (hidden == HiddenCells.Omit) ? 0 : (seat == 1 ? 1 : 2), actual: held[seat].Observation!.HiddenCount);
        }
        Assert.DoesNotContain(expectedSubstring: "\"ours\"", actualString: Content(image: held[2]), comparisonType: StringComparison.Ordinal);
        // A stream seeded for one audience does not hold another's document.
        var cursors = views.Select(selector: _ => new WorldDeliveryCursor()).ToArray();
        var documents = views.Select(selector: (view, seat) => cursors[seat].DeliverDefinition(view: view)!).ToArray();

        Assert.True(condition: cursors[1].Holds(view: views[1]));
        Assert.False(condition: cursors[1].Holds(view: views[2]));
        Assert.False(condition: cursors[2].Holds(view: views[1]));
        Assert.True(condition: cursors[2].Holds(view: views[3]));
        Assert.Equal(expected: 2, actual: WorldDefinitionRows.FindStateRow(documents[1].State, "hand")!.Cells!.Count);
        Assert.Single(collection: WorldDefinitionRows.FindStateRow(documents[2].State, "hand")!.Cells!);
        // A write to the cell that tells them apart moves seat 1's image and leaves seat 2's as it stood.
        Commit(arena: arena, write: () => Assert.True(condition: arena.TryWrite(rowOrdinal: hand, key: Key(arena: arena, name: "ours"), value: CellValue.Int(value: 9L), reason: out _)));
        Assert.NotEqual(expected: held[1].Identity, actual: views[1].Image(row: hand).Identity);
        Assert.Same(expected: held[2], actual: views[2].Image(row: hand));
        Assert.False(condition: cursors[1].Holds(view: views[1]));
        Assert.True(condition: cursors[2].Holds(view: views[2]));
    }

    // An image's identity is the digest of the bytes one writer reads from the store. Over a run of writes of every
    // kind the hand can take (a value, a cell's audience, a stamp, a behaviour, a clock, a new cell, a removed cell),
    // two readings of the hand share an identity exactly when a recipient is handed the same cells and observation:
    // nothing an image carries is missing from its identity, and nothing hidden from an audience moves it.
    [Theory]
    [InlineData(HiddenCells.Omit)]
    [InlineData(HiddenCells.Count)]
    [InlineData(HiddenCells.Placeholder)]
    public void AnImagesIdentityMovesExactlyWhenWhatItsAudienceIsHandedDoes(HiddenCells hidden) {
        var definition = Table(hidden: hidden);
        var arena = Fixtures.Store(definition: definition);
        var images = new WorldDisclosureImages();
        var hand = Row(arena: arena, name: "hand");
        var views = Enumerable.Range(start: 0, count: 4).Select(selector: seat => View(definition: definition, arena: arena, images: images, seat: seat)).ToArray();
        var readings = new List<(string Content, Puck.Assets.ContentPin Identity)>();
        void Sample() {
            foreach (var view in views) {
                var image = view.Image(row: hand);

                readings.Add(item: (Content(image: image), image.Identity));
            }
        }
        var (open, ours, mine) = (Key(arena: arena, name: "open"), Key(arena: arena, name: "ours"), Key(arena: arena, name: "mine"));
        Action[] writes = [
            () => Assert.True(condition: arena.TryWrite(rowOrdinal: hand, key: open, value: CellValue.Int(value: 5L), reason: out _)),
            () => Assert.True(condition: arena.TryWrite(rowOrdinal: hand, key: mine, value: CellValue.Int(value: 6L), reason: out _)),
            () => Assert.True(condition: arena.TryWrite(rowOrdinal: hand, key: ours, value: CellValue.Int(value: 7L), reason: out _)),
            () => Assert.True(condition: arena.TryWriteVisibility(rowOrdinal: hand, key: open, visibility: arena.Visibility(rowOrdinal: hand, key: mine))),
            () => Assert.True(condition: arena.TryWriteVisibility(rowOrdinal: hand, key: open, visibility: null)),
            () => Assert.True(condition: arena.TryWriteVisibility(rowOrdinal: hand, key: mine, visibility: Readers(0, 2))),
            () => Assert.True(condition: arena.TryWriteObservation(rowOrdinal: hand, key: open, observation: new StateObservation(Tick: 4L, Visible: true))),
            () => Assert.True(condition: arena.TryWriteObservation(rowOrdinal: hand, key: mine, observation: new StateObservation(Tick: 9L, Visible: false))),
            () => Assert.True(condition: arena.TryWriteBehavior(rowOrdinal: hand, key: ours, behavior: StateCellBehavior.None)),
            () => Assert.True(condition: arena.TryWriteClock(rowOrdinal: hand, key: ours, epochTick: 3L, epochEngineTick: 5L, y0: 7L, v0: 11L, substepTicks: 0L, reason: out _)),
            () => Assert.True(condition: arena.TryMint(rowOrdinal: hand, name: Name(value: "late"), value: CellValue.Int(value: 8L), key: out _, reason: out _)),
            () => Assert.True(condition: arena.TryWriteVisibility(rowOrdinal: hand, key: Key(arena: arena, name: "late"), visibility: Readers(0))),
            () => Assert.True(condition: arena.TryRemove(rowOrdinal: hand, key: ours, reason: out _)),
            () => Assert.True(condition: arena.TryRemove(rowOrdinal: hand, key: Key(arena: arena, name: "late"), reason: out _)),
            () => Assert.True(condition: arena.TryWrite(rowOrdinal: hand, key: open, value: CellValue.Int(value: 5L), reason: out _)),
        ];

        Sample();
        foreach (var write in writes) {
            Commit(arena: arena, write: write);
            Sample();
        }
        Assert.True(condition: readings.Select(selector: reading => reading.Content).Distinct().Count() > 8);
        for (var left = 0; left < readings.Count; left++) {
            for (var right = (left + 1); right < readings.Count; right++) {
                Assert.True(
                    condition: ((readings[left].Content == readings[right].Content) == (readings[left].Identity == readings[right].Identity)),
                    userMessage: $"Readings {left} and {right} disagree: content {(readings[left].Content == readings[right].Content ? "equal" : "differs")}, identity {(readings[left].Identity == readings[right].Identity ? "equal" : "differs")}.\n{readings[left].Content}\n{readings[right].Content}"
                );
            }
        }
        // A table that has composed nothing reads the same identities from the same store: an identity is content.
        var fresh = new WorldDisclosureImages();

        for (var seat = 0; seat < views.Length; seat++) {
            Assert.Equal(expected: views[seat].Image(row: hand).Identity, actual: View(definition: definition, arena: arena, images: fresh, seat: seat).Image(row: hand).Identity);
        }
    }

    // A hand dealt into a rack: seat 1 is shown the king and the queen and not the ace, which only seat 0 reads. The
    // queen then takes the very policy object the ace holds, so the set of policies the store holds does not change and
    // the table's policy index is not rebuilt. The stream still loses the queen: its frame moves, its document is dealt
    // again without her, and it cuts.
    [Fact]
    public void ACellPolicyEqualToOneTheStoreAlreadyHoldsStillReDealsAndCuts() {
        static StateCell Card(string key, bool hidden) => StateFixtures.Cell(key: key, kind: CellKind.Bool) with { Visibility = (hidden ? Readers(0) : null) };
        var template = new WorldPlacement(
            Id: "rack", PrototypeId: "back", Position: new System.Numerics.Vector3(x: 0f, y: 1f, z: 0f), YawDegrees: 0f, Scale: 1f,
            Distribution: new WorldDistribution(
                Region: new WorldDistributionRegion.Lattice(StepA: new System.Numerics.Vector3(x: 0.25f, y: 0f, z: 0f), CountA: 4, StepB: new System.Numerics.Vector3(x: 0f, y: 0f, z: 1f), CountB: 1),
                Fill: new WorldSequence(Name: WorldSequence.None, Offset: 0, Step: 0f)),
            Deal: new WorldPlacementDeal(Row: "hand", Preserve: new WorldPlacementDealPreserve(Prototype: true)));
        WorldPlacement Child(string key, int slot) => WorldPlacementDeal.Child(id: WorldPlacementDeal.ChildId(template: "rack", key: key), prototype: key, slot: slot, template: template);
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [
                new(Name(value: "hand"), CellKind.Bool, Cells: [Card(key: "ace", hidden: true), Card(key: "king", hidden: false), Card(key: "queen", hidden: false)],
                    Capacity: 4, Visibility: new(Hidden: HiddenCells.Omit)),
            ]),
            PlacementRowsRaw = [template, Child(key: "ace", slot: 0), Child(key: "king", slot: 1), Child(key: "queen", slot: 2)],
            Rules = [],
        };
        var arena = Fixtures.Store(definition: definition);
        var images = new WorldDisclosureImages();
        var view = View(definition: definition, arena: arena, images: images, seat: 1);
        var cursor = new WorldDeliveryCursor();
        var hand = Row(arena: arena, name: "hand");
        static string Ids(WorldDefinition document) => string.Join(separator: ",", values: document.Placements.Select(selector: placement => placement.Id));

        Assert.Equal(expected: "rack,rack/king,rack/queen", actual: Ids(document: cursor.DeliverDefinition(view: view)!));
        Assert.Single(collection: view.Advance(cursor: cursor, rows: []));
        Assert.False(condition: cursor.FrameMoved(view: view));
        var rebuilds = Read(images: images, kind: WorldDisclosureImages.IndexRebuilds);
        var generation = cursor.Generation;
        var held = arena.Visibility(rowOrdinal: hand, key: Key(arena: arena, name: "ace"));

        Assert.NotNull(@object: held);
        Commit(arena: arena, write: () => Assert.True(condition: arena.TryWriteVisibility(rowOrdinal: hand, key: Key(arena: arena, name: "queen"), visibility: held)));

        Assert.True(condition: cursor.FrameMoved(view: view));
        Assert.Equal(expected: rebuilds, actual: Read(images: images, kind: WorldDisclosureImages.IndexRebuilds));
        Assert.Equal(expected: "rack,rack/king", actual: string.Join(separator: ",", values: view.Placements.Select(selector: placement => placement.Id)));
        Assert.Equal(expected: "rack,rack/king", actual: Ids(document: view.Definition));
        var replacement = Assert.Single(collection: view.Advance(cursor: cursor, rows: [hand]));

        Assert.Equal(expected: "king", actual: Assert.Single(collection: replacement.Image.Cells).Key.Value);
        Assert.Equal(expected: (generation + 1L), actual: cursor.Generation);
        Assert.True(condition: cursor.TakeCut());
        cursor.ForgetDocument();
        Assert.Equal(expected: "rack,rack/king", actual: Ids(document: cursor.DeliverDefinition(view: view)!));
    }
}
