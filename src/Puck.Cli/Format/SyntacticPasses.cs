using Microsoft.CodeAnalysis;

using Puck.Cli.Format.Rewriters;

namespace Puck.Cli.Format;

// The rewriter behind each syntactic pass in FormatPasses, kept apart from the table so naming a pass loads no Roslyn.
internal static class SyntacticPasses {
    // Rewrites one syntax tree by the named syntactic pass.
    public static SyntaxNode Apply(this FormatPass pass, SyntaxNode node) => pass.Name switch {
        "attr-order" => new AttrRewriter().Visit(node: node)!,
        "member-groups" => new MemberGroupsRewriter().Visit(node: node)!,
        "member-spacing" => new MemberSpacingRewriter().Visit(node: node)!,
        "member-order" => new MemberOrderRewriter().Visit(node: node)!,
        "string-merge" => new StringMergeRewriter().Visit(node: node)!,
        "paren-clarity" => new ParenClarityRewriter().Visit(node: node)!,
        "logical-lines" => new LogicalLinesRewriter().Visit(node: node)!,
        "arg-lines" => new ArgLinesRewriter().Visit(node: node)!,
        "ternary-lines" => new TernaryLinesRewriter().Visit(node: node)!,
        "init-order" => new InitOrderRewriter().Visit(node: node)!,
        "trailing-comma" => new TrailingCommaRewriter().Visit(node: node)!,
        "decl-spacing" => new DeclSpacingRewriter().Visit(node: node)!,
        "literal-var" => new LiteralVarRewriter().Visit(node: node)!,
        _ => throw new ArgumentException(message: $"'{pass.Name}' is not a syntactic format pass."),
    };
}
