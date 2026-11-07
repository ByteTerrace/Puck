using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a read through a computed address is one program: the tokens that compute the key
/// stand in front of the operand's own, so evaluating it leases one evaluation frame's stack and marks (two arrays of
/// the program's length) and runs no frame of its own for the key.</summary>
public sealed class AddressProgramLawTests {
    private static long Leased(StateArena arena) {
        Assert.True(condition: ((Puck.Abstractions.Counting.IWorkCounterSource)arena).TryRead(
            kind: ArenaWork.ScratchLeasedElements,
            value: out var count
        ));

        return count;
    }

    [Fact]
    public void AComputedKeyIsTokensOfTheProgramThatReadsItAndLeasesOneFrame() {
        var section = TransformFixture.Section();
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(
            context: context,
            section: section
        );
        var program = RuleCompiler.CompileExpression(
            context: context,
            expression: RulesFixture.Program(text: "rank[1 + 1] + 0"),
            kind: CellKind.Int,
            ruleName: "law",
            verb: "law"
        );
        var read = Assert.Single(
            collection: program,
            predicate: static token => (token.Operand is not null)
        );

        Assert.Equal(
            actual: RuleExpressions.Arity(token: in read),
            expected: 1
        );

        var before = Leased(arena: host.Arena);

        var frame = new EvaluationFrame(reader: host);

        Assert.True(condition: RuleExpressions.Evaluate(
            frame: ref frame,
            kind: CellKind.Int,
            program: program
        ).TryFinite(raw: out _));
        Assert.Equal(
            actual: (Leased(arena: host.Arena) - before),
            expected: (2L * program.Length)
        );
    }
}
