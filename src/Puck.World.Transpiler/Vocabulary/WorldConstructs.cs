namespace Puck.World.Transpiler.Vocabulary;

/// <summary>The one description of <c>puck.world.definition.v1</c>'s authoring vocabulary: every construct's
/// keyword, members, document member, and printing requirement, read by the parser's embedded-language test, the
/// printer's sugar guards, the language server's completion and hover, and the manual's vocabulary tables.</summary>
/// <remarks>This describes the surface and its mapping onto the document. What a document field means is
/// <c>puck schema</c>'s, which fields carry names is <c>puck registry</c>'s, and which polymorphic <c>$type</c>
/// arms exist is the arms' own <c>JsonDerivedType</c> attributes; a row here maps onto those rather than
/// restating them.</remarks>
public static partial class WorldConstructs {
    // A construct is admitted here rather than described only when the table's own shape cannot carry it —
    // core grammar the vocabulary never interprets, or a shape with no keyword to key on.
    private static IReadOnlyList<WorldConstructExclusion> Excluded() => [
        new(Keyword: "+=", Reason: "An assignment, so there is no keyword for a reader, a diagnostic or this table to key on; lowers to `addState`."),
        new(Keyword: "=", Reason: "An assignment, so there is no keyword for a reader, a diagnostic or this table to key on; lowers to `setState`."),
        new(Keyword: "basis", Reason: "Core grammar: a document header this vocabulary does not interpret."),
        new(Keyword: "export", Reason: "Core grammar: the module surface, described by `Puck.Transpiler`."),
        new(Keyword: "for", Reason: "Core grammar: the compile-time layer, evaluated away before this vocabulary sees a statement."),
        new(Keyword: "import", Reason: "Core grammar: the module surface, described by `Puck.Transpiler`."),
        new(Keyword: "let", Reason: "Core grammar: the compile-time layer, evaluated away before this vocabulary sees a statement."),
        new(Keyword: "schema", Reason: "Core grammar: a document header this vocabulary does not interpret."),
        new(Keyword: "template", Reason: "Core grammar: the compile-time layer, evaluated away before this vocabulary sees a statement."),
    ];

    /// <summary>Gets the described constructs.</summary>
    public static WorldConstructTable Table { get; } = new(
        constructs: [.. Sections(), .. State(), .. Rules(), .. Tests()],
        excluded: Excluded()
    );
}
