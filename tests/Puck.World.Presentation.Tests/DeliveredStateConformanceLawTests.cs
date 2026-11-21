using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>THE LAW: a client reads state only from what it was delivered. Every state read an attached client makes
/// through its state mirror answers, from the cells its deliveries carried, what the authority's arena holds at the
/// client's delivered tick; a state-bound value in the client's definition answers the same; and nothing in the
/// client's definition leads back to the authority's store, neither a bound value's binder nor the arena a definition
/// is bound to.</summary>
/// <remarks>The rows cover every cell kind a document row holds (Int, Fixed, Bool, Text), a keyed row, a row whose
/// value advances with time, rule writes every tick and a console write mid-run, so the deliveries exercised are the
/// attach primer, the per-tick cell channel and a value install.</remarks>
public sealed class DeliveredStateConformanceLawTests {
    private const string SpotRow = "spot";

    private static readonly (string Row, string? Key)[] Cells = [
        ("tally", null),
        ("drift", null),
        ("lit", null),
        ("label", null),
        (SpotRow, null),
        ("scores", "a"),
        ("scores", "b"),
        ("scores", "c"),
    ];

    private static WorldDefinition Document() {
        var creation = CreationFixtures.Sphere(id: "orb", scale: 1f);

        return Fixtures.BuildDocument().WithWorldState(rows: [
            StateFixtures.IntSlot(name: "tally"),
            StateFixtures.Slot(advance: new StateAdvance(PerSecondNumerator: 3L, PerSecondDenominator: 2L), kind: CellKind.Fixed, name: "drift", value: CellValue.Fixed(rawBits: 0L)),
            StateFixtures.Slot(kind: CellKind.Bool, name: "lit", value: CellValue.Bool(value: true)),
            StateFixtures.TextSlot(name: "label", text: "first"),
            StateFixtures.TextSlot(name: SpotRow, text: "[1,2,3]"),
            new WorldStateRow(
                Name: CellName.Parse(candidate: "scores"),
                Kind: CellKind.Int,
                Cells: [StateFixtures.Cell(key: "a", value: 1L), StateFixtures.Cell(key: "b", value: 2L), StateFixtures.Cell(key: "c", value: 3L)]
            ),
        ]) with {
            CreationsRaw = [creation],
            PlacementsRaw = new WorldPlacementsSection(Rows: [
                new WorldPlacement(
                    Id: "orb",
                    PrototypeId: creation.Id,
                    Position: JsonSerializer.Deserialize<DocumentVector3>(json: $"\"state.{SpotRow}\"", options: DocumentJsonOptions.Shared)!,
                    YawDegrees: 0f,
                    Scale: 1f
                ),
            ]),
            Rules = [
                new WorldRule(Name: CellName.Parse(candidate: "count"), Mode: ActionTriggerMode.Level, Effects: [new ActionEffect.AddState(State: "tally", Value: 1m)]),
                new WorldRule(Name: CellName.Parse(candidate: "score"), Mode: ActionTriggerMode.Level, Effects: [new ActionEffect.AddState(State: "scores", Value: 2m, Key: StateChannelRef.OfNullable(spelling: "b"))]),
            ],
        };
    }

    // Every holder bound to a store in a definition, with the binder it names.
    private sealed class Binders : IWorldDocumentVisitor {
        public HashSet<object> Found { get; } = new(comparer: ReferenceEqualityComparer.Instance);

        public bool Enter(object value, WorldDocumentSite site) {
            if (value is not IDocumentStateValue holder) {
                return true;
            }
            if (holder.Binder is { } binder) {
                _ = Found.Add(item: binder);
            }

            return false;
        }
    }

    private static HashSet<object> BindersOf(WorldDefinition definition) {
        var binders = new Binders();

        new WorldDocumentWalker(isSurface: static type => typeof(IDocumentStateValue).IsAssignableFrom(c: type)).Walk(root: definition, visitor: binders);

        return binders.Found;
    }

    [Fact]
    public void EveryStateReadAClientMakesAnswersFromTheCellsItWasDelivered() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var server = fixture.Server;
        var client = ClientFixtures.Client(definition: server.Definition);

        using var lease = server.AttachSink(sink: client);

        var mirror = client.StateMirror;
        var slots = new List<(string Row, string? Key, bool Target, int Slot)>();

        foreach (var (row, key) in Cells) {
            foreach (var target in (bool[])[true, false]) {
                slots.Add(item: (row, key, target, mirror.Acquire(binding: new StateBinding(Row: row, Key: key, Target: target), conversion: WorldStateConversion.Number)));
            }
        }

        var compared = 0;

        for (var step = 0; (step < 12); step++) {
            if (step == 5) {
                server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                    Principal: Principal.Console,
                    Row: SpotRow,
                    Key: WorldStateRow.SlotKey.Value,
                    Value: 0,
                    Kind: WorldDocumentWriteKind.Set,
                    Text: "[4,5,6]"
                ));
            }

            fixture.Step();

            var tick = mirror.Tick;
            var engineTick = mirror.EngineTick;

            foreach (var (row, key, target, slot) in slots) {
                var authority = (target
                    ? WorldStateReader.TryReadValue(arena: server.Arena, definition: server.Definition, engineTick: engineTick, key: key, row: out _, rowName: row, tick: tick, value: out var live)
                    : WorldStateReader.TryReadEasedValue(arena: server.Arena, definition: server.Definition, engineTick: engineTick, key: key, row: out _, rowName: row, tick: tick, value: out live));

                Assert.True(condition: authority, userMessage: $"the authority declares no row {row}");
                Assert.True(
                    condition: Equals(objA: live, objB: mirror.Sample(slot: slot).Value),
                    userMessage: $"step {step}: state.{row}{((key is null) ? string.Empty : $".{key}")} (target {target}) reads {mirror.Sample(slot: slot).Value} on the client and {live} on the authority at tick {tick}"
                );
                compared++;
            }

            Assert.Equal(
                expected: server.Definition.PlacementFrames["orb"].Position,
                actual: client.Definition.PlacementFrames["orb"].Position
            );
        }

        Assert.Equal(expected: (12 * slots.Count), actual: compared);
        Assert.Equal(expected: new System.Numerics.Vector3(x: 4f, y: 5f, z: 6f), actual: client.Definition.PlacementFrames["orb"].Position);
        Assert.True(condition: (mirror.Sample(slot: slots[0].Slot).Value.AsInt > 0L), userMessage: "the rule never wrote the row, so the per-tick cell channel is untested");

        // Nothing the client holds leads back to the authority: no bound value names a binder the authority's own
        // definition binds through, and the client's definition is bound to no arena at all.
        var authorityBinders = BindersOf(definition: server.Definition);
        var clientBinders = BindersOf(definition: client.Definition);

        Assert.NotEmpty(collection: authorityBinders);
        Assert.NotEmpty(collection: clientBinders);
        Assert.DoesNotContain(collection: clientBinders, filter: authorityBinders.Contains);
        Assert.False(condition: WorldStateReader.TryBoundArena(arena: out _, definition: client.Definition));
    }
    /// <summary>The same law over the debug world: every cell of every document row, read stored and eased, after the
    /// attach primer and after each of several ticks.</summary>
    [Fact]
    public void EveryStateReadOfTheDebugWorldAnswersFromTheCellsItWasDelivered() {
        using var fixture = Fixtures.FreshServer(definition: AuthoredGameFixtures.Load(relativePath: "worlds/debug/debug.puck"), documentPath: RepositoryPaths.Resolve(relativePath: "worlds/debug/debug.puck"));
        var server = fixture.Server;
        var client = ClientFixtures.Client(definition: server.Definition);

        using var lease = server.AttachSink(sink: client);

        var mirror = client.StateMirror;
        var slots = new List<(string Row, string? Key, bool Target, int Slot)>();
        var arena = server.Arena;

        foreach (var row in server.Definition.State) {
            if (!arena.Catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: row.Name.Value)) {
                continue;
            }

            var cursor = 0;

            while (arena.TryNextCell(cursor: ref cursor, key: out var cell, rowOrdinal: handle.Ordinal)) {
                var key = arena.Keys[cell].Value;

                foreach (var target in (bool[])[true, false]) {
                    slots.Add(item: (row.Name.Value, key, target, mirror.Acquire(binding: new StateBinding(Row: row.Name.Value, Key: key, Target: target), conversion: WorldStateConversion.Number)));
                }
            }
        }

        Assert.NotEmpty(collection: slots);

        for (var step = 0; (step < 4); step++) {
            fixture.Step();

            var tick = mirror.Tick;
            var engineTick = mirror.EngineTick;

            foreach (var (row, key, target, slot) in slots) {
                _ = (target
                    ? WorldStateReader.TryReadValue(arena: server.Arena, definition: server.Definition, engineTick: engineTick, key: key, row: out _, rowName: row, tick: tick, value: out var live)
                    : WorldStateReader.TryReadEasedValue(arena: server.Arena, definition: server.Definition, engineTick: engineTick, key: key, row: out _, rowName: row, tick: tick, value: out live));

                Assert.True(
                    condition: Equals(objA: live, objB: mirror.Sample(slot: slot).Value),
                    userMessage: $"step {step}: state.{row}.{key} (target {target}) reads {mirror.Sample(slot: slot).Value} on the client and {live} on the authority at tick {tick}"
                );
            }
        }

        Assert.DoesNotContain(collection: BindersOf(definition: client.Definition), filter: BindersOf(definition: server.Definition).Contains);
        Assert.False(condition: WorldStateReader.TryBoundArena(arena: out _, definition: client.Definition));
    }
}
