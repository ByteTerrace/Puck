using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Puck.Cli.Source;
using Puck.Cli.Scan.Analyzers;

namespace Puck.Cli.Scan;

internal static class InlineComments {
    // The comment trivia of one file in source order, as the tuple the block pass and the emitter share.
    public static List<(int StartLine, int EndLine, bool IsSingle, string Text)> CollectComments(ParsedFile parsed) {
        var comments = new List<(int, int, bool, string)>();

        foreach (var trivia in parsed.Root.DescendantTrivia()) {
            if (!CommentSmellClassifier.IsInlineComment(trivia: trivia)) {
                continue;
            }

            var isSingle = trivia.IsKind(kind: SyntaxKind.SingleLineCommentTrivia);

            var (startLine, endLine) = ScanJsonl.LineRange(location: trivia.GetLocation());

            comments.Add(item: (startLine, endLine, isSingle, trivia.ToString().Trim()));
        }

        return comments;
    }
}
