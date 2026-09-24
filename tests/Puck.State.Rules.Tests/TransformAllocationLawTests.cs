using System.Reflection;

using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a resolved scalar transform rebuilds no record and reads no name when it fires.
/// Every row, key, board cell, direction and admitted value was resolved once at compile time, so seven of the eleven
/// allocate nothing at all, and the four that allocate take only what the substrate under them forces: a sort's key
/// table, the record a draw's sample travels in, and one stamp per cell an observe marks.</summary>
/// <remarks>Each transform is measured on its own, so one that starts allocating cannot hide behind the others. The
/// ceilings are bytes per firing over this fixture's rows, which is what makes a new per-firing record visible; a
/// refused firing is not measured, because <see cref="EffectRefusal"/> carries its code as an <see cref="Enum"/> and
/// every refusal boxes it.</remarks>
public sealed class TransformAllocationLawTests {
    // Two window sizes over the same firing, and the answer is the slope: tiering and the arena's own one-off scratch
    // land in the intercept, and anything the transform allocates per firing lands in both windows.
    private const int LargeWindow = 256;
    private const int SmallWindow = 64;

    // Every scalar case of the union, each with the bytes per firing it is allowed. A zero is a transform that
    // touches nothing on the heap at all; each non-zero ceiling names what forces it.
    private static readonly (string Name, StateTransform Transform, long Ceiling)[] Cases = [
        // Deterministic selection walks the member columns and allocates nothing.
        ("transfer", new StateTransform.Transfer(
            From: "deck",
            InsertFirst: true,
            Selector: ZoneSelector.Last,
            To: "deck"
        ), 0L),
        // Random selection fires the draw source once per token, and a sample travels back in a record.
        ("transferRandom", new StateTransform.Transfer(
            Count: 1,
            Draw: "coin",
            From: "deck",
            Selector: ZoneSelector.Random,
            To: "hand"
        ), 48L),
        ("setRay", new StateTransform.SetRay(
            Direction: CellName.Parse(candidate: "E"),
            From: "0",
            Pattern: "ones",
            Row: "board",
            Value: 9L
        ), 0L),
        // One Fisher-Yates pass over that same draw source.
        ("shuffle", new StateTransform.Shuffle(
            Draw: "coin",
            Row: "deck"
        ), 48L),
        // A key table sized by the row's live cell count, one column per sort key.
        ("sort by attributes", new StateTransform.Sort(
            By: [new SortKey(Row: "rank")],
            Row: "deck"
        ), 96L),
        ("sort", new StateTransform.Sort(Row: "scores", By: [new SortKey(Row: "scores")]), 64L),
        ("writeSet", new StateTransform.WriteSet(
            Row: "target",
            Set: "mask",
            Value: 2L
        ), 0L),
        ("boardCombine", new StateTransform.BoardCombine(
            Left: "left",
            Operation: BoardCombineOp.Or,
            Right: "right",
            Row: "target",
            Value: 1L
        ), 0L),
        ("arrange", new StateTransform.Arrange(
            From: "rankValue",
            Row: "deck"
        ), 0L),
        ("clearEnclosed", new StateTransform.ClearEnclosed(
            From: "0",
            Lower: 1L,
            Row: "board",
            Upper: 1L
        ), 0L),
        // One stamp per cell the visibility mask marks, because the arena's observation column stores a record
        // rather than a pair of numbers.
        ("observe", new StateTransform.Observe(Row: "known"), 64L),
    ];

    private static long Measure(Action action, int firings) {
        for (var warm = 0; (warm < 16); warm++) {
            action();
        }

        return AllocationWindow.Measure(window: () => {
            for (var repeat = 0; (repeat < firings); repeat++) {
                action();
            }
        });
    }
    private static long PerFiring(ArenaEffectHost host, ArenaTransform transform) {
        var arena = host.Arena;

        void Fire() {
            var mark = arena.BeginScope();

            _ = host.TryTransform(
                binding: ArenaTransformBinding.None,
                moved: out _,
                refusal: out _,
                transform: transform
            );
            arena.Rewind(mark: mark);
        }

        var small = Measure(
            action: Fire,
            firings: SmallWindow
        );
        var large = Measure(
            action: Fire,
            firings: LargeWindow
        );

        return ((large - small) / (LargeWindow - SmallWindow));
    }

    /// <summary>Returns the name of every measured firing.</summary>
    /// <returns>The names.</returns>
    public static TheoryData<string> Names() {
        var names = new TheoryData<string>();

        foreach (var (name, _, _) in Cases) {
            names.Add(row: name);
        }

        return names;
    }
    [MemberData(memberName: nameof(Names))]
    [Theory]
    public void AResolvedTransformFiresWithinItsAllocationCeiling(string name) {
        var (_, transform, ceiling) = Cases.Single(predicate: candidate => string.Equals(
            a: candidate.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));
        var section = TransformFixture.Section();
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(
            context: context,
            section: section
        );

        host.Advance(
            engineTick: 5UL,
            tick: 5UL
        );
        Assert.True(condition: RuleCompiler.TryResolveTransform(
            context: context,
            reason: out var reason,
            resolved: out var resolved,
            transform: transform
        ), userMessage: reason);

        var allocated = PerFiring(
            host: host,
            transform: resolved
        );

        Assert.True(
            condition: (allocated <= ceiling),
            userMessage: $"'{name}' allocated {allocated} bytes per firing, over its ceiling of {ceiling}"
        );
    }
    [Fact]
    public void EveryScalarTransformCarriesACeiling() {
        var measured = Cases
            .Select(selector: static entry => entry.Transform.GetType().Name)
            .ToHashSet(comparer: StringComparer.Ordinal);
        // Vector transforms have their own vector-column allocation laws. PushRay needs a live instance binding and
        // is measured by PushRayLawTests rather than this fixture's unbound scalar firing.
        var separatelyMeasured = new[] { "Mean", "Mix", "Nearest", "Remember", "PushRay" };

        foreach (var name in typeof(StateTransform)
            .GetNestedTypes(bindingAttr: BindingFlags.Public)
            .Where(predicate: static type => type.IsSubclassOf(c: typeof(StateTransform)))
            .Select(selector: static type => type.Name)
        ) {
            Assert.True(
                condition: (separatelyMeasured.Contains(
                    comparer: StringComparer.Ordinal,
                    value: name
                ) || measured.Contains(item: name)),
                userMessage: $"transform '{name}' has no allocation ceiling"
            );
        }
    }
}
