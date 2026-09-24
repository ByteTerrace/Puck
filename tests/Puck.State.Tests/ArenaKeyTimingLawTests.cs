using Puck.Abstractions.Counting;
using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

/// <summary>THE LAW: compiler symbols arriving after an arena exists do not alter its retained ledger until a
/// runtime operation admits the name, and speculative key work stays allocation-stable after the table is warm.</summary>
public sealed class ArenaKeyTimingLawTests {
    [Fact]
    public void ACompilerLiteralAddedAfterConstructionIsSourceOnlyUntilRuntimeAdmission() {
        var (catalog, arena) = ArenaFixture.Build();
        var beforeCount = arena.Keys.Count;
        var beforeBytes = arena.Keys.Bytes;
        var beforeHash = arena.ComputeHash();
        var late = ArenaFixture.Name(value: "compiler-literal-late");
        var compiled = catalog.Keys.Intern(name: late);

        Assert.Equal(beforeCount, arena.Keys.Count);
        Assert.Equal(beforeBytes, arena.Keys.Bytes);
        Assert.Equal(beforeHash, arena.ComputeHash());
        Assert.False(condition: arena.TryRead(key: compiled, rowOrdinal: ArenaFixture.Tokens, value: out _));

        Assert.True(condition: arena.TryMint(
            rowOrdinal: ArenaFixture.Tokens,
            name: late,
            value: CellValue.Int(value: 1L),
            key: out var runtime,
            reason: out var reason
        ), userMessage: reason);
        Assert.True(condition: arena.TryWrite(
            key: compiled,
            operand: 7L,
            reason: out reason,
            rowOrdinal: ArenaFixture.Tokens,
            write: StateWriteKind.Set
        ), userMessage: reason);
        Assert.True(condition: arena.TryRead(key: runtime, rowOrdinal: ArenaFixture.Tokens, value: out var value));
        Assert.Equal(CellValue.Int(value: 7L), value);
    }
    [Fact]
    public void RuntimeAdmissionHashIsIndependentOfCompilerLiteralTiming() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var early = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var name = ArenaFixture.Name(value: "timing-independent");

        _ = catalog.Keys.Intern(name: name);
        var late = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.Equal(early.Keys.Count, late.Keys.Count);
        Assert.Equal(early.Keys.Bytes, late.Keys.Bytes);
        Assert.Equal(early.ComputeHash(), late.ComputeHash());

        Assert.True(condition: early.TryMint(ArenaFixture.Tokens, name, CellValue.Int(value: 1L), out _, out var earlyReason), userMessage: earlyReason);
        Assert.True(condition: late.TryMint(ArenaFixture.Tokens, name, CellValue.Int(value: 1L), out _, out var lateReason), userMessage: lateReason);
        Assert.Equal(early.ComputeHash(), late.ComputeHash());
    }
    [Fact]
    public void WarmRetainedLedgerSpeculationAllocatesNothingPerCandidate() {
        var (_, arena) = ArenaFixture.Build();
        var retained = new CellName[20_000];

        for (var index = 0; (index < retained.Length); index++) {
            retained[index] = ArenaFixture.Name(value: $"retained-{index}");
            Assert.True(condition: arena.Keys.TryIntern(retained[index], out _, out var reason), userMessage: reason);
        }

        var candidates = Enumerable.Range(count: 64, start: 0)
            .Select(selector: index => ArenaFixture.Name(value: $"candidate-{index}"))
            .ToArray();
        var warm = arena.BeginScope();

        Assert.True(condition: arena.TryMint(
            rowOrdinal: ArenaFixture.Tokens,
            name: candidates[0],
            value: CellValue.Int(value: 1L),
            key: out _,
            reason: out var warmReason
        ), userMessage: warmReason);
        var warmHash = Fnv1aHash.Create();

        arena.AddKeyLedgerTo(hash: ref warmHash);
        arena.Rewind(mark: warm);

        var before = AllocationWindow.Least(window: () => {
            for (var index = 0; (index < candidates.Length); index++) {
                var mark = arena.BeginScope();

                Assert.True(condition: arena.TryMint(
                    rowOrdinal: ArenaFixture.Tokens,
                    name: candidates[index],
                    value: CellValue.Int(value: 1L),
                    key: out _,
                    reason: out var reason
                ), userMessage: reason);
                var hash = Fnv1aHash.Create();

                arena.AddKeyLedgerTo(hash: ref hash);
                arena.Rewind(mark: mark);
            }
        });

        Assert.Equal(actual: before, expected: 0L);
    }
    [Fact]
    public void WarmSparseKeyMintReusesFarOrdinalMapStorage() {
        var section = new StateSection(Rows: [new StateRow(
            Name: ArenaFixture.Name(value: "sparse"),
            Kind: CellKind.Int,
            Capacity: 2,
            Cells: [new StateCell(
                Key: ArenaFixture.Name(value: "seed"),
                Value: CellValue.Int(value: 1L)
            )]
        )]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        for (var index = 0; (index < 200); index++) {
            Assert.True(condition: arena.Keys.TryIntern(
                ArenaFixture.Name(value: $"sparse-orphan-{index}"),
                out _,
                out var orphanReason
            ), userMessage: orphanReason);
        }
        var candidate = ArenaFixture.Name(value: "sparse-candidate");
        const int Row = 0;
        var warm = arena.BeginScope();

        Assert.True(condition: arena.TryMint(Row, candidate, CellValue.Int(value: 1L), out _, out var warmReason), userMessage: warmReason);
        arena.Rewind(mark: warm);

        var before = AllocationWindow.Least(window: () => {
            for (var index = 0; (index < 64); index++) {
                var mark = arena.BeginScope();

                Assert.True(condition: arena.TryMint(Row, candidate, CellValue.Int(value: 1L), out _, out var reason), userMessage: reason);
                arena.Rewind(mark: mark);
            }
        });

        Assert.Equal(actual: before, expected: 0L);
    }
}
