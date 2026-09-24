using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: an arena reports its work counts through <see cref="IWorkCounterSource"/> under the
/// <see cref="ArenaWork"/> kinds, reads any other kind as unavailable, and never lowers a count: closing a second change
/// window adds its probes to the first's.</summary>
public sealed class ArenaWorkLawTests {
    private static long Read(IWorkCounterSource arena, WorkKind kind) {
        Assert.True(condition: arena.TryRead(kind: kind, value: out var value));

        return value;
    }

    [Fact]
    public void AnArenaDeclaresItsKindsAndNothingElse() {
        var (_, arena) = ArenaFixture.Build();
        IWorkCounterSource source = arena;

        Assert.Equal(actual: source.WorkKinds.ToArray(), expected: ArenaWork.Kinds.ToArray());
        Assert.Equal(
            actual: ArenaWork.Kinds.ToArray().Select(selector: kind => kind.Name),
            expected: ["state.arena.visits", "state.arena.change-window-probes", "state.arena.scratch-leased-elements"]
        );
        Assert.False(condition: source.TryRead(kind: GpuWork.Dispatches, value: out var undeclared));
        Assert.Equal(actual: undeclared, expected: 0L);
    }
    [Fact]
    public void ChangeWindowProbesAccumulateAcrossWindows() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var none = CellSet.Empty(length: catalog.Descriptors.Count);
        var probes = new List<long> { Read(arena: arena, kind: ArenaWork.ChangeWindowProbes) };

        for (var window = 0; (window < 2); window++) {
            arena.BeginChangeWindow();

            var mark = arena.BeginScope();

            Assert.True(condition: arena.TryWrite(key: slot, operand: (window + 1L), reason: out var reason, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set), userMessage: reason);
            arena.Commit(mark: mark);
            Assert.False(condition: arena.EndChangeWindow(rows: none));
            probes.Add(item: Read(arena: arena, kind: ArenaWork.ChangeWindowProbes));
        }

        Assert.True(condition: (probes[1] > probes[0]), userMessage: string.Join(separator: ", ", values: probes));
        Assert.Equal(actual: (probes[2] - probes[1]), expected: (probes[1] - probes[0]));
    }
}
