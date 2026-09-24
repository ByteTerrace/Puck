using Puck.Commands;
using Puck.Abstractions.Counting;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: a rulepush turn in the package's Hedges level is one working pass of its fixpoint group
/// and one pass that confirms nothing moved; an idle world retains no turn and allocates nothing per tick once warm;
/// and the work a turn does is bounded by the board and the tokens on it, which a larger board would grow and nothing
/// else would, so a 16x16 level of 58 tokens is admitted under the per-tick ceiling.</summary>
[Collection(AllocationCollection.Name)]
public sealed class RulePushTurnLawTests(ITestOutputHelper output) {
    // The package's composition source declares every world it ships; each is staged as its own document once per
    // suite, the way the game stages a composition before it boots, so Hedges proves its borders against the rest.
    private static readonly Lazy<string> Staged = new(valueFactory: static () => {
        var source = Path.Combine(
            path1: AuthoredGameFixtures.Root,
            path2: "worlds/rulepush/rulepush.puck"
        );
        var compilation = Puck.World.Transpiler.WorldCompiler.CompileFile(
            allowMultiple: true,
            path: source
        );

        Assert.True(condition: compilation.Success);

        var directory = Directory.CreateTempSubdirectory(prefix: "puck-rulepush-").FullName;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => {
            try {
                Directory.Delete(
                    path: directory,
                    recursive: true
                );
            } catch (IOException) {
                // Best-effort scratch cleanup; a locked handle on a slow CI disk must never fail the run.
            } catch (UnauthorizedAccessException) {
            }
        };

        foreach (var world in compilation.Worlds) {
            Assert.True(
                condition: Puck.World.Transpiler.Composition.WorldStaging.TryWrite(
                    directory: directory,
                    name: world.Name,
                    path: out _,
                    reason: out var reason,
                    sourceDirectory: Path.GetDirectoryName(path: source)!,
                    world: ((System.Text.Json.Nodes.JsonObject)world.Json.DeepClone())
                ),
                userMessage: reason
            );
        }

        return directory;
    });

    private static WorldDefinition Hedges() {
        var path = Path.Combine(path1: Staged.Value, path2: WorldDocumentName.DocumentFile(name: "hedges"));

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path,
                out var definition,
                out var reason,
                neighbours: new WorldFileNeighbourResolver(baseDirectory: () => Path.GetDirectoryName(path: path)!),
                catalog: TestHookInstaller.CreateMachineCatalog()
            ),
            userMessage: $"{path}: {reason}"
        );

        return definition!;
    }
    private static void Press(WorldFixture fixture, string channel) {
        var channels = fixture.Server.Definition.Channels;
        var ordinal = -1;

        for (var index = 0; (index < channels.Count); index++) {
            if (string.Equals(a: channels[index].Name, b: channel, comparisonType: StringComparison.Ordinal)) {
                ordinal = index;
            }
        }
        Assert.True(condition: (ordinal >= 0), userMessage: $"'{channel}' names no declared channel");
        fixture.Server.Body(index: 0)!.PressChannel(
            authoredMaximum: FixedQ4816.FromDouble(value: 60d),
            holdSeconds: 0.05f,
            ordinal: ordinal,
            value: FixedQ4816.One
        );
    }
    // A 16x16 level of 58 tokens stamped from the package's own level module; a test world, never shipped.
    private static WorldDefinition Sixteen() => AuthoredGameFixtures.Load(relativePath: "tests/Puck.World.Tests/Fixtures/rulepush-sixteen.puck");
    private static WorldFixture Boot() => Boot(definition: Hedges());
    private static WorldFixture Boot(WorldDefinition definition) {
        var fixture = Fixtures.FreshServer(definition: definition);
        var seat = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        return fixture;
    }
    private static int Retained(WorldFixture fixture) => fixture.Server.Arena.ExportUndoSnapshot().Groups.Single().Segments.Count;
    private static long Slot(WorldFixture fixture, string row) {
        var arena = fixture.Server.Arena;

        Assert.True(condition: arena.Catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: row));
        Assert.True(condition: arena.TryRead(rowOrdinal: handle.Ordinal, key: arena.Catalog.Keys.Intern(name: StateRow.SlotKey), value: out var value));

        return value.AsInt;
    }

    [Fact]
    public void ATurnIsOneWorkingPassAndOneConfirmingPass() {
        using var fixture = Boot();

        for (var tick = 0; (tick < 8); tick++) {
            fixture.Step();
        }

        var retained = Retained(fixture: fixture);
        var turn = fixture.Server.RuleHost.GroupState;

        Assert.False(condition: turn.Progress(name: "turn").Running);
        Press(channel: "west", fixture: fixture);

        var ticks = 0;

        while (Slot(fixture: fixture, row: "moves") == 0L) {
            Assert.True(condition: (++ticks < 8), userMessage: "the press never moved the imp");
            fixture.Step();
        }

        // The pass that moved the imp read the board, rewrote nothing and settled it; the group stays open for one
        // more pass, which changes nothing and closes the turn.
        Assert.Equal(expected: 1, actual: turn.Progress(name: "turn").Step);
        Assert.Equal(expected: 0L, actual: Slot(fixture: fixture, row: "dirty"));
        Assert.Equal(expected: retained, actual: Retained(fixture: fixture));

        fixture.Step();
        Assert.False(condition: turn.Progress(name: "turn").Running);
        Assert.Equal(expected: (retained + 1), actual: Retained(fixture: fixture));

        for (var tick = 0; (tick < 16); tick++) {
            fixture.Step();
        }
        Assert.Equal(expected: (retained + 1), actual: Retained(fixture: fixture));
        Assert.Equal(expected: 1L, actual: Slot(fixture: fixture, row: "moves"));
    }
    // The whole tick is measured, seat, physics and publication included, on this thread alone. An idle tick
    // evaluates every member's gate and fires none, so it allocates what any quiet world tick does; a turn's working
    // tick adds only what the pass itself rents and commits.
    [Fact]
    public void ATurnAllocatesAQuietTickBoundAndAnIdleTickNothingOfItsOwn() {
        using var fixture = Boot();

        for (var tick = 0; (tick < 64); tick++) {
            fixture.Step();
        }

        var idle = new long[64];

        for (var tick = 0; (tick < idle.Length); tick++) {
            idle[tick] = AllocationWindow.Total(window: () => fixture.Step());
        }

        var turns = new long[16];

        for (var turn = 0; (turn < turns.Length); turn++) {
            Press(channel: (((turn % 2) == 0) ? "west" : "east"), fixture: fixture);
            var moves = Slot(fixture: fixture, row: "moves");

            turns[turn] = AllocationWindow.Total(window: () => {
                while (Slot(fixture: fixture, row: "moves") == moves) {
                    fixture.Step();
                }
                fixture.Step();
            });
            for (var tick = 0; (tick < 4); tick++) {
                fixture.Step();
            }
        }

        Array.Sort(array: idle);
        Array.Sort(array: turns);
        output.WriteLine(message: $"rulepush hedges: idle median {idle[(idle.Length / 2)]:N0} bytes/tick, turn median {turns[(turns.Length / 2)]:N0} bytes/turn, widest turn {turns[^1]:N0}");
        Assert.Equal(expected: 16L, actual: Slot(fixture: fixture, row: "moves"));
        Assert.True(condition: (idle[(idle.Length / 2)] < 1024L), userMessage: $"idle median {idle[(idle.Length / 2)]:N0} bytes");
        Assert.True(condition: (turns[(turns.Length / 2)] < (64L * 1024L)), userMessage: $"turn median {turns[(turns.Length / 2)]:N0} bytes");
    }
    // Admission and every reader of a document's work run one computation: the sheet validation admitted, a fresh
    // compilation's and Measure's are the same, rewind included, and the rewind carries a number.
    [Fact]
    public void TheMeasuredSheetIsTheSheetAdmissionHeld() {
        var hedges = Hedges();

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(compilation: out var admitted, definition: hedges, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: admitted);
        Assert.Equal(expected: admitted.WorkBudget, actual: WorldRuleWorkBudget.Measure(definition: hedges));
        Assert.Equal(expected: admitted.WorkBudget, actual: WorldRuleWorkBudget.Measure(compilation: WorldRuleCompilation.Compile(definition: hedges)));

        var undo = Assert.Single(collection: WorldRuleWorkBudget.Contributors(definition: hedges), predicate: static line => (line.Name == "undo"));

        Assert.True(condition: undo.WorkUnits.IsKnown, userMessage: $"undo prices {undo.WorkUnits}");
    }
    // The load itself is the admission: validation refuses a document whose worst-case tick exceeds the ceiling.
    // Hedges is 9x7 and the 16x16 level carries 58 tokens; the larger one prices higher for its board and tokens
    // alone, and plays a turn.
    [Fact]
    public void ASixteenBySixteenLevelIsAdmittedUnderTheTickCeilingAndPlays() {
        var hedges = WorldRuleWorkBudget.Measure(compilation: WorldRuleCompilation.Compile(definition: Hedges())).WorkUnitsPerTick;
        var sixteen = Sixteen();
        var price = WorldRuleWorkBudget.Measure(compilation: WorldRuleCompilation.Compile(definition: sixteen)).WorkUnitsPerTick;

        output.WriteLine(message: $"rulepush hedges (9x7): {hedges} work units per tick; rulepush 16x16: {price} of {RuleCapacity.MaxWorkUnitsPerTick}");
        Assert.True(condition: price.Fits(ceiling: RuleCapacity.MaxWorkUnitsPerTick), userMessage: $"16x16 prices {price}");
        Assert.True(condition: (RuleWork.Compare(left: price, right: hedges) > 0), userMessage: $"16x16 {price} against hedges {hedges}");

        using var fixture = Boot(definition: sixteen);

        Press(channel: "east", fixture: fixture);

        for (var tick = 0; (Slot(fixture: fixture, row: "moves") == 0L); tick++) {
            Assert.True(condition: (tick < 8), userMessage: "the press never moved the imp");
            fixture.Step();
        }
        Assert.Equal(expected: 0L, actual: Slot(fixture: fixture, row: "bumps"));
    }
}
