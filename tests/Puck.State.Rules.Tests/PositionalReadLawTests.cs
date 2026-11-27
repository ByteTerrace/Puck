using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a lattice or ring cell read through a computed integer key reads the cell at that
/// position, answering exactly what a read of the key the position's decimal spelling interns answers: the held value
/// in range, zero past either end.</summary>
public sealed class PositionalReadLawTests {
    [InlineData("target", 4)]
    [InlineData("log", 3)]
    [Theory]
    public void AComputedIndexReadsTheCellItsSpellingNames(string row, int cells) {
        var section = TransformFixture.Section();
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(
            context: context,
            section: section
        );
        var arena = host.Arena;
        var ordinal = TransformFixture.Ordinal(context: context, name: row);

        // A ring takes pushes only, and one push short of full leaves its last slot unheld.
        for (var position = 0; (position < cells); position++) {
            var reason = string.Empty;

            Assert.True(condition: ((row == "log")
                ? ((position == (cells - 1)) || arena.TryPush(reason: out reason, rowOrdinal: ordinal, value: (10L * (position + 1))))
                : arena.TryWrite(
                    key: context.Catalog.Keys.Intern(name: CellName.Parse(candidate: position.ToString(provider: System.Globalization.CultureInfo.InvariantCulture))),
                    operand: (10L * (position + 1)),
                    reason: out reason,
                    rowOrdinal: ordinal,
                    write: StateWriteKind.Set
                )), userMessage: reason);
        }

        for (var position = -2; (position < (cells + 2)); position++) {
            var program = RuleCompiler.CompileExpression(
                context: context,
                expression: RulesFixture.Program(text: $"{row}[{position} + 0]"),
                kind: CellKind.Int,
                ruleName: "law",
                verb: "law"
            );
            var read = Assert.IsType<StateCellOperand>(@object: Assert.Single(
                collection: program,
                predicate: static token => (token.Operand is not null)
            ).Operand);

            Assert.True(condition: read.ReadsByPosition);

            var frame = new EvaluationFrame(reader: host);

            Assert.True(condition: RuleExpressions.Evaluate(
                frame: ref frame,
                kind: CellKind.Int,
                program: new CompiledProgram(tokens: program)
            ).TryFinite(raw: out var actual));

            var time = ArenaTime.Origin;
            var expected = ((arena.Keys.TryResolveIndex(index: position, key: out var key) &&
                arena.TryReadLiveNumber(key: key, rowOrdinal: ordinal, time: in time, value: out var held))
                ? held
                : 0L);

            Assert.Equal(
                actual: actual,
                expected: expected
            );
        }
    }
}
