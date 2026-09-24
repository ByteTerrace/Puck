using Syntax = Puck.State.ExpressionSpelling;

namespace Puck.Transpiler.Ast;

/// <summary>A dotted reference as an author writes it: a head name and the members read through it, such as a module
/// instance's row (<c>box.child.score</c>), an import's export (<c>alias.name</c>), an enum member
/// (<c>Suit.hearts</c>), a pool binding's field (<c>card.rank</c>) or a composition endpoint
/// (<c>hub.floor.east</c>).</summary>
/// <remarks>This is the one place a dotted name is split into its segments or joined from them, and the one reading
/// of a member-access chain — in the document grammar (<see cref="MemberAccessExpressionNode"/>) and in the operand
/// grammar (<see cref="Syntax.SourceAccess"/>) — as the name it spells. A segment may be empty, as the text it was
/// read from may be; <see cref="IsWellFormed"/> says whether none is.</remarks>
public sealed class QualifiedName {
    /// <summary>The character that joins a qualified name's segments.</summary>
    public const char Separator = '.';

    private readonly string[] m_segments;

    private QualifiedName(string[] segments) {
        m_segments = segments;
    }

    /// <summary>Gets the name's segments, head first; there is always at least one.</summary>
    public IReadOnlyList<string> Segments => m_segments;
    /// <summary>Gets the first segment.</summary>
    public string Head => m_segments[0];
    /// <summary>Gets the last segment: the member the name finally reads, or the head of an unqualified name.</summary>
    public string Last => m_segments[^1];
    /// <summary>Gets whether the name has more than one segment.</summary>
    public bool IsQualified => (m_segments.Length > 1);
    /// <summary>Gets whether every segment is nonempty.</summary>
    public bool IsWellFormed => m_segments.All(predicate: static segment => (segment.Length > 0));
    /// <summary>Gets the segments after the head, joined; empty for an unqualified name.</summary>
    public string Tail => string.Join(separator: Separator, values: m_segments.Skip(count: 1));
    /// <summary>Gets the segments before the last, joined; empty for an unqualified name.</summary>
    public string Qualifier => string.Join(separator: Separator, values: m_segments.Take(count: (m_segments.Length - 1)));

    /// <summary>Reads a dotted name's segments.</summary>
    /// <param name="text">The name as written.</param>
    /// <returns>The name; one with no separator has one segment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static QualifiedName Parse(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        return new QualifiedName(segments: text.Split(separator: Separator));
    }
    /// <summary>Returns the name a document-grammar member-access chain over one identifier spells
    /// (<c>box.child.score</c>).</summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The name, or <see langword="null"/> for an expression that is neither an identifier nor a member access
    /// of one.</returns>
    public static QualifiedName? From(ExpressionNode expression) => expression switch {
        IdentifierExpressionNode identifier => Parse(text: identifier.Name),
        MemberAccessExpressionNode access when (From(expression: access.Target) is { } target) => target.Append(member: access.Member),
        _ => null,
    };
    /// <summary>Returns the name an operand-grammar access chain over one unquoted name spells.</summary>
    /// <param name="syntax">The operand syntax.</param>
    /// <returns>The name, or <see langword="null"/> for syntax that is neither an unquoted name nor an access of
    /// one.</returns>
    public static QualifiedName? From(Syntax.SyntaxNode syntax) => syntax switch {
        Syntax.SourceName { Quoted: false } name => Parse(text: name.Name),
        Syntax.SourceAccess access when (From(syntax: access.Target) is { } target) => target.Append(member: access.Member),
        _ => null,
    };
    /// <summary>Returns this name with one more member read through it.</summary>
    /// <param name="member">The member.</param>
    /// <returns>The longer name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> is <see langword="null"/>.</exception>
    public QualifiedName Append(string member) {
        ArgumentNullException.ThrowIfNull(argument: member);

        return new QualifiedName(segments: [.. m_segments, .. member.Split(separator: Separator)]);
    }
    /// <summary>Returns the operand syntax that reads this name: the head as a name, each member as an access of the
    /// one before it.</summary>
    /// <returns>The syntax.</returns>
    public Syntax.SyntaxNode ToSyntax() {
        Syntax.SyntaxNode result = new Syntax.SourceName(Name: Head);

        foreach (var member in m_segments.Skip(count: 1)) {
            result = new Syntax.SourceAccess(Member: member, Target: result);
        }

        return result;
    }
    /// <summary>Returns the name with each segment spelled by <paramref name="segment"/>.</summary>
    /// <param name="segment">Spells one segment.</param>
    /// <returns>The spelled name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> is <see langword="null"/>.</exception>
    public string Print(Func<string, string> segment) {
        ArgumentNullException.ThrowIfNull(argument: segment);

        return string.Join(separator: Separator, values: m_segments.Select(selector: segment));
    }
    /// <summary>Returns the name as written: its segments joined by <see cref="Separator"/>.</summary>
    /// <returns>The dotted name.</returns>
    public override string ToString() => string.Join(separator: Separator, values: m_segments);
}
