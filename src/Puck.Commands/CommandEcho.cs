using System.Buffers;
using System.Globalization;
using System.Text;

namespace Puck.Commands;

/// <summary>
/// Builds the bracketed console echo line a read-back/mutation verb emits — <c>[verb: key=value key=value | head
/// field=value field=value]</c> — so the envelope and segment separator are defined once rather than hand-spelled per
/// verb. A segment (the content between two <see cref="Segment"/> boundaries) is one of three shapes: a run of
/// <see cref="Field(string, string)"/> <c>key=value</c> tokens; one <see cref="Head(string)"/> token — a declared,
/// non-<c>key=value</c> first word naming what the segment describes (<c>kind</c>, <c>group</c>, <c>listing</c>) —
/// followed by zero or more qualifying <see cref="Field(string, string)"/> tokens; or, for content that is not meant
/// to be machine-parsed as a segment at all, one <see cref="Text(string)"/> call carrying a free-text sentence.
/// <see cref="Open(string)"/> starts the line; <see cref="Segment"/> marks a boundary between segments (a
/// <c>" | "</c> separator, written only when further content follows — a trailing <see cref="Segment"/> before
/// <see cref="Close"/> is dropped, so a trailing separator is impossible by construction); <see cref="Close"/> yields
/// the finished string. Mutable and not thread-safe: one instance builds one line.
/// </summary>
/// <remarks>
/// <para><b>Quoting.</b> The grammar reserves four characters: whitespace separates tokens, <c>'|'</c> separates
/// segments, <c>']'</c> closes the envelope, and <c>'"'</c> opens a quoted run. A value carrying any of them would
/// otherwise let a scripted driver's split land inside it — <c>path=C:\my games</c> reads as two tokens,
/// <c>members=[seat1]</c> closes the envelope early — so <see cref="Field(string, string)"/> and
/// <see cref="SpliceTag(string, string)"/> route their value through <see cref="Quote(string)"/> first: a value
/// holding a reserved character is emitted as a double-quoted run with <c>'\'</c> and <c>'"'</c> backslash-escaped
/// inside it, and every other value is emitted verbatim. A driver undoes it in one pass — a token that starts with
/// <c>'"'</c> runs to the next unescaped <c>'"'</c>, and <c>\x</c> inside it is <c>x</c>.</para>
/// <para><see cref="Head(string)"/> and <see cref="Text(string)"/> are deliberately NOT quoted: a head word is a
/// declared literal from a closed set, and <see cref="Text(string)"/> exists precisely for a prose segment nobody
/// machine-parses. Anything a driver reads back is a <see cref="Field(string, string)"/>.</para>
/// </remarks>
public sealed class CommandEcho {
    // The grammar's reserved characters — see the quoting remarks on the class. '\' is absent deliberately: it is
    // only special INSIDE a quoted run, so a value carrying one but no reserved character stays verbatim.
    private static readonly SearchValues<char> ReservedCharacters = SearchValues.Create(values: "\t\n\r \"]|");

    private readonly StringBuilder m_builder;

    private bool m_pendingSegment;

    private CommandEcho(string verb) {
        m_builder = new StringBuilder(value: "[").Append(value: verb).Append(value: ':');
    }

    /// <summary>Starts a new echo line, writing the verb name.</summary>
    /// <param name="verb">The verb name.</param>
    /// <returns>The echo builder.</returns>
    public static CommandEcho Open(string verb) => new(verb: verb);
    /// <summary>Renders <paramref name="value"/> as one echo token: verbatim when it carries none of the grammar's
    /// reserved characters (whitespace, <c>'|'</c>, <c>']'</c>, <c>'"'</c>), and otherwise as a double-quoted run with
    /// <c>'\'</c> and <c>'"'</c> backslash-escaped inside it. <see cref="Field(string, string)"/> and
    /// <see cref="SpliceTag(string, string)"/> already apply it; it is public so a verb composing a token by hand can
    /// reach the same rule rather than inventing a second one.</summary>
    /// <param name="value">The token text.</param>
    /// <returns>The token, quoted only if it needs to be.</returns>
    public static string Quote(string value) {
        if (value.AsSpan().IndexOfAny(values: ReservedCharacters) < 0) {
            return value;
        }

        var builder = new StringBuilder(capacity: (value.Length + 2)).Append(value: '"');

        foreach (var character in value) {
            if (
                (character == '"') ||
                (character == '\\')
            ) {
                _ = builder.Append(value: '\\');
            }

            _ = builder.Append(value: character);
        }

        return builder.Append(value: '"').ToString();
    }

    private void FlushPendingSegment() {
        if (m_pendingSegment) {
            _ = m_builder.Append(value: " |");
            m_pendingSegment = false;
        }
    }

    /// <summary>Marks a boundary between groups of fields — the <c>" | "</c> separator is written only if more
    /// content follows, so a boundary marked immediately before <see cref="Close"/> vanishes rather than trailing.</summary>
    /// <returns>The echo builder.</returns>
    public CommandEcho Segment() {
        m_pendingSegment = true;

        return this;
    }
    /// <summary>Appends a space-prefixed <c>key=value</c> token, the value passed through
    /// <see cref="Quote(string)"/> so a reserved character inside it cannot end the token, the segment, or the
    /// envelope early.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="value">The field value, already formatted.</param>
    /// <returns>The echo builder.</returns>
    public CommandEcho Field(string key, string value) {
        FlushPendingSegment();

        _ = m_builder.Append(value: ' ').Append(value: key).Append(value: '=').Append(value: Quote(value: value));

        return this;
    }
    /// <summary>Appends a space-prefixed <c>key=value</c> token, the value invariant-culture formatted.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="value">The field value.</param>
    /// <returns>The echo builder.</returns>
    public CommandEcho Field<T>(string key, T value) where T : IFormattable => Field(
        key: key,
        value: value.ToString(
            format: null,
            formatProvider: CultureInfo.InvariantCulture
        )
    );
    /// <summary>Appends a space-prefixed <c>key=true</c>/<c>key=false</c> token.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="value">The field value.</param>
    /// <returns>The echo builder.</returns>
    public CommandEcho Field(string key, bool value) => Field(
        key: key,
        value: (value
            ? "true"
            : "false")
    );
    /// <summary>Appends a space-prefixed, declared free-text HEAD token — the one non-<c>key=value</c> word a
    /// segment may open with (e.g. <c>"kind"</c>, <c>"group"</c>, <c>"listing"</c>), naming what the
    /// <see cref="Field(string, string)"/> tokens that follow describe.</summary>
    /// <param name="head">The head word.</param>
    /// <returns>The echo builder.</returns>
    public CommandEcho Head(string head) {
        FlushPendingSegment();

        _ = m_builder.Append(value: ' ').Append(value: head);

        return this;
    }
    /// <summary>Appends space-prefixed free text — a whole segment of prose not meant to be machine-parsed as
    /// <c>key=value</c> fields at all (distinct from <see cref="Head(string)"/>, which names a segment's own
    /// content).</summary>
    /// <param name="text">The text to append.</param>
    /// <returns>The echo builder.</returns>
    public CommandEcho Text(string text) {
        FlushPendingSegment();

        _ = m_builder.Append(value: ' ').Append(value: text);

        return this;
    }
    /// <summary>Closes the echo line and returns the finished string.</summary>
    /// <returns>The finished <c>[verb: …]</c> line.</returns>
    public string Close() => m_builder.Append(value: ']').ToString();
    /// <summary>Splices ` <paramref name="tag"/>` just inside an already-closed bracketed echo's trailing <c>]</c>,
    /// or returns <paramref name="text"/> unchanged when it does not end in <c>]</c> — the shared surgery every
    /// after-the-fact echo tag (instance, perception anchor) uses. Each caller computes its own tag text; the tag is
    /// spliced as ONE token through <see cref="Quote(string)"/>, so a reserved character in the name a caller
    /// interpolated cannot close the envelope it is being spliced into.</summary>
    /// <param name="text">The bracketed echo to tag.</param>
    /// <param name="tag">The tag text, without surrounding brackets or the leading space.</param>
    /// <returns>The tagged echo, or <paramref name="text"/> unchanged.</returns>
    public static string SpliceTag(string text, string tag) =>
        (text.EndsWith(value: ']')
            ? $"{text[..^1]} {Quote(value: tag)}]"
            : text
        );
}
