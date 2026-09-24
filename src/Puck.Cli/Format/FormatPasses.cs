namespace Puck.Cli.Format;

// How a pass runs. A syntactic pass rewrites one C# syntax tree at a time through the re-parsing pipeline; a semantic
// pass resolves symbols against a project compilation and runs as its own disk phase; the .puck pass parses and
// prints each .puck source.
internal enum FormatPassKind {
    Syntactic,
    Semantic,
    Puck,
}
// One pass of the format pipeline, named as --only spells it.
internal sealed record FormatPass(string Name, bool Default, FormatPassKind Kind) {
    public bool Semantic => (Kind == FormatPassKind.Semantic);
    public bool Syntactic => (Kind == FormatPassKind.Syntactic);
}
// The single pass table. The known-name set, the --only error text, the syntactic pipeline and the bare-`format`
// default set all derive from it, so there is no second list to keep in agreement. It names passes and carries no
// Roslyn type, so building the command tree (and printing its help) loads no compiler: SyntacticPasses maps a name
// to its rewriter.
internal static class FormatPasses {
    // Canonical order — the order the syntactic pipeline runs in, and the order the error text lists.
    // `Default: true` marks the bare-`format` set: the baseline house normalizers. The vertical
    // line-wrappers (logical-lines, arg-lines, ternary-lines) stay opt-in via --only, because their
    // one-per-line layout is a deliberate choice rather than a baseline. named-args is in despite its
    // semantic cost.
    public static readonly FormatPass[] All = [
        new(
            Default: true,
            Kind: FormatPassKind.Syntactic,
            Name: "attr-order"
        ),
        new(
            Default: false,
            Kind: FormatPassKind.Syntactic,
            Name: "member-groups"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Syntactic,
            Name: "member-spacing"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Syntactic,
            Name: "member-order"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Semantic,
            Name: "null-pattern"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Syntactic,
            Name: "string-merge"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Syntactic,
            Name: "paren-clarity"
        ),
        new(
            Default: false,
            Kind: FormatPassKind.Syntactic,
            Name: "logical-lines"
        ),
        new(
            Default: false,
            Kind: FormatPassKind.Syntactic,
            Name: "arg-lines"
        ),
        new(
            Default: false,
            Kind: FormatPassKind.Syntactic,
            Name: "ternary-lines"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Syntactic,
            Name: "init-order"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Syntactic,
            Name: "trailing-comma"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Syntactic,
            Name: "decl-spacing"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Syntactic,
            Name: "literal-var"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Semantic,
            Name: "named-args"
        ),
        new(
            Default: true,
            Kind: FormatPassKind.Puck,
            Name: "puck"
        ),
    ];

    // The pass names in canonical order, for help and the unknown-pass error.
    public static string Names =>
        string.Join(
            separator: ", ",
            values: All.Select(selector: static pass => pass.Name)
        );

    // The names a bare `format` selects.
    public static HashSet<string> DefaultSelection() =>
        All.Where(predicate: static pass => pass.Default).Select(selector: static pass => pass.Name).ToHashSet(comparer: StringComparer.Ordinal);
    public static bool IsKnown(string name) =>
        All.Any(predicate: pass => string.Equals(
            a: pass.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));
}
