using Microsoft.CodeAnalysis;

using Puck.Cli.Format.Rewriters;

namespace Puck.Cli.Format;

// One pass of the format pipeline. `Apply` is null for the semantic pass, which resolves symbols against
// a Compilation and therefore runs as its own disk phase instead of through the syntactic pipeline.
internal sealed record FormatPass(string Name, bool Default, Func<SyntaxNode, SyntaxNode>? Apply) {
    public bool Semantic => (Apply is null);
}

// The single pass table. The known-name set, the -Only error text, the syntactic pipeline and the
// bare-`format` default set all derive from it, so there is no second list to keep in agreement.
internal static class FormatPasses {
    // Canonical order — the order the syntactic pipeline runs in, and the order the error text lists.
    // `Default: true` marks the bare-`format` set: the semantics-preserving normalizers. The vertical
    // line-wrappers (logical-lines, arg-lines, ternary-lines) stay opt-in via -Only, because their
    // one-per-line layout is a deliberate choice rather than a baseline. named-args is in despite its
    // semantic cost. NOTE: the tree is not swept to these, so a bare `format`/`-WhatIf` reports (and
    // fixes) drift until a deliberate tree-wide run converts it.
    public static readonly FormatPass[] All = [
        new(Name: "attr-order", Default: true, Apply: static node => new AttrRewriter().Visit(node: node)!),
        new(Name: "member-spacing", Default: true, Apply: static node => new MemberSpacingRewriter().Visit(node: node)!),
        new(Name: "member-order", Default: true, Apply: static node => new MemberOrderRewriter().Visit(node: node)!),
        new(Name: "null-pattern", Default: true, Apply: static node => new NullPatternRewriter().Visit(node: node)!),
        new(Name: "paren-clarity", Default: true, Apply: static node => new ParenClarityRewriter().Visit(node: node)!),
        new(Name: "logical-lines", Default: false, Apply: static node => new LogicalLinesRewriter().Visit(node: node)!),
        new(Name: "arg-lines", Default: false, Apply: static node => new ArgLinesRewriter().Visit(node: node)!),
        new(Name: "ternary-lines", Default: false, Apply: static node => new TernaryLinesRewriter().Visit(node: node)!),
        new(Name: "init-order", Default: true, Apply: static node => new InitOrderRewriter().Visit(node: node)!),
        new(Name: "trailing-comma", Default: true, Apply: static node => new TrailingCommaRewriter().Visit(node: node)!),
        new(Name: "decl-spacing", Default: true, Apply: static node => new DeclSpacingRewriter().Visit(node: node)!),
        new(Name: "literal-var", Default: true, Apply: static node => new LiteralVarRewriter().Visit(node: node)!),
        new(Name: "named-args", Default: true, Apply: null),
    ];

    // The pass names in canonical order, for the unknown-pass error.
    public static string Names =>
        string.Join(separator: ", ", values: All.Select(selector: static pass => pass.Name));

    public static bool IsKnown(string name) =>
        All.Any(predicate: pass => string.Equals(a: pass.Name, b: name, comparisonType: StringComparison.Ordinal));

    // The names a bare `format` selects.
    public static HashSet<string> DefaultSelection() =>
        All.Where(predicate: static pass => pass.Default).Select(selector: static pass => pass.Name).ToHashSet(comparer: StringComparer.Ordinal);
}
