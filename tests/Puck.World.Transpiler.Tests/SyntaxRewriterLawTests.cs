using System.Reflection;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Rewriting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The rewrite law: a rewriter that overrides nothing is the identity, its descent reaches every node kind
/// the tree can carry, and a rewrite that touches one node leaves every other node's comments and blank lines
/// exactly where their author put them.</summary>
/// <remarks>It runs over every shipped <c>.puck</c> source and over one generated source per construct
/// <see cref="ConstructRegistry"/> registers, so a node kind the descent drops fails under its own name.</remarks>
public class SyntaxRewriterLawTests {
    // Exposes the descent so one node kind can be walked on its own, without a source that produces it.
    private sealed class Descent : PuckSyntaxRewriter {
        public SyntaxNode Walk(SyntaxNode node) => (node switch {
            DocumentNode document => this.DescendDocument(document: document),
            StatementNode statement => this.DescendStatement(statement: statement),
            ExpressionNode expression => this.DescendExpression(expression: expression),
            PredicateNode predicate => this.DescendPredicate(predicate: predicate),
            RhsNode value => this.DescendRhs(value: value),
            _ => this.DescendNode(node: node),
        });
    }
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
    // Every parameter a node's own primary constructor demands, with an optional one taken at its default: the
    // descent cares about a node's shape, never about whether the values in it mean anything.
    private static object? Seed(ParameterInfo parameter) {
        if (parameter.HasDefaultValue) {
            return parameter.DefaultValue;
        }

        var type = parameter.ParameterType;

        if (type == typeof(string)) {
            return "x";
        }
        if (type == typeof(PatternNode)) {
            return new PatternNode.Nothing();
        }
        if (typeof(SyntaxNode).IsAssignableFrom(c: type)) {
            return Instance(type: (type.IsAbstract
                ? Concrete(declared: type)
                : type
            ));
        }
        if (
            type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        ) {
            return Array.CreateInstance(
                elementType: type.GetGenericArguments()[0],
                length: 0
            );
        }
        if (type.IsEnum) {
            return Enum.GetValues(enumType: type).GetValue(index: 0);
        }

        return (Activator.CreateInstance(type: type) ?? throw new InvalidOperationException(message: $"no seed for {type.Name}"));
    }
    private static SyntaxNode Instance(Type type) {
        var constructor = type.GetConstructors().Single();

        return ((SyntaxNode)constructor.Invoke(parameters: [.. constructor.GetParameters().Select(selector: Seed)]));
    }
    private static Type Concrete(Type declared) => (declared switch {
        _ when (declared == typeof(ExpressionNode)) => typeof(LiteralExpressionNode),
        _ when (declared == typeof(PredicateNode)) => typeof(ComparisonPredicateNode),
        _ when (declared == typeof(RhsNode)) => typeof(RhsTextNode),
        _ when (declared == typeof(StatementNode)) => typeof(FlagStatementNode),
        _ when (declared == typeof(TestStepNode)) => typeof(TestTicksStepNode),
        _ => throw new InvalidOperationException(message: $"no concrete stand-in for {declared.Name}"),
    });
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
    /// <summary>Returns every concrete syntax node type the tree can carry.</summary>
    /// <returns>The node kinds as xUnit theory data.</returns>
    public static TheoryData<string> NodeKinds() => SyntaxNodeKinds.Names();
    /// <summary>Returns every shipped world source.</summary>
    /// <returns>The source corpus as xUnit theory data.</returns>
    public static TheoryData<string> ShippedSources() => ShippedWorlds.Sources();
    // A node kind with no descent arm never reaches a source-driven law until somebody authors a source that
    // produces it, so each kind is walked on its own.
    [MemberData(nameof(NodeKinds))]
    [Theory]
    public void TheDescentReachesEveryNodeKind(string kind) {
        var type = typeof(SyntaxNode).Assembly.GetType(name: kind)!;

        new Descent().Walk(node: Instance(type: type));
    }
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
