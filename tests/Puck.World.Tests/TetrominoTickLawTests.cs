using Puck.Commands;
using Puck.Abstractions.Counting;
using Puck.Maths;
using Puck.State.Rules;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: the tetromino world's work is one piece's life, one workflow step a tick, pinned by
/// deterministic counters. A falling piece's quiet tick fires nothing and its gravity tick fires only gravity; a hard
/// drop's life from the press to the next piece is five firings, never more than two in a tick; a four-row clear is one
/// firing of the clear step; neither a quiet tick nor a piece's life allocates past a bound; and the document's
/// worst-case tick is admitted under the rule work ceiling.</summary>
[Collection(AllocationCollection.Name)]
public sealed class TetrominoTickLawTests(ITestOutputHelper output) {
    private const string SourcePath = "src/Puck.World/Assets/worlds/games/tetromino.puck";

    private static readonly Lazy<WorldDefinition> Source = new(valueFactory: static () => AuthoredGameFixtures.Load(relativePath: SourcePath));

    private static WorldFixture Boot() {
        var fixture = Fixtures.FreshServer(definition: Source.Value);
        var seat = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(seat, seat.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        _ = fixture.StepUntil(
            ceiling: 8,
            settled: () => (Read(fixture: fixture, row: "stage") == 1L)
        );

        return fixture;
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
            holdSeconds: 0.1f,
            ordinal: ordinal,
            value: FixedQ4816.One
        );
    }
    private static void Set(WorldFixture fixture, string row, string key, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Kind: WorldDocumentWriteKind.Set,
        Key: key,
        Principal: Principal.Console,
        Row: row,
        Value: value
    ));
    private static long Read(WorldFixture fixture, string row, string? key = null) {
        var arena = fixture.Server.Arena;

        Assert.True(condition: arena.Catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: row), userMessage: row);

        return (arena.TryRead(rowOrdinal: handle.Ordinal, key: arena.Catalog.Keys.Intern(name: CellName.Parse(candidate: (key ?? WorldStateRow.SlotKey.Value))), value: out var value)
            ? value.AsInt
            : 0L
        );
    }
    private static long Count(IWorkCounterSource source, WorkKind kind) {
        Assert.True(condition: source.TryRead(kind: kind, value: out var value), userMessage: kind.Name);

        return value;
    }
    // Steps one tick and returns what it evaluated, fired and allocated on this thread.
    private static (long Evaluations, long Firings, long Bytes) Measured(WorldFixture fixture) {
        var work = fixture.Server.RuleHost.RuleWork;
        var evaluations = Count(kind: RuleWorkKinds.Evaluations, source: work);
        var firings = Count(kind: RuleWorkKinds.Firings, source: work);
        var bytes = AllocationWindow.Total(window: () => fixture.Step());

        return (
            (Count(kind: RuleWorkKinds.Evaluations, source: work) - evaluations),
            (Count(kind: RuleWorkKinds.Firings, source: work) - firings),
            bytes
        );
    }
    // Hard-drops the piece in play and steps until the next one is falling, one tick at a time.
    private static List<(long Evaluations, long Firings, long Bytes)> DropToNextPiece(WorldFixture fixture) {
        var spawned = Read(fixture: fixture, row: "spawned");
        var ticks = new List<(long Evaluations, long Firings, long Bytes)>();

        Press(channel: "drop", fixture: fixture);
        while (Read(fixture: fixture, row: "spawned") == spawned) {
            Assert.True(condition: (ticks.Count < 16), userMessage: "the next piece never appeared");
            ticks.Add(item: Measured(fixture: fixture));
        }

        return ticks;
    }

    // Level 1's gravity cycle reads zero once every thirty ticks, so sixty quiet ticks of a falling piece hold exactly
    // two gravity firings and nothing else. A hard drop fires itself, then one workflow step a tick: the fall step
    // (on the drop's own tick, when the drop has already reset the lock deadline to now), the lock, the clear and the
    // next spawn.
    [Fact]
    public void APieceFiresOneStepATickAndAQuietTickFiresNothing() {
        using var fixture = Boot();
        var quiet = new List<(long Evaluations, long Firings, long Bytes)>();

        for (var tick = 0; (tick < 60); tick++) {
            quiet.Add(item: Measured(fixture: fixture));
        }

        var drops = new List<List<(long Evaluations, long Firings, long Bytes)>>();

        for (var piece = 0; (piece < 6); piece++) {
            drops.Add(item: DropToNextPiece(fixture: fixture));
            fixture.Step();
        }

        output.WriteLine(message: $"tetromino: quiet tick {quiet.Min(selector: static tick => tick.Evaluations)}..{quiet.Max(selector: static tick => tick.Evaluations)} evaluations, {quiet.Sum(selector: static tick => tick.Firings)} firings in 60 ticks; a drop to the next piece {string.Join(separator: " | ", values: drops.Select(selector: static life => string.Join(separator: ",", values: life.Select(selector: static tick => $"{tick.Firings}f/{tick.Evaluations}e"))))}");
        Assert.Equal(expected: 2L, actual: quiet.Sum(selector: static tick => tick.Firings));
        Assert.All(collection: quiet, action: static tick => Assert.InRange(actual: tick.Firings, high: 1L, low: 0L));
        Assert.All(collection: drops, action: static life => {
            Assert.Equal(expected: 5L, actual: life.Sum(selector: static tick => tick.Firings));
            Assert.All(collection: life, action: static tick => Assert.InRange(actual: tick.Firings, high: 2L, low: 1L));
        });
        Assert.Equal(expected: 7L, actual: Read(fixture: fixture, row: "spawned"));
        Assert.Equal(expected: 6L, actual: Read(fixture: fixture, row: "locked"));
    }
    // The whole tick is measured, seat, physics and publication included, on this thread alone.
    [Fact]
    public void AQuietTickAndAPiecesLifeAllocateWithinBounds() {
        using var fixture = Boot();

        for (var tick = 0; (tick < 8); tick++) {
            fixture.Step();
        }

        var quiet = new long[29];

        for (var tick = 0; (tick < quiet.Length); tick++) {
            quiet[tick] = Measured(fixture: fixture).Bytes;
        }

        var lives = new long[6];

        for (var piece = 0; (piece < lives.Length); piece++) {
            lives[piece] = DropToNextPiece(fixture: fixture).Sum(selector: static tick => tick.Bytes);
            fixture.Step();
        }

        Array.Sort(array: quiet);
        Array.Sort(array: lives);
        output.WriteLine(message: $"tetromino: quiet median {quiet[(quiet.Length / 2)]:N0} bytes/tick; a piece's life median {lives[(lives.Length / 2)]:N0} bytes, widest {lives[^1]:N0}");
        Assert.True(condition: (quiet[(quiet.Length / 2)] < 1024L), userMessage: $"quiet median {quiet[(quiet.Length / 2)]:N0} bytes");
        Assert.True(condition: (lives[(lives.Length / 2)] < (64L * 1024L)), userMessage: $"life median {lives[(lives.Length / 2)]:N0} bytes");
    }
    // Columns 0 to 8 of the bottom four rows are full; an upright I dropped into column 9 fills all four, and the one
    // clear step that follows removes them in a single firing. The I comes out of the bag through the first hold.
    [Fact]
    public void AFourRowClearIsOneFiringOfTheClearStep() {
        using var fixture = Boot();

        foreach (var row in new[] { "20", "21", "22", "23" }) {
            Set(fixture: fixture, key: row, row: "well", value: 61439L);
        }
        Set(fixture: fixture, key: WorldStateRow.SlotKey.Value, row: "bag", value: 0L);
        fixture.Step();
        Press(channel: "hold", fixture: fixture);
        for (var tick = 0; (tick < 5); tick++) {
            fixture.Step();
        }
        Assert.Equal(expected: 0L, actual: Read(fixture: fixture, row: "kind"));
        Press(channel: "clockwise", fixture: fixture);
        for (var tick = 0; (tick < 5); tick++) {
            fixture.Step();
        }
        for (var move = 0; (move < 4); move++) {
            Press(channel: "right", fixture: fixture);
            for (var tick = 0; (tick < 5); tick++) {
                fixture.Step();
            }
        }

        var life = DropToNextPiece(fixture: fixture);
        var cleared = life.Count(predicate: static tick => (tick.Firings > 0));

        output.WriteLine(message: $"tetromino: a four-row clear {string.Join(separator: ",", values: life.Select(selector: static tick => $"{tick.Firings}f/{tick.Evaluations}e/{tick.Bytes}b"))}");
        Assert.Equal(expected: 4L, actual: Read(fixture: fixture, row: "lastClear"));
        Assert.Equal(expected: 5L, actual: life.Sum(selector: static tick => tick.Firings));
        Assert.Equal(actual: cleared, expected: 4);
        foreach (var row in new[] { "20", "21", "22", "23" }) {
            Assert.Equal(expected: 57351L, actual: Read(fixture: fixture, key: row, row: "well"));
        }
    }
    // Validation admitted the document, so its worst-case tick fits the ceiling; the costliest line is the clear step,
    // which walks every playing row for each of the four rows a clear can remove.
    [Fact]
    public void TheWorstCaseTickIsAdmittedUnderTheCeiling() {
        var definition = Source.Value;
        var price = WorldRuleWorkBudget.Measure(compilation: WorldRuleCompilation.Compile(definition: definition)).WorkUnitsPerTick;
        var lines = WorldRuleWorkBudget.Contributors(definition: definition).ToArray();
        var costliest = lines.Aggregate(func: static (best, line) => ((RuleWork.Compare(left: line.WorkUnits, right: best.WorkUnits) > 0) ? line : best));

        output.WriteLine(message: $"tetromino: {price} of {RuleCapacity.MaxWorkUnitsPerTick} work units a tick; costliest {costliest.Name} at {costliest.WorkUnits}");
        Assert.True(condition: price.Fits(ceiling: RuleCapacity.MaxWorkUnitsPerTick), userMessage: $"tetromino prices {price}");
        Assert.Equal(expected: "piece$clear", actual: costliest.Name);
    }
}
