namespace Puck.State;

public static partial class ExpressionSpelling {
    /// <summary>Returns the spelling an operand reads back as the one whole name <paramref name="name"/>: the name
    /// bare where the bare form reads it back, and backquoted where the bare form would read something else — a
    /// reserved word, a number or a unit-bearing literal, text outside the identifier rule, or a dotted read of
    /// another row.</summary>
    /// <param name="name">The name.</param>
    /// <param name="quoted">Whether to backquote the name whatever the bare form would read: a name its author
    /// backquoted, or one that must read as the literal row that no binding, local or reserved word can
    /// capture.</param>
    /// <returns>The spelling. The backquoted form has no escape, so it reads back as
    /// <paramref name="name"/> exactly when <see cref="IsSpelledName"/> holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public static string PrintName(string name, bool quoted = false) {
        ArgumentNullException.ThrowIfNull(argument: name);

        return ((quoted || RowNameNeedsBackquoteForDotSafety(name: name) || !IsBareName(name: name))
            ? $"`{name}`"
            : name
        );
    }
    /// <summary>Returns the author's spelling of a parsed name: backquoted exactly where it was written backquoted
    /// (<see cref="PrintName(string, bool)"/>), and otherwise as written.</summary>
    /// <param name="name">The parsed name.</param>
    /// <returns>The spelling.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public static string PrintName(SourceName name) {
        ArgumentNullException.ThrowIfNull(argument: name);

        return (name.Quoted
            ? PrintName(
                name: name.Name,
                quoted: true
            )
            : name.Name
        );
    }
    /// <summary>Whether a name prints bare, without backquotes.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when the name lexes as one bare identifier.</returns>
    public static bool IsBareName(string name) =>
        ((ScanBareName(
            start: 0,
            text: name
        ) == name.Length) && (name.Length > 0) && !IsReservedName(name: name));
    /// <summary>Whether a name can be spelled at all: a name the bare form cannot carry is backquoted, and the
    /// backquoted form has no escape, so a name is spelled exactly when it is nonempty and holds no backquote. Every
    /// <see cref="CellName"/> is one; an expression operand naming anything else is refused where it is built.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when the name prints bare or backquoted and reads back as itself.</returns>
    public static bool IsSpelledName(string name) {
        ArgumentNullException.ThrowIfNull(argument: name);

        return ((name.Length > 0) && !name.Contains(value: '`'));
    }
    /// <summary>Whether this spelling reserves a name as a bare word: a function's name, a fold, a vector function, or
    /// a board query, each of which the parser reads as a call before it considers a row. A reserved name reads as a
    /// row only backquoted. Inside brackets no name is reserved, since a bare name there is a literal key.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when the bare name reads as something other than a row.</returns>
    public static bool IsReservedName(string name) => (Calls.ContainsKey(key: name) || IsReservedCallName(name: name));

    // A row name printed bare re-parses through ParsePrimary's own dot-access split, not through ParseKey (which
    // never splits): an unreserved name carrying a literal dot must print backquoted, or its printed form would
    // re-parse as a dotted read of a different row entirely, rather than the one whole name it started as.
    private static bool RowNameNeedsBackquoteForDotSafety(string name) => (!name.StartsWith(value: '$') && name.Contains(value: '.'));
}
