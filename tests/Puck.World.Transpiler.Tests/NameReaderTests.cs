using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>One routine reads every name, so every position that takes a name takes the same four spellings — a
/// bare identifier, a plain string, a raw string, and a reserved-channel name. A raw string reads as a name because
/// the one routine knows the raw fence; the separate string read it replaced stopped at the empty <c>""</c> the
/// fence opens with.</summary>
public class NameReaderTests {
    private static DocumentNode Parse(string source) {
        var diagnostics = new DiagnosticBag();
        var parsed = PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            diagnostics: diagnostics,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.NotNull(@object: parsed.Value);

        return parsed.Value!;
    }

    [Fact]
    public void ARawStringNamesTheSchema() {
        Assert.Equal(
            actual: Parse(source: "schema: \"\"\"puck.world.definition.v1\"\"\"\n").Schema,
            expected: "puck.world.definition.v1"
        );
    }
    [InlineData("rule \"\"\"raw named\"\"\" {\n  counter += 1\n}\n")]
    [InlineData("rule \"raw named\" {\n  counter += 1\n}\n")]
    [Theory]
    public void ARuleNameReadsTheSameInEitherStringSpelling(string source) {
        var rule = Assert.IsType<RuleBlockNode>(@object: Parse(source: source).Statements[0]);

        Assert.Equal(
            actual: rule.Name,
            expected: "raw named"
        );
    }
    [Fact]
    public void ABlockNameReadsBareOrQuoted() {
        var bare = Assert.IsType<BlockNode>(@object: Parse(source: "layout study {\n  count: 1\n}\n").Statements[0]);
        var quoted = Assert.IsType<BlockNode>(@object: Parse(source: "layout \"study\" {\n  count: 1\n}\n").Statements[0]);

        Assert.Equal(
            actual: bare.Name,
            expected: quoted.Name
        );
        Assert.False(condition: bare.NameQuoted);
        Assert.True(condition: quoted.NameQuoted);
    }
    // The spelling rides the node, so the printer writes a name back the way it was written rather than the way the
    // reader happened to normalize it.
    [InlineData("layout study {\n  count: 1\n}\n")]
    [InlineData("layout \"study\" {\n  count: 1\n}\n")]
    [Theory]
    public void ABlockNamePrintsInTheSpellingItWasWrittenIn(string source) {
        Assert.Equal(
            actual: PuckFormat.Format(source),
            expected: source
        );
    }
    // A property name is quoted only where printing it bare would open a different production: `step:` inside a
    // `fill` block is a property wherever it stands, while `score:` inside a rule body is the score statement.
    [InlineData("step")]
    [InlineData("schema")]
    [InlineData("table")]
    [InlineData("enum")]
    [InlineData("rules")]
    [InlineData("set")]
    [InlineData("pattern")]
    [InlineData("skip")]
    [Theory]
    public void APropertyNameThatCannotBeMisreadPrintsBare(string name) {
        var source = $"host {{\n  {name}: 1\n}}\n";

        Assert.Equal(
            actual: PuckFormat.Format(source),
            expected: source
        );

        var block = Assert.IsType<BlockNode>(@object: Parse(source: source).Statements[0]);
        var property = Assert.IsType<PropertyNode>(@object: block.Statements[0]);

        Assert.Equal(
            actual: property.Name,
            expected: name
        );
    }
    [InlineData("score")]
    [InlineData("when")]
    [InlineData("let")]
    [InlineData("import")]
    [InlineData("watchMemory")]
    [Theory]
    public void APropertyNameAStatementReaderWouldClaimPrintsQuoted(string name) {
        var source = $"host {{\n  \"{name}\": 1\n}}\n";

        Assert.Equal(
            actual: PuckFormat.Format(source),
            expected: source
        );
    }
    // At the top level `schema:` and `basis:` are the document's own headers, so a property of either name is the
    // one place the quoting is load-bearing.
    [Fact]
    public void ADocumentHeaderNamePrintsQuotedOnlyAtTheTopLevel() {
        const string Source = "\"schema\": 1\nhost {\n  schema: 2\n}\n";

        Assert.Equal(
            actual: PuckFormat.Format(Source),
            expected: Source
        );
    }
    // A cell key is folded by the reader into the document's own spelling; `Puck.State` owns the fold back, so an
    // effect target reads the same way as the gate above it rather than in two spellings eleven lines apart.
    [Fact]
    public void ACellKeyPrintsInTheSpellingItWasWrittenIn() {
        const string Source = "rule \"r\" {\n  when board[next[$value]] == 1\n  board[next[$value]] = 2\n  board[1 + 2] = 3\n}\n";

        Assert.Equal(
            actual: PuckFormat.Format(Source),
            expected: Source
        );
        // The parentheses an author writes around a computed key are the reader's, not the document's: it keeps the
        // key and drops them, and the printer writes back only what reads as the same key.
        Assert.Equal(
            actual: PuckFormat.Format("rule \"r\" {\n  board[(1 + 2)] = 3\n}\n"),
            expected: "rule \"r\" {\n  board[1 + 2] = 3\n}\n"
        );
    }
    [Fact]
    public void AReservedChannelNameReadsAsOneOperand() {
        var rule = Assert.IsType<RuleBlockNode>(@object: Parse(source: "rule \"r\" {\n  when $channel:1:strafe >= 0.5\n  counter += 1\n}\n").Statements[0]);
        var gate = Assert.IsType<WhenStatementNode>(@object: rule.Statements[0]);
        var comparison = Assert.IsType<ComparisonPredicateNode>(@object: gate.Predicate);

        Assert.Equal(
            actual: comparison.LeftText,
            expected: "$channel:1:strafe"
        );
    }
}
