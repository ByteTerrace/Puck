using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Format.Rewriters;

// The literal-var normalizer (the `literal-var` pass): an explicitly-typed local initialized from a bare
// numeric literal becomes `var` with the literal carrying the matching type suffix, so the type is
// stated ONCE and inferred — `uint executableCount = 0;` -> `var executableCount = 0U;`. Only the
// suffix-bearing primitives are converted (uint -> U, long -> L, ulong -> UL, float -> F, double -> D,
// decimal -> M); the suffix makes `var` infer the original type exactly. Left alone: `const`/`using`
// declarations, multi-declarator statements, non-literal or already-suffixed initializers, and
// hex/binary literals (whose trailing letters are digits, not a suffix). After conversion the type is
// `var`, so the pass is idempotent.
internal sealed class LiteralVarRewriter : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node) {
        var visited = ((LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node: node)!);

        if (!visited.UsingKeyword.IsKind(kind: SyntaxKind.None)
            || visited.Modifiers.Any(predicate: static modifier => modifier.IsKind(kind: SyntaxKind.ConstKeyword))) {
            return visited;
        }

        var declaration = visited.Declaration;

        if ((declaration.Type is not PredefinedTypeSyntax predefined)
            || (SuffixFor(keyword: predefined.Keyword.Kind()) is not { } suffix)
            || (declaration.Variables.Count != 1)) {
            return visited;
        }

        var variable = declaration.Variables[0];

        if ((variable.Initializer?.Value is not LiteralExpressionSyntax literal)
            || !literal.Token.IsKind(kind: SyntaxKind.NumericLiteralToken)) {
            return visited;
        }

        var literalText = literal.Token.Text;

        if (literalText.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "0x")
            || literalText.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "0b")
            || HasTypeSuffix(text: literalText)
            || (SyntaxFactory.ParseExpression(text: (literalText + suffix)) is not LiteralExpressionSyntax suffixed)) {
            return visited;
        }

        var newLiteral = suffixed
            .WithLeadingTrivia(trivia: literal.GetLeadingTrivia())
            .WithTrailingTrivia(trivia: literal.GetTrailingTrivia());
        var newVariable = variable.WithInitializer(initializer: variable.Initializer!.WithValue(value: newLiteral));
        var newType = SyntaxFactory.IdentifierName(name: "var")
            .WithLeadingTrivia(trivia: predefined.GetLeadingTrivia())
            .WithTrailingTrivia(trivia: predefined.GetTrailingTrivia());

        return visited.WithDeclaration(
            declaration: declaration
                .WithType(type: newType)
                .WithVariables(variables: SyntaxFactory.SingletonSeparatedList(node: newVariable)));
    }

    private static string? SuffixFor(SyntaxKind keyword) => keyword switch {
        SyntaxKind.UIntKeyword => "U",
        SyntaxKind.LongKeyword => "L",
        SyntaxKind.ULongKeyword => "UL",
        SyntaxKind.FloatKeyword => "F",
        SyntaxKind.DoubleKeyword => "D",
        SyntaxKind.DecimalKeyword => "M",
        _ => null
    };
    private static bool HasTypeSuffix(string text) =>
        ((text.Length > 0) && (text[^1] is 'u' or 'U' or 'l' or 'L' or 'f' or 'F' or 'd' or 'D' or 'm' or 'M'));
}
