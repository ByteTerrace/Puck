using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>Exercises <see cref="RatchetLedger"/>: parsing, judging one file, reconciling a whole tree, and
/// rendering the document the verbs write.</summary>
public sealed class RatchetLedgerTests {
    private static RatchetLedger Parse(int ceiling, params (string Key, int Count)[] recorded) {
        Assert.True(
            condition: RatchetLedger.TryParse(
                error: out var error,
                json: RatchetLedger.Render(
                    ceiling: ceiling,
                    recorded: recorded.Select(selector: static row => new KeyValuePair<string, int>(
                        key: row.Key,
                        value: row.Count
                    ))
                ),
                ledger: out var ledger
            ),
            userMessage: error
        );

        return ledger!;
    }
    private static Dictionary<string, int> Tree(params (string Key, int Count)[] files) =>
        files.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static file => file.Count,
            keySelector: static file => file.Key
        );

    [Fact]
    public void ALinkedFileKeysToItsOwnPathNotTheLinkingProjects() {
        Assert.Equal(
            actual: RatchetLedger.KeyFor(
                filePath: "D:\\repo\\tests\\Puck.World.Browser.Tests\\..\\..\\src\\Puck.World.Browser\\Engine\\BrowserSession.cs",
                ledgerDirectory: "D:\\repo"
            ),
            expected: "src/Puck.World.Browser/Engine/BrowserSession.cs"
        );
        Assert.Equal(
            actual: RatchetLedger.KeyFor(
                filePath: "/repo/src/./Puck.Cli/Program.cs",
                ledgerDirectory: "/repo"
            ),
            expected: "src/Puck.Cli/Program.cs"
        );
    }
    [Fact]
    public void AFallenCountIsLoweredAndAStaleEntryRemoved() {
        var ledger = Parse(
            ceiling: 10,
            recorded: [("a.cs", 20), ("b.cs", 15), ("gone.cs", 12)]
        );

        var reconciliation = ledger.Reconcile(measured: Tree(("a.cs", 18), ("b.cs", 9)));

        Assert.False(condition: reconciliation.Refused);
        Assert.Equal(
            actual: reconciliation.Next,
            expected: [new KeyValuePair<string, int>(key: "a.cs", value: 18)]
        );
        Assert.Equal(
            actual: reconciliation.Findings.Select(selector: static finding => (finding.Key, finding.Verdict)),
            expected: [("b.cs", RatchetVerdict.Stale), ("gone.cs", RatchetVerdict.Stale)]
        );
    }
    [Fact]
    public void ARisenOrNewFileRefusesTheRewriteAndKeepsTheRecordedCount() {
        var ledger = Parse(
            ceiling: 10,
            recorded: [("a.cs", 20)]
        );

        var reconciliation = ledger.Reconcile(measured: Tree(("a.cs", 21), ("new.cs", 11)));

        Assert.True(condition: reconciliation.Refused);
        Assert.Equal(
            actual: reconciliation.Next,
            expected: [new KeyValuePair<string, int>(key: "a.cs", value: 20)]
        );
        Assert.Equal(
            actual: reconciliation.Findings.Select(selector: static finding => (finding.Key, finding.Verdict)),
            expected: [("a.cs", RatchetVerdict.Grew), ("new.cs", RatchetVerdict.OverCeiling)]
        );
    }
    [Fact]
    public void ALowerCeilingRecordsEveryFileOverItAtItsCount() {
        var ledger = Parse(ceiling: 10);

        var reconciliation = ledger.Reconcile(
            ceiling: 5,
            measured: Tree(("a.cs", 5), ("b.cs", 6), ("c.cs", 10))
        );

        Assert.False(condition: reconciliation.Refused);
        Assert.Equal(
            actual: reconciliation.Ceiling,
            expected: 5
        );
        Assert.Equal(
            actual: reconciliation.Next,
            expected: [new KeyValuePair<string, int>(key: "b.cs", value: 6), new KeyValuePair<string, int>(key: "c.cs", value: 10)]
        );
    }
    [Fact]
    public void ALowerCeilingNeverRecordsAFileAlreadyOverTheOldOne() {
        var ledger = Parse(ceiling: 10);

        var reconciliation = ledger.Reconcile(
            ceiling: 5,
            measured: Tree(("a.cs", 11))
        );

        Assert.True(condition: reconciliation.Refused);
        Assert.Equal(
            actual: reconciliation.Findings.Single().Verdict,
            expected: RatchetVerdict.OverCeiling
        );
    }
    [Fact]
    public void ACeilingNeverRises() {
        var ledger = Parse(ceiling: 10);

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ledger.Reconcile(
            ceiling: 11,
            measured: Tree()
        ));
    }
    [Fact]
    public void JudgeAgreesWithReconcile() {
        var ledger = Parse(
            ceiling: 10,
            recorded: [("a.cs", 20)]
        );

        Assert.Equal(
            actual: ledger.Judge(
                count: 20,
                key: "a.cs"
            ),
            expected: RatchetVerdict.Within
        );
        Assert.Equal(
            actual: ledger.Judge(
                count: 21,
                key: "a.cs"
            ),
            expected: RatchetVerdict.Grew
        );
        Assert.Equal(
            actual: ledger.Judge(
                count: 10,
                key: "a.cs"
            ),
            expected: RatchetVerdict.Stale
        );
        Assert.Equal(
            actual: ledger.Judge(
                count: 11,
                key: "b.cs"
            ),
            expected: RatchetVerdict.OverCeiling
        );
        Assert.Equal(
            actual: ledger.Judge(
                count: 10,
                key: "b.cs"
            ),
            expected: RatchetVerdict.Within
        );
    }
    [Fact]
    public void RenderWritesTheCheckedInShape() {
        Assert.Equal(
            actual: RatchetLedger.Render(
                ceiling: 2000,
                recorded: [new KeyValuePair<string, int>(key: "src/b.cs", value: 2002), new KeyValuePair<string, int>(key: "src/a.cs", value: 2001)]
            ),
            expected: "{\n    \"format\": 1,\n    \"ceiling\": 2000,\n    \"recorded\": {\n        \"src/a.cs\": 2001,\n        \"src/b.cs\": 2002\n    }\n}\n"
        );
        Assert.Equal(
            actual: RatchetLedger.Render(
                ceiling: 0,
                recorded: []
            ),
            expected: "{\n    \"format\": 1,\n    \"ceiling\": 0,\n    \"recorded\": {}\n}\n"
        );
    }
}
