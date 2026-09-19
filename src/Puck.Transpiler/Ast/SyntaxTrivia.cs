namespace Puck.Transpiler.Ast;

/// <summary>What a piece of trivia is: a comment the author wrote, or the blank line they left.</summary>
public enum TriviaKind {
    /// <summary>One empty line; a run of them is one piece each, so the run keeps its length.</summary>
    BlankLine,
    /// <summary>A <c>//</c> line comment or a <c>/* … */</c> comment, carried verbatim including its delimiters.</summary>
    Comment,
}
/// <summary>One comment or blank line, in the order the author wrote it.</summary>
/// <param name="Kind">Whether the piece is a comment or a blank line.</param>
/// <param name="Text">The comment's own text including its delimiters; empty for a blank line.</param>
public sealed record TriviaPiece(TriviaKind Kind, string Text);
/// <summary>What a node carries for the printer rather than for the document: the comments and blank lines around
/// it, and whether its author spread it over several lines. Lowering never reads it, and it never changes what a
/// source means.</summary>
public sealed record SyntaxTrivia {
    /// <summary>The empty trivia every node starts with.</summary>
    public static readonly SyntaxTrivia None = new();

    /// <summary>The comments and blank lines standing before the node, in written order.</summary>
    public IReadOnlyList<TriviaPiece> Leading { get; init; } = [];
    /// <summary>The comments and blank lines standing between a container's last child and its closing delimiter.</summary>
    public IReadOnlyList<TriviaPiece> Inner { get; init; } = [];
    /// <summary>The same, for the closing delimiter of a node's SECOND body: an <c>else</c>, an <c>onFailure</c>.</summary>
    public IReadOnlyList<TriviaPiece> Alternate { get; init; } = [];
    /// <summary>The comments standing inside a construct's header, between its first token and its opening brace.</summary>
    public IReadOnlyList<TriviaPiece> Header { get; init; } = [];
    /// <summary>The comment the author left on the node's own last line, or <see langword="null"/>.</summary>
    public string? Trailing { get; init; }
    /// <summary>The comment standing on the same line as a container's opening delimiter, or <see langword="null"/>.</summary>
    public string? Opening { get; init; }
    /// <summary>Whether a <c>,</c> or <c>;</c> followed this node inside its container. The grammar treats one as
    /// optional whitespace, so it is the author's punctuation and nothing else's.</summary>
    public bool Separated { get; init; }
    /// <summary>Whether the author broke the node over several lines. An array or an object reads this to keep the
    /// shape it was written in; a node the author kept on one line prints on one line.</summary>
    public bool MultiLine { get; init; }
    /// <summary>Whether a line break stands between this node and whatever came before it inside its container. An
    /// element of a multi-line array reads this, so a board written eight to a row prints eight to a row.</summary>
    public bool OnNewLine { get; init; }
    /// <summary>Whether this trivia carries nothing at all, so a node holding it prints exactly as its shape says.</summary>
    public bool IsEmpty => (
        (Leading.Count == 0) &&
        (Inner.Count == 0) &&
        (Alternate.Count == 0) &&
        (Header.Count == 0) &&
        (Trailing is null) &&
        (Opening is null) &&
        !Separated &&
        !MultiLine
    );
}
