using Puck.Abstractions.Counting;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: rewinding a retained turn is priced by the work of restoring it, not by the memory the
/// history reserves: the price is at least what the widest turn's rewind visits, at most a small multiple of it, and the
/// same at every retained depth.</summary>
/// <remarks>The turn is the widest the plan admits: every cell of a 16x16 board written and a full twenty-token pile
/// reversed, so every number, presence and member-key position the plan retains is restored and the pile's key index
/// is rebuilt.</remarks>
public sealed class ArenaRewindWorkLawTests(ITestOutputHelper output) {
    private const int Members = 20;
    private const int Side = 16;
    // The widest the price may run above the work a wide turn's rewind visits: the per-position bookkeeping the
    // visits do not count (generation, version, reindex mark, group containment, the popped segment's clear).
    private const long Slack = 16L;

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateSection Section() => new(
        Lattices: [new LatticeTopology.Grid(Name: "board", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: Side, Depth: Side)],
        Rows: [
            new StateRow(
                Name: Name(value: "tokens"),
                Kind: CellKind.Int,
                Capacity: Members,
                Cells: [.. Enumerable.Range(count: Members, start: 0).Select(selector: token => new StateCell(Key: Name(value: $"t{token}"), Value: CellValue.Int(value: token)))]
            ),
            new StateRow(
                Name: Name(value: "pile"),
                Kind: CellKind.Int,
                Capacity: Members,
                Domain: new StateDomain.KeysOf(Ordered: true, Row: Name(value: "tokens")),
                Cells: [.. Enumerable.Range(count: Members, start: 0).Select(selector: token => new StateCell(Key: Name(value: $"t{token}"), Value: CellValue.Int(value: token)))]
            ),
            new StateRow(Name: Name(value: "board"), Kind: CellKind.Int, Domain: new StateDomain.CellsOf(Topology: "board")),
        ]
    );
    private static (StateArena Arena, ArenaUndoPlan Plan) Retained(int depth) {
        var section = Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog, section, ArenaTime.Origin);
        var plan = new ArenaUndoPlan(Depth: depth, Name: "turn", Rows: [1, 2]);

        arena.ConfigureUndo(plans: [plan]);
        arena.BeginUndoTurn(group: "turn");
        arena.BeginUndoPass(group: "turn");

        var mark = arena.BeginScope();
        Span<int> reversed = stackalloc int[Members];

        for (var position = 0; (position < Members); position++) {
            reversed[position] = ((Members - 1) - position);
        }
        Assert.True(condition: arena.TryReorder(order: reversed, reason: out var reason, rowOrdinal: 1), userMessage: reason);
        for (var cell = 0; (cell < (Side * Side)); cell++) {
            Assert.True(condition: arena.TryWrite(
                key: arena.Keys.Intern(name: Name(value: cell.ToString(provider: System.Globalization.CultureInfo.InvariantCulture))),
                operand: (cell + 1L),
                reason: out reason,
                rowOrdinal: 2,
                write: StateWriteKind.Set
            ), userMessage: reason);
        }
        arena.Commit(mark: mark);
        arena.EndUndoPass(group: "turn");
        arena.CommitUndoTurn(group: "turn");

        return (arena, plan);
    }
    // The lanes the arena has visited so far, read through its work counter source.
    private static long Visits(IWorkCounterSource arena) {
        Assert.True(condition: arena.TryRead(kind: ArenaWork.Visits, value: out var visits));

        return visits;
    }

    [Fact]
    public void ARewindIsPricedAtLeastItsRestoresAndAtMostASmallMultipleOfThem() {
        var (arena, plan) = Retained(depth: 4);
        var price = StateArena.EstimateRewindWork(catalog: arena.Catalog, layout: arena.Layout, plan: plan, plans: [plan]);
        var before = Visits(arena: arena);

        Assert.True(condition: arena.TryRewindGroup(group: "turn", reason: out var reason), userMessage: reason);

        var observed = (Visits(arena: arena) - before);

        output.WriteLine(message: $"rewind: priced {price}, observed {observed}");
        // Every board cell's number and presence, and the reversed pile's lanes, were restored.
        Assert.True(condition: (observed >= ((2L * Side) * Side)), userMessage: $"rewind observed {observed}");
        Assert.True(condition: (price >= observed), userMessage: $"rewind priced {price}, under the {observed} it did");
        Assert.True(condition: (price <= (Slack * observed)), userMessage: $"rewind priced {price}, past {Slack} times the {observed} it did");
    }
    [Fact]
    public void ARewindCostsTheSameAtEveryRetainedDepth() {
        var (shallow, shallowPlan) = Retained(depth: 1);
        var (deep, deepPlan) = Retained(depth: 32);

        Assert.Equal(
            expected: StateArena.EstimateRewindWork(catalog: shallow.Catalog, layout: shallow.Layout, plan: shallowPlan, plans: [shallowPlan]),
            actual: StateArena.EstimateRewindWork(catalog: deep.Catalog, layout: deep.Layout, plan: deepPlan, plans: [deepPlan])
        );
        Assert.True(condition: (StateArena.EstimateUndoBytes(catalog: deep.Catalog, layout: deep.Layout, plans: [deepPlan]) > StateArena.EstimateUndoBytes(catalog: shallow.Catalog, layout: shallow.Layout, plans: [shallowPlan])));
    }
}
