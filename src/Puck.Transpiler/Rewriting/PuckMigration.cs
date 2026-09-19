using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Rewriting;

/// <summary>One named rewrite over <c>.puck</c> source, applied to every source under a directory by
/// <c>puck migrate</c>.</summary>
/// <remarks>
/// <para>A migration lands with the reshape it serves and is deleted once it has run over the sources that needed
/// it, so nothing here is a compatibility path: the registry is expected to be empty between reshapes.</para>
/// <para><see cref="ReshapedMembers"/> is the migration's own claim about what its rewrite may change in the
/// compiled document. A caller compiles each source before and after and holds the migration to that claim: a
/// difference at a member the migration did not name is the migration's defect, not the source's.</para>
/// </remarks>
public abstract class PuckMigration {
    // Every comment a tree carries, in the descent's own order. The order is a function of the tree's shape alone,
    // so two trees of the same shape yield the same order and a rewrite that moved a comment reads as a difference.
    private sealed class Comments : PuckSyntaxRewriter {
        private readonly List<string> m_texts = [];

        private void Record(SyntaxNode node) {
            var trivia = node.Trivia;

            Append(pieces: trivia.Leading);
            Append(pieces: trivia.Header);

            if (trivia.Opening is { } opening) {
                m_texts.Add(item: opening);
            }

            Append(pieces: trivia.Inner);
            Append(pieces: trivia.Alternate);

            if (trivia.Trailing is { } trailing) {
                m_texts.Add(item: trailing);
            }

            void Append(IReadOnlyList<TriviaPiece> pieces) {
                foreach (var piece in pieces) {
                    if (piece.Kind == TriviaKind.Comment) {
                        m_texts.Add(item: piece.Text);
                    }
                }
            }
        }

        protected override DocumentNode RewriteDocument(DocumentNode document) {
            this.Record(node: document);

            return base.RewriteDocument(document: document);
        }
        protected override ExpressionNode RewriteExpression(ExpressionNode expression) {
            this.Record(node: expression);

            return base.RewriteExpression(expression: expression);
        }
        protected override SyntaxNode RewriteNode(SyntaxNode node) {
            this.Record(node: node);

            return base.RewriteNode(node: node);
        }
        protected override PredicateNode RewritePredicate(PredicateNode predicate) {
            this.Record(node: predicate);

            return base.RewritePredicate(predicate: predicate);
        }
        protected override RhsNode RewriteRhs(RhsNode value) {
            this.Record(node: value);

            return base.RewriteRhs(value: value);
        }
        protected override StatementNode? RewriteStatement(StatementNode statement) {
            this.Record(node: statement);

            return base.RewriteStatement(statement: statement);
        }

        public static IReadOnlyList<string> Of(DocumentNode document) {
            var reader = new Comments();

            reader.Rewrite(document: document);

            return reader.m_texts;
        }
    }

    private static string Join(string path, string segment) => (string.IsNullOrEmpty(value: path)
        ? segment
        : $"{path}/{segment}"
    );
    // A `/* … */` spans lines; a refusal is one line.
    private static string OneLine(string text) => text.ReplaceLineEndings(replacementText: " ");

    // A member the migration declares is not descended into at all: the whole subtree under it is the migration's
    // to reshape, so a difference deeper down is still covered by the declaration above it.
    private string? Difference(JsonNode? before, JsonNode? after, string path) {
        if (this.Reshapes(memberPath: path)) {
            return null;
        }
        if (
            (before is null) &&
            (after is null)
        ) {
            return null;
        }
        if (before is null) {
            return $"{path}: the migration added a member";
        }
        if (after is null) {
            return $"{path}: the migration removed a member";
        }
        if (before.GetValueKind() != after.GetValueKind()) {
            return $"{path}: the member became {after.GetValueKind()} from {before.GetValueKind()}";
        }

        if (
            (before is JsonObject beforeObject) &&
            (after is JsonObject afterObject)
        ) {
            foreach (var (key, value) in beforeObject) {
                if (this.Difference(
                    after: afterObject[propertyName: key],
                    before: value,
                    path: Join(
                        path: path,
                        segment: key
                    )
                ) is { } difference) {
                    return difference;
                }
            }
            foreach (var (key, value) in afterObject) {
                if (
                    !beforeObject.ContainsKey(propertyName: key) &&
                    (this.Difference(
                        after: value,
                        before: null,
                        path: Join(
                            path: path,
                            segment: key
                        )
                    ) is { } difference)
                ) {
                    return difference;
                }
            }

            return null;
        }

        if (
            (before is JsonArray beforeArray) &&
            (after is JsonArray afterArray)
        ) {
            if (beforeArray.Count != afterArray.Count) {
                return $"{path}: the member holds {afterArray.Count} entries where it held {beforeArray.Count}";
            }

            for (var index = 0; (index < beforeArray.Count); ++index) {
                if (this.Difference(
                    after: afterArray[index],
                    before: beforeArray[index],
                    path: Join(
                        path: path,
                        segment: index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
                    )
                ) is { } difference) {
                    return difference;
                }
            }

            return null;
        }

        return (JsonNode.DeepEquals(
            node1: before,
            node2: after
        )
            ? null
            : $"{path}: the value became {after.ToJsonString()} from {before.ToJsonString()}"
        );
    }

    /// <summary>Gets the name <c>puck migrate</c> selects this migration by.</summary>
    /// <remarks>Lower-case words joined by <c>-</c>, naming the reshape rather than the mechanism.</remarks>
    public abstract string Name { get; }
    /// <summary>Gets the members of the compiled document this rewrite may change, as <c>/</c>-separated paths from
    /// the document root, where a <c>*</c> segment matches any one object key or array index.</summary>
    /// <remarks>Empty means the rewrite may not change the document at all — the claim a rewrite of the
    /// compile-time layer alone makes, since that layer is evaluated away before the document exists. A path covers
    /// everything beneath it: <c>state/world/*/cells</c> admits a change to any cell under any row.</remarks>
    public abstract IReadOnlyList<string> ReshapedMembers { get; }
    /// <summary>Gets a value indicating whether this rewrite may remove, add, or reword the author's comments.</summary>
    /// <remarks>False unless a migration says otherwise, and a caller holds it to that: a comment that does not
    /// come back refuses the run. Comments are not in the compiled document, so
    /// <see cref="ReshapedMembers"/> can say nothing about them. Declare this only when moving a comment is part
    /// of the reshape.</remarks>
    public virtual bool ReshapesComments => false;
    /// <summary>Gets the one-line description an unknown-name listing prints.</summary>
    public abstract string Summary { get; }

    /// <summary>Returns <paramref name="document"/> rewritten by this migration.</summary>
    /// <param name="document">The parsed source tree, carrying the author's trivia.</param>
    /// <returns>The rewritten tree.</returns>
    /// <exception cref="PuckRewriteException">The rewrite answered a typed position with a node that position
    /// cannot hold.</exception>
    public abstract DocumentNode Apply(DocumentNode document);
    /// <summary>Returns a value indicating whether <paramref name="memberPath"/> lies at or beneath a member this
    /// migration declares it reshapes.</summary>
    /// <param name="memberPath">A <c>/</c>-separated path from the compiled document's root, with no leading
    /// separator.</param>
    /// <returns><see langword="true"/> when the path is covered by <see cref="ReshapedMembers"/>.</returns>
    public bool Reshapes(string memberPath) {
        ArgumentNullException.ThrowIfNull(argument: memberPath);

        var segments = memberPath.Split(separator: '/');

        foreach (var declared in this.ReshapedMembers) {
            var pattern = declared.Split(separator: '/');

            if (pattern.Length > segments.Length) {
                continue;
            }

            var covers = true;

            for (var index = 0; (index < pattern.Length); ++index) {
                if (
                    !string.Equals(
                        a: pattern[index],
                        b: "*",
                        comparisonType: StringComparison.Ordinal
                    ) &&
                    !string.Equals(
                        a: pattern[index],
                        b: segments[index],
                        comparisonType: StringComparison.Ordinal
                    )
                ) {
                    covers = false;
                    break;
                }
            }

            if (covers) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Returns the first difference between the document a source compiled to before this migration and
    /// the one it compiles to after, ignoring everything at or beneath a member <see cref="ReshapedMembers"/>
    /// declares.</summary>
    /// <param name="before">The document the source compiled to before the rewrite.</param>
    /// <param name="after">The document the rewritten source compiles to.</param>
    /// <returns>A one-line description naming the member, or <see langword="null"/> when the two documents differ
    /// only where this migration said they would.</returns>
    public string? UndeclaredDifference(JsonNode? before, JsonNode? after) => this.Difference(
        after: after,
        before: before,
        path: string.Empty
    );
    /// <summary>Returns the first comment this migration moved, dropped, added, or reworded, or
    /// <see langword="null"/> when every comment came back as its author wrote it — or when
    /// <see cref="ReshapesComments"/> says the migration may change them.</summary>
    /// <param name="before">The tree parsed from the source as its author left it.</param>
    /// <param name="after">The tree the migrated source parses back to.</param>
    /// <returns>A one-line description naming the comment, or <see langword="null"/>.</returns>
    /// <remarks>Comparing the two trees' comments in reading order is what the compiled-document comparison
    /// cannot do: trivia never reaches the document, so a rewrite that only loses a comment moves no member.</remarks>
    public string? UndeclaredCommentChange(DocumentNode before, DocumentNode after) {
        ArgumentNullException.ThrowIfNull(argument: before);
        ArgumentNullException.ThrowIfNull(argument: after);

        if (this.ReshapesComments) {
            return null;
        }

        var authored = Comments.Of(document: before);
        var migrated = Comments.Of(document: after);

        for (var index = 0; (index < Math.Min(val1: authored.Count, val2: migrated.Count)); ++index) {
            if (!string.Equals(
                a: authored[index],
                b: migrated[index],
                comparisonType: StringComparison.Ordinal
            )) {
                return $"the comment '{OneLine(text: authored[index])}' reads '{OneLine(text: migrated[index])}' after the rewrite";
            }
        }

        if (authored.Count > migrated.Count) {
            return $"the comment '{OneLine(text: authored[migrated.Count])}' is gone after the rewrite";
        }
        if (migrated.Count > authored.Count) {
            return $"the rewrite added the comment '{OneLine(text: migrated[authored.Count])}'";
        }

        return null;
    }
}
