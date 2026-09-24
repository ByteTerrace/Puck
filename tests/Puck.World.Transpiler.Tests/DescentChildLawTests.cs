using System.Text;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Rewriting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The child law: every descent arm rebuilds every syntax-node child its own node declares. A node type
/// gaining a child that its arm does not rewrite fails here under the node's name, which is the only thing that
/// stops a new structured member from being skipped silently.</summary>
/// <remarks>Distinct from <c>TheDescentReachesEveryNodeKind</c>, which walks each node with empty children and so
/// proves only that an arm exists. The members that carry a sub-grammar as a <see cref="string"/> rather than as a
/// child — <c>EmbeddedBlockNode.Body</c>, the operand spans, a pattern's <c>Match</c> — are outside both laws by
/// construction, and the manual's rewriting section names them.</remarks>
public class DescentChildLawTests {
    // Records every node the descent hands to a hook, so the law can ask afterwards whether each planted child
    // arrived.
    private sealed class Recorder : PuckSyntaxRewriter {
        private readonly List<SyntaxNode> m_seen = [];

        protected override DocumentNode RewriteDocument(DocumentNode document) {
            m_seen.Add(item: document);

            return base.RewriteDocument(document: document);
        }
        protected override ExpressionNode RewriteExpression(ExpressionNode expression) {
            m_seen.Add(item: expression);

            return base.RewriteExpression(expression: expression);
        }
        protected override SyntaxNode RewriteNode(SyntaxNode node) {
            m_seen.Add(item: node);

            return base.RewriteNode(node: node);
        }
        protected override PredicateNode RewritePredicate(PredicateNode predicate) {
            m_seen.Add(item: predicate);

            return base.RewritePredicate(predicate: predicate);
        }
        protected override RhsNode RewriteRhs(RhsNode value) {
            m_seen.Add(item: value);

            return base.RewriteRhs(value: value);
        }
        protected override StatementNode? RewriteStatement(StatementNode statement) {
            m_seen.Add(item: statement);

            return base.RewriteStatement(statement: statement);
        }

        public bool Saw(SyntaxNode node) => m_seen.Any(predicate: seen => ReferenceEquals(
            objA: seen,
            objB: node
        ));
        public void Walk(SyntaxNode node) {
            switch (node) {
                case DocumentNode document: {
                        this.DescendDocument(document: document);

                        break;
                    }
                case StatementNode statement: {
                        this.DescendStatement(statement: statement);

                        break;
                    }
                case ExpressionNode expression: {
                        this.DescendExpression(expression: expression);

                        break;
                    }
                case PredicateNode predicate: {
                        this.DescendPredicate(predicate: predicate);

                        break;
                    }
                case RhsNode value: {
                        this.DescendRhs(value: value);

                        break;
                    }
                default: {
                        this.DescendNode(node: node);

                        break;
                    }
            }
        }
    }

    [Fact]
    public void EveryDescentArmRebuildsEveryChildTheNodeDeclares() {
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

            var recorder = new Recorder();

            recorder.Walk(node: root);

            foreach (var child in planted) {
                if (!recorder.Saw(node: child)) {
                    report.AppendLine(value: $"{kind.Name}: its '{child.GetType().Name}' child was never handed to a hook");
                }
            }
        }

        Assert.Equal(
            actual: report.ToString(),
            expected: string.Empty
        );
        // A seeder that silently stopped filling child positions would make the law vacuous.
        Assert.True(
            condition: (carriers > 30),
            userMessage: $"only {carriers} node kinds carried a child, so the law covered almost nothing"
        );
    }
}
