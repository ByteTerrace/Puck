using System.Globalization;
using System.Text;

namespace Puck.Hosting;

/// <summary>The process stream a <see cref="HostLog"/> line was written to.</summary>
public enum HostLogStream {
    /// <summary>Standard output.</summary>
    Output,
    /// <summary>Standard error.</summary>
    Error,
}
/// <summary>One line a host wrote.</summary>
/// <param name="Sequence">The line's position in everything the log has held, from 1; a later line has a larger one.</param>
/// <param name="Tick">The tick the host had completed when the line was written, or <see langword="null"/> before a
/// tick source was anchored.</param>
/// <param name="Stream">The stream the line was written to.</param>
/// <param name="Category">The line's leading tag (<c>sdf.mesh-bake</c> for <c>[sdf.mesh-bake] …</c>,
/// <c>world.wait</c> for <c>[world.wait: …]</c>); an indented continuation line carries the tag of the line before it,
/// and an untagged line carries none.</param>
/// <param name="Text">The line, without its terminator, cut at <see cref="HostLog.MaxLineCharacters"/>.</param>
public readonly record struct HostLogLine(long Sequence, ulong? Tick, HostLogStream Stream, string Category, string Text);
/// <summary>What a <see cref="HostLog.Read"/> selects. Every member narrows; none set reads the newest
/// <see cref="Last"/> lines.</summary>
public sealed record HostLogQuery {
    /// <summary>Gets the most lines answered, the newest of those that match.</summary>
    public int Last { get; init; } = 100;
    /// <summary>Gets the sequence a line must follow, to read on from an earlier read.</summary>
    public long Since { get; init; }
    /// <summary>Gets the first tick a line may carry; a line written before the tick source was anchored has none and
    /// never matches a tick bound.</summary>
    public ulong? FromTick { get; init; }
    /// <summary>Gets the last tick a line may carry.</summary>
    public ulong? ToTick { get; init; }
    /// <summary>Gets the prefix a line's category starts with, ignoring case.</summary>
    public string? Category { get; init; }
    /// <summary>Gets text a line contains, ignoring case.</summary>
    public string? Text { get; init; }
    /// <summary>Gets the stream a line was written to.</summary>
    public HostLogStream? Stream { get; init; }
}
/// <summary>The answer to a <see cref="HostLog.Read"/>.</summary>
/// <param name="Lines">The matching lines, oldest first.</param>
/// <param name="Matched">How many held lines matched, of which <paramref name="Lines"/> are the newest.</param>
/// <param name="Held">How many lines the log holds.</param>
/// <param name="Newest">The newest held line's sequence, 0 when none was written; a later read with this as
/// <see cref="HostLogQuery.Since"/> answers only lines written after this read.</param>
/// <param name="Dropped">How many lines the ring has evicted since the process started.</param>
public sealed record HostLogRead(IReadOnlyList<HostLogLine> Lines, int Matched, int Held, long Newest, long Dropped);
/// <summary>
/// A bounded ring of the lines a host process wrote to standard output and standard error, each anchored to the tick
/// the host had completed when it was written, so a caller that did not launch the process (an Operator attachment)
/// can read the announcements only stderr carried: the bake lines, refusals, pacing. <see cref="CaptureConsole"/>
/// tees <see cref="Console.Out"/> and <see cref="Console.Error"/> into it; a writer that bypasses them appends its
/// lines with <see cref="Append"/>. The ring holds at most <see cref="Capacity"/> lines and
/// <see cref="CharacterBudget"/> characters and evicts the oldest past either, so its retention never grows with the
/// run. It is presentation of the process's output and never feeds simulation state. Safe from any thread.
/// </summary>
/// <param name="capacity">The most lines held.</param>
/// <param name="characterBudget">The most characters held across every line.</param>
public sealed class HostLog(int capacity = HostLog.Capacity, int characterBudget = HostLog.CharacterBudget) {
    /// <summary>The default most lines held.</summary>
    public const int Capacity = 4096;
    /// <summary>The default most characters held, the control transport's whole answer.</summary>
    public const int CharacterBudget = 1_048_576;
    /// <summary>The longest line held; a longer one is cut and ends in a marker naming its length.</summary>
    public const int MaxLineCharacters = 4096;

    private readonly Lock m_gate = new();
    private readonly HostLogLine[] m_ring = new HostLogLine[Math.Max(val1: 1, val2: capacity)];
    private readonly string[] m_lastCategory = [string.Empty, string.Empty];

    private int m_characters;
    private int m_count;
    private long m_dropped;
    private int m_head;
    private long m_sequence;
    private Func<ulong>? m_tick;

    /// <summary>Gets the line capacity.</summary>
    public int LineCapacity => m_ring.Length;
    /// <summary>Gets the character budget.</summary>
    public int CharacterLimit => characterBudget;

    /// <summary>Creates a log and tees the process's <see cref="Console.Out"/> and <see cref="Console.Error"/> into
    /// it, each still writing to the stream it wrote to before.</summary>
    /// <returns>The log.</returns>
    public static HostLog CaptureConsole() {
        var log = new HostLog();

        Console.SetOut(newOut: log.Tee(inner: Console.Out, stream: HostLogStream.Output));
        Console.SetError(newError: log.Tee(inner: Console.Error, stream: HostLogStream.Error));

        return log;
    }

    /// <summary>Anchors every later line to a tick.</summary>
    /// <param name="completedTick">Reads the tick the host has completed; called on the writing thread.</param>
    public void Anchor(Func<ulong> completedTick) {
        ArgumentNullException.ThrowIfNull(argument: completedTick);

        Volatile.Write(location: ref m_tick, value: completedTick);
    }
    /// <summary>Appends text written to a stream, one held line per line of it.</summary>
    /// <param name="stream">The stream it was written to.</param>
    /// <param name="text">The text; any line ending separates lines, and a trailing one ends the last.</param>
    public void Append(HostLogStream stream, string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        foreach (var line in text.AsSpan().TrimEnd(trimChars: "\r\n").EnumerateLines()) {
            Commit(line: line, stream: stream);
        }
    }
    /// <summary>Wraps a writer so every line written through it is also held here.</summary>
    /// <param name="inner">The writer the text still reaches.</param>
    /// <param name="stream">The stream the lines are held as.</param>
    /// <returns>The teeing writer.</returns>
    public TextWriter Tee(TextWriter inner, HostLogStream stream) {
        ArgumentNullException.ThrowIfNull(argument: inner);

        return new HostLogWriter(inner: inner, log: this, stream: stream);
    }
    /// <summary>Reads the held lines a query selects.</summary>
    /// <param name="query">What to select.</param>
    /// <returns>The newest matching lines and the ring's extent.</returns>
    public HostLogRead Read(HostLogQuery query) {
        ArgumentNullException.ThrowIfNull(argument: query);

        var matched = new List<HostLogLine>();

        lock (m_gate) {
            for (var index = 0; (index < m_count); index++) {
                var line = m_ring[((m_head + index) % m_ring.Length)];

                if (Matches(line: line, query: query)) {
                    matched.Add(item: line);
                }
            }

            var last = Math.Max(val1: 0, val2: query.Last);

            return new HostLogRead(
                Dropped: m_dropped,
                Held: m_count,
                Lines: ((matched.Count > last) ? matched[(matched.Count - last)..] : matched),
                Matched: matched.Count,
                Newest: m_sequence
            );
        }
    }

    // The tag a line opens with: the text after '[' up to ']', ':' or a space.
    private static string CategoryOf(ReadOnlySpan<char> line) {
        if (!line.StartsWith(value: '[')) {
            return string.Empty;
        }

        var end = line[1..].IndexOfAny(values: "]: ");

        return ((end > 0) ? line.Slice(start: 1, length: end).ToString() : string.Empty);
    }
    private static bool Matches(HostLogLine line, HostLogQuery query) =>
        (line.Sequence > query.Since) &&
        ((query.Stream is not { } stream) || (line.Stream == stream)) &&
        ((query.FromTick is not { } from) || (line.Tick >= from)) &&
        ((query.ToTick is not { } to) || (line.Tick <= to)) &&
        (string.IsNullOrEmpty(value: query.Category) || line.Category.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: query.Category)) &&
        (string.IsNullOrEmpty(value: query.Text) || line.Text.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: query.Text));

    private void Commit(ReadOnlySpan<char> line, HostLogStream stream) {
        var text = ((line.Length > MaxLineCharacters)
            ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{line[..MaxLineCharacters]}… [{line.Length} characters]")
            : line.ToString());
        var tick = Volatile.Read(location: ref m_tick)?.Invoke();

        lock (m_gate) {
            var slot = (int)stream;
            var continuation = ((text.Length > 0) && char.IsWhiteSpace(c: text[0]));
            var category = (continuation ? m_lastCategory[slot] : CategoryOf(line: text));

            m_lastCategory[slot] = category;

            while ((m_count > 0) && ((m_count == m_ring.Length) || ((m_characters + text.Length) > characterBudget))) {
                m_characters -= m_ring[m_head].Text.Length;
                m_ring[m_head] = default;
                m_head = ((m_head + 1) % m_ring.Length);
                m_count--;
                m_dropped++;
            }

            m_ring[((m_head + m_count) % m_ring.Length)] = new HostLogLine(
                Category: category,
                Sequence: ++m_sequence,
                Stream: stream,
                Text: text,
                Tick: tick
            );
            m_count++;
            m_characters += text.Length;
        }
    }

    // Forwards every write to the inner writer and commits each completed line. A write call is atomic against the
    // log's other writers; a line split across calls is assembled per writer.
    private sealed class HostLogWriter(TextWriter inner, HostLog log, HostLogStream stream) : TextWriter {
        private readonly Lock m_gate = new();
        private readonly StringBuilder m_pending = new();

        public override Encoding Encoding => inner.Encoding;
        public override IFormatProvider FormatProvider => inner.FormatProvider;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string NewLine { get => inner.NewLine; set => inner.NewLine = value; }

        public override void Flush() => inner.Flush();
        public override void Write(char value) {
            lock (m_gate) {
                inner.Write(value: value);
                Collect(text: [value]);
            }
        }
        public override void Write(char[] buffer, int index, int count) {
            lock (m_gate) {
                inner.Write(buffer: buffer, count: count, index: index);
                Collect(text: buffer.AsSpan(start: index, length: count));
            }
        }
        public override void Write(ReadOnlySpan<char> buffer) {
            lock (m_gate) {
                inner.Write(buffer: buffer);
                Collect(text: buffer);
            }
        }
        public override void Write(string? value) {
            lock (m_gate) {
                inner.Write(value: value);
                Collect(text: value);
            }
        }
        public override void WriteLine() {
            lock (m_gate) {
                inner.WriteLine();
                Collect(text: "\n");
            }
        }
        public override void WriteLine(string? value) {
            lock (m_gate) {
                inner.WriteLine(value: value);
                Collect(text: value);
                Collect(text: "\n");
            }
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                inner.Dispose();
            }

            base.Dispose(disposing: disposing);
        }

        private void Collect(ReadOnlySpan<char> text) {
            while (!text.IsEmpty) {
                var end = text.IndexOf(value: '\n');

                if (end < 0) {
                    if (m_pending.Length <= MaxLineCharacters) {
                        _ = m_pending.Append(value: text);
                    }

                    return;
                }

                _ = m_pending.Append(value: text[..end]);

                var length = m_pending.Length;

                if ((length > 0) && (m_pending[length - 1] == '\r')) {
                    m_pending.Length = (length - 1);
                }

                log.Commit(line: m_pending.ToString(), stream: stream);
                _ = m_pending.Clear();
                text = text[(end + 1)..];
            }
        }
    }
}
