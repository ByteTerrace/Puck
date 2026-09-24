using System.Text;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The walk laws: <see cref="SyntaxWalk.Children"/> names every syntax-node child a node declares, and
/// <see cref="SyntaxWalk.PathAt"/> descends through every construct a position can stand in, so an editor service
/// that reads the path answers for a name wherever it is written.</summary>
public class SyntaxWalkLawTests {
    private const string GateSource = """
        template limit(n = 2) { width: n }
        rule "gate" {
            when limit(|n: 3)
            hp += 1
        }
        """;

    [Fact]
    public void ChildrenNamesEveryChildTheNodeDeclares() {
        var carriers = 0;
        var report = new StringBuilder();

        foreach (var kind in SyntaxNodeKinds.All()) {
            var planted = new List<SyntaxNode>();
            var root = SyntaxNodeKinds.Instance(
                planted: planted,
                type: kind
            );

            if (planted.Count == 0) {
                continue;
            }

            ++carriers;

            var children = SyntaxWalk.Children(node: root);

            foreach (var child in planted) {
                if (!children.Any(predicate: seen => ReferenceEquals(objA: seen, objB: child))) {
                    report.AppendLine(value: $"{kind.Name}: its '{child.GetType().Name}' child is not among its children");
                }
            }
        }

        Assert.Equal(
            actual: report.ToString(),
            expected: string.Empty
        );
        Assert.True(
            condition: (carriers > 30),
            userMessage: $"only {carriers} node kinds carried a child, so the law covered almost nothing"
        );
    }
    [Fact]
    public void PathAtDescendsThroughAWhenClause() {
        var cursor = MarkedSource.Parse(marked: GateSource);
        var document = PuckParser.ParseDocumentWithDiagnostics(
            source: cursor.Text,
            vocabulary: WorldDocumentVocabulary.Instance
        ).Value;

        Assert.NotNull(@object: document);

        var path = SyntaxWalk.PathAt(
            offset: GateSource.IndexOf(value: '|'),
            root: document
        );

        Assert.Equal(
            actual: path.Select(selector: static node => node.GetType().Name),
            expected: [nameof(DocumentNode), nameof(RuleBlockNode), nameof(WhenStatementNode), nameof(CallPredicateNode), nameof(CallExpressionNode), nameof(ArgumentNode)]
        );
        // Every node on the path lies inside the one before it.
        for (var index = 1; (index < path.Count); index++) {
            Assert.InRange(actual: path[index].Offset, high: (path[(index - 1)].Offset + path[(index - 1)].Length), low: path[(index - 1)].Offset);
            Assert.InRange(actual: (path[index].Offset + path[index].Length), high: (path[(index - 1)].Offset + path[(index - 1)].Length), low: path[index].Offset);
        }
    }
    // A conditional's arms are its children, so an editor service reaches a name written in either arm.
    [Fact]
    public async Task HoverResolvesANameInsideAConditionalsArm() {
        const string Source = """
            template limit(n = 2) { width: n }
            value: 1 > 0 ? 0 : limit(|n: 3)
            """;
        var cursor = MarkedSource.Parse(marked: Source);
        var document = PuckParser.ParseDocumentWithDiagnostics(
            source: cursor.Text,
            vocabulary: WorldDocumentVocabulary.Instance
        ).Value!;

        Assert.Contains(
            collection: SyntaxWalk.PathAt(offset: Source.IndexOf(value: '|'), root: document),
            filter: static node => (node is ConditionalExpressionNode)
        );

        var text = await LanguageServerClient.HoverAsync(markedSource: Source);

        Assert.NotNull(@object: text);
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "n — template parameter"
        );
    }
    [Fact]
    public void PathAtIsEmptyOutsideTheRoot() {
        var document = PuckParser.ParseDocumentWithDiagnostics(
            source: "let seats = 4\n",
            vocabulary: WorldDocumentVocabulary.Instance
        ).Value!;

        Assert.Empty(collection: SyntaxWalk.PathAt(offset: -1, root: document));
        Assert.Empty(collection: SyntaxWalk.PathAt(offset: (document.Offset + document.Length), root: document));
    }
    // The argument name of a template call written as a rule's gate resolves to the template's formal parameter, which
    // hover can only see by descending into the `when` clause that holds the call.
    [Fact]
    public async Task HoverResolvesANameInsideAWhenClause() {
        var text = await LanguageServerClient.HoverAsync(markedSource: GateSource);

        Assert.NotNull(@object: text);
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "n — template parameter"
        );
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "n = 2"
        );
    }
}
