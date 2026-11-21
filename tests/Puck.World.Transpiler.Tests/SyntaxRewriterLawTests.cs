using Puck.Transpiler.Ast;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Rewriting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The rewrite law: a rewriter that overrides nothing is the identity, and a rewrite that touches one node
/// leaves every other node's comments and blank lines exactly where their author put them.</summary>
/// <remarks>It runs over every shipped <c>.puck</c> source and over one generated source per construct
/// <see cref="ConstructRegistry"/> registers. That the descent reaches every node kind is the compiler's to hold:
/// a node without <see cref="SyntaxNode.MapChildren"/> does not build.</remarks>
public class SyntaxRewriterLawTests {
    private sealed class Identity : PuckSyntaxRewriter {
    }
    // Renames one bare identifier wherever it is read, which is what a compile-time constant's rename is.
    private sealed class RenameIdentifier : PuckSyntaxRewriter {
        public required string From { get; init; }
        public required string To { get; init; }

        protected override ExpressionNode RewriteExpression(ExpressionNode expression) => (base.RewriteExpression(expression: expression) switch {
            IdentifierExpressionNode identifier when string.Equals(
                a: identifier.Name,
                b: this.From,
                comparisonType: StringComparison.Ordinal
            ) => (identifier with { Name = this.To }),
            var other => other,
        });
    }
    // Answers a `BlockNode Body` position with a statement that is not a block.
    private sealed class ReplaceTemplateBody : PuckSyntaxRewriter {
        protected override StatementNode? RewriteStatement(StatementNode statement) => (base.RewriteStatement(statement: statement) switch {
            BlockNode block => new FlagStatementNode(Name: block.Identifier),
            var other => other,
        });
    }
    // Halves every numeric literal in place, keeping whatever digits the author wrote.
    private sealed class HalveKeepingRawText : PuckSyntaxRewriter {
        protected override ExpressionNode RewriteExpression(ExpressionNode expression) => (base.RewriteExpression(expression: expression) switch {
            LiteralExpressionNode literal when (literal.Value is double number) => (literal with { Value = (number / 2.0) }),
            var other => other,
        });
    }
    // Answers nothing for a rule scope's `when` header, which is an optional single position.
    private sealed class DropTheHeaderGate : PuckSyntaxRewriter {
        protected override StatementNode? RewriteStatement(StatementNode statement) => (base.RewriteStatement(statement: statement) switch {
            WhenStatementNode => null,
            var other => other,
        });
    }
    private sealed class HalveClearingRawText : PuckSyntaxRewriter {
        protected override ExpressionNode RewriteExpression(ExpressionNode expression) => (base.RewriteExpression(expression: expression) switch {
            LiteralExpressionNode literal when (literal.Value is double number) => (literal with { RawText = null, Value = (number / 2.0) }),
            var other => other,
        });
    }

    private const string Trivial = """
        schema: "puck.world.definition.v1"

        // a leading comment
        let speed = 4

        host {
          // inside the block
          width: speed
        }
        """;

    private static readonly Identity Nothing = new();

    private static DocumentNode Parse(string source, string label) {
        var parsed = PuckParser.ParseDocumentWithDiagnostics(source: source);

        Assert.True(
            condition: (parsed.Value is not null),
            userMessage: $"{label} does not parse:{Environment.NewLine}{parsed.Diagnostics.FormatReport(sourceText: source)}"
        );

        return parsed.Value!;
    }
    private static string Print(DocumentNode document) => PuckPrinter.Print(document: document);
    private static void AssertRewritingChangesNothing(string source, string label) {
        var printed = Print(document: Parse(
            label: label,
            source: source
        ));
        var rewritten = Print(document: Nothing.Rewrite(document: Parse(
            label: label,
            source: source
        )));

        Assert.Equal(
            actual: rewritten,
            expected: printed
        );
    }

    /// <summary>Returns every construct the registry names, so a verdict names one construct.</summary>
    /// <returns>The construct corpus as xUnit theory data.</returns>
    public static TheoryData<string> Constructs() => new(values: ConstructCorpus.Sources.Keys.Order(comparer: StringComparer.Ordinal));
    /// <summary>Returns every shipped world source.</summary>
    /// <returns>The source corpus as xUnit theory data.</returns>
    public static TheoryData<string> ShippedSources() => ShippedWorlds.Sources();
    [MemberData(nameof(Constructs))]
    [Theory]
    public void RewritingNothingChangesNothingForAConstruct(string construct) => AssertRewritingChangesNothing(
        label: construct,
        source: ConstructCorpus.Sources[key: construct]
    );
    [MemberData(nameof(ShippedSources))]
    [Theory]
    public void RewritingNothingChangesNothingForAShippedSource(string relativePath) => AssertRewritingChangesNothing(
        label: relativePath,
        source: File.ReadAllText(path: Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        ))
    );
    // Presence only, and only around nodes the rename did not touch. The strong statement — one textual
    // substitution accounts for the whole migrated file — is `PuckMigrationLawTests`'.
    [Fact]
    public void ARenameLeavesEveryCommentAndTheCompileTimeLayerInTheSource() {
        var rewritten = Print(document: new RenameIdentifier { From = "speed", To = "pace" }.Rewrite(document: Parse(
            label: "trivial",
            source: Trivial
        )));

        Assert.Contains(
            actualString: rewritten,
            expectedSubstring: "// a leading comment"
        );
        Assert.Contains(
            actualString: rewritten,
            expectedSubstring: "// inside the block"
        );
        Assert.Contains(
            actualString: rewritten,
            expectedSubstring: "width: pace"
        );
        // The compile-time layer is still source: the rewrite ran on the tree, never on a lowered document.
        Assert.Contains(
            actualString: rewritten,
            expectedSubstring: "let speed = 4"
        );
    }
    // The printer prefers a numeric literal's authored digits, so a rewrite that replaces the value and keeps them
    // would be discarded with no trace at all: the printed source would equal the original and the run would
    // report nothing to do.
    [Fact]
    public void ARewrittenLiteralMustPrintItsNewValue() {
        const string Source = "schema: \"puck.world.definition.v1\"\n\nlet ratio = 1.5\n";

        Assert.Throws<PuckRewriteException>(testCode: () => new HalveKeepingRawText().Rewrite(document: Parse(
            label: "ratio",
            source: Source
        )));
        Assert.Contains(
            actualString: Print(document: new HalveClearingRawText().Rewrite(document: Parse(
                label: "ratio",
                source: Source
            ))),
            expectedSubstring: "let ratio = 0.75"
        );
    }
    // A `when` on a rule scope's header is declared optional, so a rewrite that removes one is expressible rather
    // than a refusal: dropping a gate is a reshape a migration is expected to make.
    [Fact]
    public void AnOptionalStatementPositionCanBeDropped() {
        var printed = Print(document: new DropTheHeaderGate().Rewrite(document: Parse(
            label: "scope",
            source: "schema: \"puck.world.definition.v1\"\n\nstate {\n  world {\n    slot hp = 1\n  }\n}\n\nrules group when hp > 0 {\n  rule \"one\" {\n    hp = 1\n  }\n}\n"
        )));

        Assert.Contains(
            actualString: printed,
            expectedSubstring: "rules group {"
        );
        Assert.DoesNotContain(
            actualString: printed,
            expectedSubstring: "when hp > 0"
        );
    }
    [Fact]
    public void ARewriteThatEmptiesATypedPositionIsRefused() {
        var document = Parse(
            label: "template",
            source: "schema: \"puck.world.definition.v1\"\n\ntemplate t(a) {\n  host {\n    width: a\n  }\n}\n"
        );

        Assert.Throws<PuckRewriteException>(testCode: () => new ReplaceTemplateBody().Rewrite(document: document));
    }
}
