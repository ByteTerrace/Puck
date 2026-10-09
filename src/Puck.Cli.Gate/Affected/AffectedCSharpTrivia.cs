using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Affected;

/// <summary>Recognizes C# edits whose syntax and executable directives stay unchanged, without a base build.</summary>
public static class AffectedCSharpTrivia {
    private static readonly CSharpParseOptions Options = new(languageVersion: LanguageVersion.Preview);

    /// <summary>Compares a source present in both trees. Conditional compilation is deliberately unjudged because
    /// parsing without the project's symbols cannot see every token that its build compiles.</summary>
    public static bool IsUnchanged(string path, IAffectedTree before, IAffectedTree after) {
        if (!path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".cs") ||
            (before.ReadText(path: path) is not { } oldText) || (after.ReadText(path: path) is not { } newText)) {
            return false;
        }

        var oldRoot = CSharpSyntaxTree.ParseText(text: oldText, options: Options).GetRoot();
        var newRoot = CSharpSyntaxTree.ParseText(text: newText, options: Options).GetRoot();

        if (oldRoot.GetDiagnostics().Concat(second: newRoot.GetDiagnostics()).Any(predicate: diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error))) {
            return false;
        }

        var oldDirectives = Directives(root: oldRoot);
        var newDirectives = Directives(root: newRoot);

        if (oldDirectives.Concat(second: newDirectives).Any(predicate: directive => (directive is ConditionalDirectiveTriviaSyntax or ElseDirectiveTriviaSyntax or EndIfDirectiveTriviaSyntax))) {
            return false;
        }

        return (SyntaxFactory.AreEquivalent(newNode: newRoot, oldNode: oldRoot, topLevel: false) &&
            (oldDirectives.Length == newDirectives.Length) &&
            oldDirectives.Zip(second: newDirectives).All(predicate: pair => SyntaxFactory.AreEquivalent(newNode: pair.Second, oldNode: pair.First, topLevel: false)));
    }

    // Regions only group editor text; every other directive keeps its tokens in the comparison.
    private static DirectiveTriviaSyntax[] Directives(SyntaxNode root) => [.. root.DescendantTrivia(descendIntoTrivia: true)
        .Select(selector: trivia => trivia.GetStructure()).OfType<DirectiveTriviaSyntax>()
        .Where(predicate: directive => (directive is not (RegionDirectiveTriviaSyntax or EndRegionDirectiveTriviaSyntax)))];
}
