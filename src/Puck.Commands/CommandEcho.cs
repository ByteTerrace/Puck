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
    /// <summary>Appends a space-prefixed <c>key=value</c> token.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="value">The field value, already formatted.</param>
    /// <returns>The echo builder.</returns>
    public CommandEcho Field(string key, string value) {
        FlushPendingSegment();

        _ = m_builder.Append(value: ' ').Append(value: key).Append(value: '=').Append(value: value);

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
}
