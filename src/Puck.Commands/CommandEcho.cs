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
/// <para><b>Quoting.</b> The grammar reserves whitespace (it separates tokens), <c>'|'</c> (it separates segments),
/// <c>']'</c> (it closes the envelope) and <c>'"'</c> (it opens a quoted run). A value carrying any of them would
/// otherwise let a scripted driver's split land inside it — <c>path=C:\my games</c> reads as two tokens,
/// <c>members=[seat1]</c> closes the envelope early — so <see cref="Field(string, string)"/> and
/// <see cref="SpliceTag(string, string, string)"/> route their value through <see cref="Quote(string)"/> first. Whitespace
/// here is <see cref="char.IsWhiteSpace(char)"/>, the same rule the wire tokenizer splits a submitted line on, so
/// what this writer reserves is exactly what that reader separates on rather than an ASCII approximation of it.</para>
/// <para>A value needing no quoting is emitted verbatim, so every echo that was already unambiguous reads exactly as
/// it did. One that needs it is emitted as a double-quoted run in which <c>'\'</c> and <c>'"'</c> are
/// backslash-escaped and a line break is escaped rather than carried — quoting a raw newline would still leave the
/// record split across two lines, which is precisely the split a line-oriented driver makes FIRST, before it unquotes
/// anything. That is why the escaped set is wider than <c>\n</c> and <c>\r</c>: .NET's own line-ending rule
/// (<see cref="string.ReplaceLineEndings()"/>, <see cref="MemoryExtensions.EnumerateLines(ReadOnlySpan{char})"/>) also
/// breaks on <c>U+000B</c>, <c>U+000C</c>, <c>U+0085</c>, <c>U+2028</c> and <c>U+2029</c>, so every control character
/// and both Unicode separators are escaped — the three familiar ones as <c>\n</c>, <c>\r</c> and <c>\t</c> and the rest
/// as <c>\uXXXX</c>. An echo is therefore always exactly one line, whatever a value carries.</para>
/// <para><b>Reading it back.</b> The quoting opens where the VALUE begins, not where the token does — a
/// <see cref="Field(string, string)"/> emits <c>key="…"</c> and a <see cref="SpliceTag(string, string, string)"/> emits
/// <c>prefix:"…"</c> — so a reader that splits the envelope body on whitespace first has already torn
/// <c>path="C:\\my games"</c> into two pieces before it looks for a quote. Undoing an echo is therefore ONE pass over
/// the line, not a split followed by an unquote: a token runs until the first whitespace that is not inside a quoted
/// run; a <c>'"'</c> opens or closes such a run and is not itself part of the value; and inside a run <c>\n</c>,
/// <c>\r</c> and <c>\t</c> are those three characters, <c>\uXXXX</c> is the character at that code point, and
/// <c>\x</c> is <c>x</c>.
/// <see cref="TryReadToken(string, ref int, out string)"/> is that pass, and
/// <see cref="Unquote(string)"/> is the exact inverse of <see cref="Quote(string)"/> for one token already in hand —
/// they exist so a driver reads back through the writer's own rule instead of a second, drifting copy of it.</para>
/// <para><see cref="Head(string)"/> and <see cref="Text(string)"/> are deliberately NOT quoted: a head word is a
/// declared literal from a closed set, and <see cref="Text(string)"/> exists precisely for a prose segment nobody
/// machine-parses. Anything a driver reads back is a <see cref="Field(string, string)"/>.</para>
/// </remarks>
public sealed class CommandEcho {
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
    /// reserved characters (whitespace, <c>'|'</c>, <c>']'</c>, <c>'"'</c>), and otherwise as a double-quoted run
    /// escaped as the class remarks describe. <see cref="Field(string, string)"/> and
    /// <see cref="SpliceTag(string, string, string)"/> already apply it; it is public so a verb composing a token by hand can
    /// reach the same rule rather than inventing a second one.</summary>
    /// <param name="value">The token text.</param>
    /// <returns>The token, quoted only if it needs to be.</returns>
    public static string Quote(string value) {
        if (!NeedsQuoting(value: value)) {
            return value;
        }

        var builder = new StringBuilder(capacity: (value.Length + 2)).Append(value: '"');

        foreach (var character in value) {
            switch (character) {
                // '\' introduces every escape, so it escapes itself. It is also why a value carrying one is quoted at
                // all (see NeedsQuoting): the console's splitter hands a resubmitted token to a verb with the quotes
                // already stripped, and a reader looking at that bare value can only treat every '\' as an escape if
                // the writer never emitted a literal one.
                case '\\':
                    _ = builder.Append(value: "\\\\");

                    break;
                // The line breaks are the one class of reserved character quoting alone cannot contain — a driver
                // splits the stream into lines BEFORE it looks for tokens — so they are escaped rather than carried.
                // These three have short spellings because they are the ones a human reads back off a console.
                case '\n':
                    _ = builder.Append(value: "\\n");

                    break;
                case '\r':
                    _ = builder.Append(value: "\\r");

                    break;
                case '\t':
                    _ = builder.Append(value: "\\t");

                    break;
                default:
                    // Every other character the quoting alone cannot contain — see MustEscape — rides as \uXXXX,
                    // including the '"' that would otherwise close the run. A space, a '|' and a ']' ARE contained by
                    // the quoting and ride through as written.
                    if (MustEscape(character: character)) {
                        AppendUnicodeEscape(
                            builder: builder,
                            character: character
                        );
                    } else {
                        _ = builder.Append(value: character);
                    }

                    break;
            }
        }

        return builder.Append(value: '"').ToString();
    }
    /// <summary>Reads one whole token out of an echo line, starting at <paramref name="index"/>, and decodes it — the
    /// ONE pass that undoes what this writer emits, and the exact inverse of the token shapes
    /// <see cref="Field(string, string)"/>, <see cref="Head(string)"/> and
    /// <see cref="SpliceTag(string, string, string)"/> produce.</summary>
    /// <remarks>Leading whitespace is skipped, then the token runs to the first whitespace OUTSIDE a quoted run. A
    /// <c>'"'</c> opens or closes a run and is never part of the value, so <c>key="a b"</c> comes back whole as
    /// <c>key=a b</c>; inside a run <c>\n</c>, <c>\r</c> and <c>\t</c> decode to those characters, <c>\uXXXX</c> to the
    /// character at that code point, and <c>\x</c> to <c>x</c>. Splitting the line on whitespace FIRST and unquoting afterwards cannot work — the quoting opens where
    /// the value does, mid-token, so the split has already landed inside it.
    /// <para>This reads tokens, not structure: the caller still decides what the envelope, the <c>" | "</c> segment
    /// separators, and a token's own <c>=</c> or <c>:</c> mean.</para></remarks>
    /// <param name="line">The echo line to read.</param>
    /// <param name="index">The position to read from; on return, the position just past the token that was read.</param>
    /// <param name="token">The decoded token, or <see cref="string.Empty"/> when none remained.</param>
    /// <returns><see langword="true"/> when a token was read; <see langword="false"/> at the end of the line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or past the line's end.</exception>
    public static bool TryReadToken(string line, ref int index, out string token) {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: line.Length,
            value: index
        );

        while (
            (index < line.Length) &&
            char.IsWhiteSpace(c: line[index])
        ) {
            index++;
        }

        if (index >= line.Length) {
            token = string.Empty;

            return false;
        }

        var builder = new StringBuilder();
        var quoted = false;

        while (index < line.Length) {
            var character = line[index];

            if (
                !quoted &&
                char.IsWhiteSpace(c: character)
            ) {
                break;
            }

            if (character == '"') {
                quoted = !quoted;
                index++;

                continue;
            }

            // Escapes are read only INSIDE a run, because Quote only writes them there: a value carrying a backslash is
            // always quoted (see NeedsQuoting), so an UNQUOTED backslash cannot have come from this writer and means
            // itself — which is what a reader handed foreign text should make of it.
            if (
                quoted &&
                (character == '\\') &&
                ((index + 1) < line.Length)
            ) {
                if (TryReadUnicodeEscape(
                    character: out var escaped,
                    index: index,
                    line: line
                )) {
                    _ = builder.Append(value: escaped);
                    index += 6;

                    continue;
                }

                _ = builder.Append(value: (line[(index + 1)] switch {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    var other => other,
                }));
                index += 2;

                continue;
            }

            _ = builder.Append(value: character);
            index++;
        }

        token = builder.ToString();

        return true;
    }
    /// <summary>Decodes one token already in hand — the exact inverse of <see cref="Quote(string)"/>, so
    /// <c>Unquote(Quote(v)) == v</c> for every <paramref name="token"/> this writer can emit.</summary>
    /// <remarks>Reads by the same one-pass rule <see cref="TryReadToken(string, ref int, out string)"/> applies, and
    /// therefore stops at the first whitespace outside a quoted run. Pass exactly what <see cref="Quote(string)"/>
    /// returned; a reader working from a whole line wants <see cref="TryReadToken(string, ref int, out string)"/>
    /// instead, which finds the token boundaries the split cannot.</remarks>
    /// <param name="token">The token text, as this writer emitted it.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
    public static string Unquote(string token) {
        var index = 0;

        return (TryReadToken(
            index: ref index,
            line: token,
            token: out var decoded
        )
            ? decoded
            : string.Empty
        );
    }
    /// <summary>Decodes the escapes <see cref="Quote(string)"/> wrote for a value whose surrounding quoted run some
    /// OTHER splitter has already removed — the console's, when an operator copies a
    /// <see cref="SpliceTag(string, string, string)"/> tag off an echo and hands it straight back as an argument.</summary>
    /// <remarks>
    /// Two readers undo this writer, and only one of them is ours. <see cref="TryReadToken(string, ref int, out string)"/>
    /// reads a whole echo LINE and understands both halves of the encoding: the quoted run and the escapes inside it.
    /// System.CommandLine's command-line splitter, which every resubmitted line goes through, understands only the
    /// first: <c>'"'</c> opens and closes a run and nothing else means anything, so it hands the verb a value with its
    /// delimiting quotes gone and every escape still in it. This is the second half, applied by that verb.
    /// <para>The encoding is chosen so both readers can succeed. A <c>'"'</c> inside a value is written <c>\u0022</c>
    /// rather than <c>\"</c>, because the splitter has no escapes at all and would read the <c>'"'</c> of a <c>\"</c>
    /// pair as the run's end — <c>say \"hi\"</c> came back as <c>say \hi\</c>, with both quotes gone and two
    /// backslashes invented. And a value carrying a literal <c>'\'</c> is always quoted, so that after the splitter has
    /// stripped the quotes every remaining <c>'\'</c> is unambiguously an escape and this method can invert it.</para>
    /// <para>The consequence is that a value reaching a verb through this door is written in the echo's own escape
    /// grammar: a hand-typed backslash means an escape here, and a hand-typed literal one is doubled.</para>
    /// </remarks>
    /// <param name="value">The value, quotes already removed by the splitter that delivered it.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static string Unescape(string value) {
        ArgumentNullException.ThrowIfNull(value);

        // The overwhelmingly common case: an ordinary name, carrying no escape at all.
        if (value.IndexOf(value: '\\') < 0) {
            return value;
        }

        var builder = new StringBuilder(capacity: value.Length);
        var index = 0;

        while (index < value.Length) {
            var character = value[index];

            if (
                (character == '\\') &&
                ((index + 1) < value.Length)
            ) {
                if (TryReadUnicodeEscape(
                    character: out var escaped,
                    index: index,
                    line: value
                )) {
                    _ = builder.Append(value: escaped);
                    index += 6;

                    continue;
                }

                _ = builder.Append(value: (value[(index + 1)] switch {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    var other => other,
                }));
                index += 2;

                continue;
            }

            _ = builder.Append(value: character);
            index++;
        }

        return builder.ToString();
    }

    // Writes one character as the \uXXXX escape the reader inverts — four lowercase hex digits, always, so the reader
    // can find the escape's end by counting rather than by scanning.
    private static void AppendUnicodeEscape(StringBuilder builder, char character) {
        Span<char> hex = stackalloc char[4];

        _ = ((ushort)character).TryFormat(
            destination: hex,
            charsWritten: out _,
            format: "x4",
            provider: CultureInfo.InvariantCulture
        );
        _ = builder.Append(value: "\\u").Append(value: hex);
    }
    // THE ONE RULE for "this character cannot ride inside a quoted run as written", so Quote and the reader that
    // inverts it cannot drift apart. Quoting contains everything a token or a segment split would find, but it cannot
    // contain a LINE break: a driver splits the stream into lines first, before it looks for tokens at all, so a
    // character .NET counts as a line ending tears the record in half however well quoted it is. That set is wider than
    // '\n' and '\r' — ReplaceLineEndings and EnumerateLines also break on U+000B, U+000C, U+0085, U+2028 and U+2029 —
    // and a listed set would have to be re-derived every time one is added, so the test is the CATEGORY: every control
    // character, plus the two Unicode separators that are not control characters, plus the '"' itself — see Unescape
    // for why that one may not ride as `\"`.
    private static bool MustEscape(char character) => (
        char.IsControl(c: character) ||
        (character == '"') ||
        (char.GetUnicodeCategory(c: character) is (UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
    );
    // Whether a driver's own split could land inside this value. Whitespace is tested by CATEGORY rather than against
    // a listed set: char.IsWhiteSpace is the rule CommandRegistry's wire tokenizer splits on, and a listed set would
    // let a vertical tab or a non-breaking space through unquoted for a reader that splits the way the wire does.
    //
    // A '\' forces quoting even though nothing splits on it: it is the escape introducer, and Unescape reads a value
    // whose quotes some other splitter has already removed, so it cannot tell an escape from a literal backslash
    // unless every literal one was written doubled. Quoting is what guarantees that.
    private static bool NeedsQuoting(string value) {
        foreach (var character in value) {
            if (
                char.IsWhiteSpace(c: character) ||
                (character == '\\') ||
                (character == ']') ||
                (character == '|') ||
                MustEscape(character: character)
            ) {
                return true;
            }
        }

        return false;
    }
    // Reads a \uXXXX escape at `index` (which addresses its '\'), or answers false for anything else — a short line, a
    // different escape letter, a digit that is not hex. Strict AllowHexSpecifier rather than NumberStyles.HexNumber:
    // the latter tolerates surrounding whitespace, which would let `\u 41` decode as a character.
    private static bool TryReadUnicodeEscape(string line, int index, out char character) {
        if (
            (line[(index + 1)] == 'u') &&
            ((index + 6) <= line.Length) &&
            ushort.TryParse(
            s: line.AsSpan(length: 4, start: (index + 2)),
            provider: CultureInfo.InvariantCulture,
            style: NumberStyles.AllowHexSpecifier,
            result: out var scalar
        )
        ) {
            character = ((char)scalar);

            return true;
        }

        character = '\0';

        return false;
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
    /// <summary>Splices ` <paramref name="prefix"/><paramref name="value"/>` just inside an already-closed bracketed
    /// echo's trailing <c>]</c>, or returns <paramref name="text"/> unchanged when it does not end in <c>]</c> — the
    /// shared surgery every after-the-fact echo tag (instance, perception anchor) uses.</summary>
    /// <remarks>
    /// The tag is a KEY and a VALUE, never one opaque string, because only the value may be quoted. A composite tag
    /// quoted whole (<c>"instance:my world"</c>) hides its own reserved prefix behind the quote, and the readers of
    /// these tags test for that prefix — so the tag would still be one well-formed token and still mean nothing to the
    /// thing it was written for. The prefix therefore rides through verbatim (callers spell a declared literal there —
    /// <c>instance:</c>, <c>anchor=body:</c> — never interpolated text) and only the value goes through
    /// <see cref="Quote(string)"/>, which is enough for the whole tag to stay one token.
    /// <para>A quoted value reads back exactly as <see cref="Field(string, string)"/>'s does, but which reader is doing
    /// the reading decides how much of the decoding is left. A driver reading the echo LINE uses
    /// <see cref="TryReadToken(string, ref int, out string)"/>, which undoes the whole encoding. A resubmitted line
    /// goes through the console's own splitter instead, and that splitter knows only about <c>'"'</c>: it removes the
    /// pair — so <c>instance:"my world"</c> reaches a verb as the single token <c>instance:my world</c>, with the
    /// reserved prefix still leading — and leaves every escape inside untouched. The verb receiving such a token
    /// therefore finishes the job with <see cref="Unescape(string)"/>, which is why <see cref="Quote(string)"/> writes
    /// an interior <c>'"'</c> as <c>\u0022</c> rather than <c>\"</c> and always quotes a value carrying <c>'\'</c>.</para>
    /// </remarks>
    /// <param name="text">The bracketed echo to tag.</param>
    /// <param name="prefix">The tag's declared literal key, including its own separator (e.g. <c>instance:</c>).</param>
    /// <param name="value">The tag's value, quoted only if it needs to be.</param>
    /// <returns>The tagged echo, or <paramref name="text"/> unchanged.</returns>
    public static string SpliceTag(string text, string prefix, string value) =>
        (text.EndsWith(value: ']')
            ? $"{text[..^1]} {prefix}{Quote(value: value)}]"
            : text
        );
}
