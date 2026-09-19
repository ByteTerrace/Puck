namespace Puck.World.Transpiler.Vocabulary;

// The projections the emitter and the printer take of the shipped table. A lookup that names a construct or a
// member the table does not describe throws rather than folding to an empty set: an empty admitted-key set would
// silently refuse every document, and an empty required-key set would silently sugar every node.
public static partial class WorldConstructs {
    private static WorldConstruct Require(string? enclosing, string keyword) => (Table.TryGet(
        construct: out var construct,
        enclosing: enclosing,
        keyword: keyword
    )
        ? construct!
        : throw new InvalidOperationException(message: $"'{keyword}' is not a construct described inside {(enclosing is null
            ? "the document"
            : $"'{enclosing}'")}.")
    );

    /// <summary>Returns the words a described member admits, as an ordinal set.</summary>
    /// <param name="enclosing">The enclosing construct's keyword, or <see langword="null"/> for the document's own
    /// root.</param>
    /// <param name="keyword">The construct's keyword.</param>
    /// <param name="member">The member's authored spelling.</param>
    /// <returns>The member's admitted words.</returns>
    /// <exception cref="InvalidOperationException">The construct or the member is not described, or the member
    /// admits no fixed set of words.</exception>
    public static HashSet<string> Choices(string? enclosing, string keyword, string member) => (Require(enclosing: enclosing, keyword: keyword).TryGetMember(
        member: out var described,
        name: member
    ) && (described!.Choices.Count > 0)
        ? new HashSet<string>(collection: described.Choices, comparer: StringComparer.Ordinal)
        : throw new InvalidOperationException(message: $"'{keyword}' describes no '{member}' member with a fixed set of words.")
    );
    /// <summary>Returns the modifiers one entry of a described construct's cell body admits.</summary>
    /// <param name="enclosing">The enclosing construct's keyword, or <see langword="null"/> for the document's own
    /// root.</param>
    /// <param name="keyword">The construct's keyword.</param>
    /// <returns>The admitted cell modifier names, as an ordinal set.</returns>
    /// <exception cref="InvalidOperationException">The construct is not described.</exception>
    public static HashSet<string> CellModifiersOf(string? enclosing, string keyword) =>
        new(collection: Require(enclosing: enclosing, keyword: keyword).CellModifierNames, comparer: StringComparer.Ordinal);
    /// <summary>Returns the keys one entry of a described construct's cell body can carry.</summary>
    /// <param name="enclosing">The enclosing construct's keyword, or <see langword="null"/> for the document's own
    /// root.</param>
    /// <param name="keyword">The construct's keyword.</param>
    /// <returns>The admitted cell keys, as an ordinal set.</returns>
    /// <exception cref="InvalidOperationException">The construct is not described.</exception>
    public static HashSet<string> CellKeysOf(string? enclosing, string keyword) =>
        new(collection: Require(enclosing: enclosing, keyword: keyword).CellKeys, comparer: StringComparer.Ordinal);
    /// <summary>Returns the modifiers a described construct's header admits.</summary>
    /// <param name="enclosing">The enclosing construct's keyword, or <see langword="null"/> for the document's own
    /// root.</param>
    /// <param name="keyword">The construct's keyword.</param>
    /// <returns>The admitted modifier names, as an ordinal set.</returns>
    /// <exception cref="InvalidOperationException">The construct is not described.</exception>
    public static HashSet<string> ModifiersOf(string? enclosing, string keyword) =>
        new(collection: Require(enclosing: enclosing, keyword: keyword).ModifierNames, comparer: StringComparer.Ordinal);
    /// <summary>Returns the keys a described construct's spelling can carry on a document member other than its
    /// own — a <c>grid</c>'s shape modifiers on the topology it mints.</summary>
    /// <param name="enclosing">The enclosing construct's keyword, or <see langword="null"/> for the document's own
    /// root.</param>
    /// <param name="keyword">The construct's keyword.</param>
    /// <param name="documentNode">The other document member, as <see cref="WorldConstruct.DocumentMember"/>
    /// spells it.</param>
    /// <returns>The keys those members fill, as an ordinal set.</returns>
    /// <exception cref="InvalidOperationException">The construct is not described, or it describes no member on
    /// that document member.</exception>
    public static HashSet<string> KeysOn(string? enclosing, string keyword, string documentNode) {
        var keys = Require(enclosing: enclosing, keyword: keyword).Members
            .Where(predicate: member => string.Equals(
            a: member.DocumentNode,
            b: documentNode,
            comparisonType: StringComparison.Ordinal
        ))
            .SelectMany(selector: static member => member.DocumentKeys);
        var set = new HashSet<string>(
            collection: keys,
            comparer: StringComparer.Ordinal
        );

        return ((set.Count > 0)
            ? set
            : throw new InvalidOperationException(message: $"'{keyword}' describes no member on '{documentNode}'.")
        );
    }
    /// <summary>Returns the keys a described construct's spelling can carry on its own document node.</summary>
    /// <param name="enclosing">The enclosing construct's keyword, or <see langword="null"/> for the document's own
    /// root.</param>
    /// <param name="keyword">The construct's keyword.</param>
    /// <returns>The admitted keys, as an ordinal set.</returns>
    /// <exception cref="InvalidOperationException">The construct is not described.</exception>
    /// <remarks>A node carrying a key outside this set has no spelling and takes the construct's recorded
    /// fallback.</remarks>
    public static HashSet<string> NodeKeysOf(string? enclosing, string keyword) =>
        new(collection: Require(enclosing: enclosing, keyword: keyword).NodeKeys, comparer: StringComparer.Ordinal);
}
