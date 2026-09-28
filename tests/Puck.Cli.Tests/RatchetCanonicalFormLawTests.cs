using Puck.Analyzers;
using Puck.Cli.Ratchets;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for the one spelling of each ratchet ledger under <c>--check</c> (<see cref="RatchetCommand.Check"/>): a
/// ledger whose counts all hold is still refused when its bytes differ from what its verb writes, here two entries
/// swapped out of ordinal order, and the refusal names the rewrite; the verb's own rendering of the same ledger passes.
/// Each law runs over both ledgers, <c>FileLengths.json</c> and <c>CommentSmells.json</c>.
/// </summary>
public sealed class RatchetCanonicalFormLawTests {
    private static RatchetCommand.Gate GateFor(string verb) => verb switch {
        "lengths" => RatchetCommand.LengthsGate,
        "comment-smells" => RatchetCommand.CommentSmellsGate,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: verb,
            message: "no ledger verb has this name.",
            paramName: nameof(verb)
        ),
    };
    // Three files recorded above the ledger's ceiling, each measured at exactly its recorded count, so every count
    // holds and the ledger's form is the only thing left to judge.
    private static Dictionary<string, int> Measured(string verb) {
        var ceiling = ((verb == "lengths")
            ? 2000
            : 0
        );

        return new Dictionary<string, int>(comparer: StringComparer.Ordinal) {
            ["src/Puck.Alpha/Alpha.cs"] = (ceiling + 3),
            ["src/Puck.Beta/Beta.cs"] = (ceiling + 2),
            ["src/Puck.Gamma/Gamma.cs"] = (ceiling + 1),
        };
    }
    private static string Canonical(string verb) => RatchetLedger.Render(
        ceiling: ((verb == "lengths")
            ? 2000
            : 0
        ),
        recorded: Measured(verb: verb)
    );
    private static IReadOnlyList<string> Check(string verb, string ledgerText) {
        Assert.True(
            condition: RatchetLedger.TryParse(
                error: out var error,
                json: ledgerText,
                ledger: out var ledger
            ),
            userMessage: error
        );

        return RatchetCommand.Check(
            gate: GateFor(verb: verb),
            ledger: ledger!,
            ledgerText: ledgerText,
            measured: Measured(verb: verb)
        );
    }

    [InlineData("lengths")]
    [InlineData("comment-smells")]
    [Theory]
    public void TwoSwappedEntriesAreRefusedAndTheRefusalNamesTheRewrite(string verb) {
        var lines = Canonical(verb: verb).Split(separator: '\n');
        var first = Array.FindIndex(
            array: lines,
            match: static line => line.Contains(value: "src/Puck.Alpha/Alpha.cs")
        );
        var second = Array.FindIndex(
            array: lines,
            match: static line => line.Contains(value: "src/Puck.Beta/Beta.cs")
        );

        (lines[first], lines[second]) = (lines[second], lines[first]);

        var problems = Check(
            ledgerText: string.Join(
                separator: '\n',
                value: lines
            ),
            verb: verb
        );
        var problem = Assert.Single(collection: problems);

        Assert.StartsWith(
            actualString: problem,
            expectedStartString: "not canonical:"
        );
        Assert.Contains(
            actualString: problem,
            expectedSubstring: GateFor(verb: verb).LedgerFileName
        );
        Assert.Contains(
            actualString: problem,
            expectedSubstring: $"run 'puck {verb}' without --check to rewrite it"
        );
    }
    [InlineData("lengths")]
    [InlineData("comment-smells")]
    [Theory]
    public void TheVerbsOwnRenderingPasses(string verb) =>
        Assert.Empty(collection: Check(
            ledgerText: Canonical(verb: verb),
            verb: verb
        ));
}
