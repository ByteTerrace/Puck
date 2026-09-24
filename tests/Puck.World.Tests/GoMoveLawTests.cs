using Puck.Commands;
using Puck.Abstractions.Counting;
using Puck.State.Rules;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: the Go world's work is bounded by its board and pinned by deterministic counters. A
/// move is one firing of its colour's door and one of its report, a quiet tick fires nothing, and neither allocates
/// past a bound; the area count at the end of a game settles in a bounded number of flood passes; and the CPU's search
/// lands one move whose judged candidates, grown nodes and playout plies are the same on every run, with no tick
/// spending past the search's allowance.</summary>
[Collection(AllocationCollection.Name)]
public sealed class GoMoveLawTests(ITestOutputHelper output) {
    private static readonly Lazy<WorldDefinition> Source = new(valueFactory: static () => AuthoredGameFixtures.Load(relativePath: "src/Puck.World/Assets/worlds/games/go.puck"));

    private static WorldFixture Boot() {
        var fixture = Fixtures.FreshServer(definition: Source.Value);

        for (var tick = 0; (tick < 4); tick++) {
            fixture.Step();
        }

        return fixture;
    }
    private static void Set(WorldFixture fixture, string row, string key, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Kind: WorldDocumentWriteKind.Set,
        Key: key,
        Principal: Principal.Console,
        Row: row,
        Value: value
    ));
    private static void Slot(WorldFixture fixture, string row, long value) => Set(
        fixture: fixture,
        key: WorldStateRow.SlotKey.Value,
        row: row,
        value: value
    );
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

    // A move's tick evaluates every rule once and fires the door of the colour to move and that colour's report; the
    // door of the other colour, the refusal, the pass and the scoring rules stay closed. An idle tick fires nothing.
    [Fact]
    public void AMoveFiresItsDoorAndItsReportAndAnIdleTickFiresNothing() {
        using var fixture = Boot();
        var work = fixture.Server.RuleHost.RuleWork;

        for (var tick = 0; (tick < 64); tick++) {
            fixture.Step();
        }

        var idleEvaluations = Count(kind: RuleWorkKinds.Evaluations, source: work);
        var idleFirings = Count(kind: RuleWorkKinds.Firings, source: work);
        var idleBytes = AllocationWindow.Total(window: () => fixture.Step());

        idleEvaluations = (Count(kind: RuleWorkKinds.Evaluations, source: work) - idleEvaluations);
        idleFirings = (Count(kind: RuleWorkKinds.Firings, source: work) - idleFirings);

        var moves = new long[8];
        var firings = new long[8];
        var evaluations = new long[8];

        // Black and white alternate down the centre column, never touching, so every move is an ordinary placement.
        for (var move = 0; (move < moves.Length); move++) {
            Set(fixture: fixture, key: "play", row: "stone", value: (180 + (((move % 2) == 0) ? (move * 2) : ((move * 2) + 38))));

            var evaluated = Count(kind: RuleWorkKinds.Evaluations, source: work);
            var fired = Count(kind: RuleWorkKinds.Firings, source: work);

            moves[move] = AllocationWindow.Total(window: () => fixture.Step());
            evaluations[move] = (Count(kind: RuleWorkKinds.Evaluations, source: work) - evaluated);
            firings[move] = (Count(kind: RuleWorkKinds.Firings, source: work) - fired);
            Assert.Equal(expected: (move + 1L), actual: Read(fixture: fixture, row: "moves"));
            fixture.Step();
        }

        // A request on an occupied point is the same console write through the same mutation door, answered by the
        // refusal rule alone; what a move allocates beyond it is the move's own rule work.
        Set(fixture: fixture, key: "play", row: "stone", value: 180L);

        var door = AllocationWindow.Total(window: () => fixture.Step());

        Assert.Equal(expected: 1L, actual: Read(fixture: fixture, row: "rejected"));
        Array.Sort(array: moves);
        output.WriteLine(message: $"go: idle tick {idleEvaluations} evaluations, {idleFirings} firings, {idleBytes:N0} bytes; move tick {evaluations[0]}..{evaluations[^1]} evaluations, {firings[0]}..{firings[^1]} firings, median {moves[(moves.Length / 2)]:N0} bytes, widest {moves[^1]:N0}; the mutation door and refusal alone {door:N0} bytes");
        Assert.Equal(actual: idleFirings, expected: 0L);
        Assert.All(collection: firings, action: static fired => Assert.Equal(actual: fired, expected: 2L));
        Assert.All(collection: evaluations, action: evaluated => Assert.Equal(actual: evaluated, expected: idleEvaluations));
        Assert.True(condition: (idleBytes < 1024L), userMessage: $"idle tick {idleBytes:N0} bytes");
        Assert.True(condition: ((moves[(moves.Length / 2)] - door) < (16L * 1024L)), userMessage: $"move median {moves[(moves.Length / 2)]:N0} bytes against the door's {door:N0}");
    }
    // Two corners walled off by seven stones each: the flood passes, one a tick, until a pass reaches nothing new; the
    // count follows on the tick after. The open middle is about thirty points across, so the flood settles well short
    // of its pass ceiling.
    [Fact]
    public void TheAreaCountSettlesInABoundedNumberOfFloodPasses() {
        using var fixture = Boot();

        foreach (var cell in new[] { 3, 22, 41, 60, 59, 58, 57 }) {
            Set(fixture: fixture, key: cell.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), row: "board", value: 1L);
        }
        foreach (var cell in new[] { 357, 338, 319, 300, 301, 302, 303 }) {
            Set(fixture: fixture, key: cell.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), row: "board", value: 2L);
        }

        Set(fixture: fixture, key: "1", row: "stones", value: 7L);
        Set(fixture: fixture, key: "2", row: "stones", value: 7L);
        Slot(fixture: fixture, row: "passRequest", value: 2L);
        Slot(fixture: fixture, row: "passes", value: 1L);
        fixture.Step();
        Assert.Equal(expected: 1L, actual: Read(fixture: fixture, row: "over"));

        var passes = fixture.StepUntil(
            ceiling: 256,
            settled: () => (Read(fixture: fixture, row: "scoring") == 2L)
        );

        output.WriteLine(message: $"go: the area count settled after {passes} ticks");
        Assert.InRange(actual: passes, high: 40, low: 10);
        Assert.Equal(expected: 9L, actual: Read(fixture: fixture, key: "1", row: "territory"));
        Assert.Equal(expected: 9L, actual: Read(fixture: fixture, key: "2", row: "territory"));
        Assert.Equal(expected: 2L, actual: Read(fixture: fixture, row: "winner"));
    }

    // One CPU move on the empty board, from enabling the job to the stone standing, told by the search's own counters.
    private static (int Ticks, long Candidates, long Expansions, long PlayoutPlies, int TreeNodes, long Peak, long Allowance, long JudgeCost, long Target) CpuMove() {
        using var fixture = Boot();
        IWorkCounterSource search = fixture.Server.Search;
        var candidates = Count(kind: SearchWorkKinds.Candidates, source: search);
        var expansions = Count(kind: SearchWorkKinds.Expansions, source: search);
        var plies = Count(kind: SearchWorkKinds.PlayoutPlies, source: search);

        Slot(fixture: fixture, row: "aiSide", value: 1L);

        var ticks = fixture.StepUntil(
            ceiling: 400,
            settled: () => fixture.Server.Search.Status(index: 0).Done
        );
        var landed = fixture.Server.Search.Status(index: 0);
        var measured = (
            Ticks: ticks,
            Candidates: (Count(kind: SearchWorkKinds.Candidates, source: search) - candidates),
            Expansions: (Count(kind: SearchWorkKinds.Expansions, source: search) - expansions),
            PlayoutPlies: (Count(kind: SearchWorkKinds.PlayoutPlies, source: search) - plies),
            landed.TreeNodes,
            Peak: landed.PeakStepWork,
            landed.Allowance,
            landed.JudgeCost,
            Target: ((long)landed.BestTarget)
        );

        _ = fixture.StepUntil(
            ceiling: 4,
            settled: () => (Read(fixture: fixture, row: "moves") == 1L)
        );
        Assert.Equal(expected: 1L, actual: Read(fixture: fixture, row: "moves"));
        Assert.Equal(expected: measured.Target, actual: Read(fixture: fixture, row: "placed"));

        return measured;
    }

    [Fact]
    public void TheCpuLandsOneMoveUnderItsAllowanceAndTheSameWorkEveryRun() {
        var first = CpuMove();
        var second = CpuMove();

        output.WriteLine(message: $"go cpu: {first.Ticks} ticks, {first.Candidates} candidates judged, {first.Expansions} nodes grown, {first.PlayoutPlies} playout plies, {first.TreeNodes} of {SearchCapacity.TreeNodes} pool nodes, peak {first.Peak:N0} of {first.Allowance:N0} work units a tick, {first.JudgeCost:N0} a judge, played {first.Target}");
        Assert.Equal(actual: second, expected: first);
        Assert.True(condition: (first.Peak <= first.Allowance), userMessage: $"peak {first.Peak} over allowance {first.Allowance}");
        // The root walk judges each of the 361 points once. Each of the tree's 256 iterations grows exactly one node on
        // a board this open, plays out the three plies below it that a depth of four leaves, and judges a handful of
        // candidates in all, never a board's worth; the pool holds the root and the grown nodes.
        Assert.Equal(actual: first.Expansions, expected: 256L);
        Assert.Equal(actual: first.PlayoutPlies, expected: (3L * 256L));
        Assert.Equal(actual: first.TreeNodes, expected: 257);
        Assert.InRange(actual: first.Candidates, high: (361L + (256L * 16L)), low: (361L + (256L * 4L)));
        Assert.InRange(actual: first.Target, high: 360L, low: 0L);
    }
}
