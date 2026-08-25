namespace Puck.Commands;

/// <summary>
/// The zero-copy argument view a <see cref="CommandDefinition.WithWireArgs"/> handler receives: the trailing tokens of a
/// submitted console line, each addressable as a <see cref="ReadOnlySpan{Char}"/> that slices straight into the original
/// line (or the fallback token array) with no per-token substring. This is the wire format's argument primitive — the
/// thing that lets the stdin hot path tokenize, look up, and dispatch a <c>verb arg arg…</c> line without materializing a
/// single heap string (see <c>CommandRegistry.Submit</c>'s wire-native path).
/// </summary>
/// <remarks>
/// A <see cref="WireArgs"/> is a <see langword="ref"/> struct: it borrows the caller's line span and token ranges and is
/// valid only for the synchronous duration of the handler call — a handler must not store it or let it escape. It carries
/// two construction modes behind one surface:
/// <list type="bullet">
/// <item><description><b>(a) span mode</b> — a line span plus a <see cref="Range"/> span, the allocation-free hot path the
/// registry drives from a <see langword="stackalloc"/> token buffer.</description></item>
/// <item><description><b>(b) array mode</b> — a <see cref="string"/> array, the adapter the <see cref="CommandDefinition.WithWireArgs"/>
/// text command uses on the System.CommandLine fallback path (quoted lines, help, parse-error text), so one wire handler
/// serves both paths as a single source of truth.</description></item>
/// </list>
/// <see cref="Echo"/> carries the registry's per-dispatch acknowledgement decision so a handler can skip building a
/// success echo string when acks are quiet — the branch that makes a flooded quiet wire path allocate nothing.
/// </remarks>
public readonly ref struct WireArgs {
    // The scratch size Tail joins on the stack before renting the heap.
    private const int MaxStackTail = 512;

    private readonly ReadOnlySpan<char> m_line;
    private readonly ReadOnlySpan<Range> m_ranges;
    private readonly string[]? m_array;

    /// <summary>Span-mode constructor (a): the trailing tokens are <see cref="Range"/>s into <paramref name="line"/>.</summary>
    /// <param name="line">The full submitted line the ranges slice into.</param>
    /// <param name="ranges">One <see cref="Range"/> per trailing token, in order (the verb token excluded).</param>
    /// <param name="echo">Whether a success echo will be surfaced — see <see cref="Echo"/>.</param>
    internal WireArgs(ReadOnlySpan<char> line, ReadOnlySpan<Range> ranges, bool echo) {
        m_array = null;
        m_line = line;
        m_ranges = ranges;
        Echo = echo;
    }
    /// <summary>Array-mode constructor (b): the trailing tokens are the elements of <paramref name="array"/>.</summary>
    /// <param name="array">The trailing tokens as already-materialized strings (the System.CommandLine fallback).</param>
    /// <param name="echo">Whether a success echo will be surfaced — see <see cref="Echo"/>.</param>
    internal WireArgs(string[] array, bool echo) {
        m_array = array;
        m_line = default;
        m_ranges = default;
        Echo = echo;
    }

    /// <summary>The number of trailing tokens (the verb token is not counted).</summary>
    public int Count => (m_array?.Length ?? m_ranges.Length);
    /// <summary>
    /// Whether a success echo produced by this dispatch will actually be surfaced (acks on, or a query verb). A wire
    /// handler should gate its success-string construction on this — <c>args.Echo ? new CommandResult(...) : CommandResult.None</c>
    /// — so that in quiet mode the string is never even built. Errors ignore this flag: a handler always builds its error
    /// string and marks the result <see cref="CommandResult.IsError"/>, and errors are never suppressed.
    /// </summary>
    public bool Echo { get; }
    /// <summary>An empty argument list — the explicit "this call site supplies no tokens" value for a helper that takes
    /// <see cref="WireArgs"/> but is being reached from a path that has none.</summary>
    public static WireArgs Empty => default;

    /// <summary>
    /// Gets the trailing token at <paramref name="index"/> as a span slicing directly into the underlying line or token
    /// array — no substring is allocated.
    /// </summary>
    /// <param name="index">The zero-based trailing-token index, in <c>[0, <see cref="Count"/>)</c>.</param>
    /// <returns>The token's characters.</returns>
    public ReadOnlySpan<char> this[int index] => ((m_array is { } array)
        ? array[index].AsSpan()
        : m_line[m_ranges[index]]
    );

    /// <summary>Whether the token at <paramref name="index"/> equals <paramref name="value"/> case-insensitively — the
    /// allocation-free replacement for the <c>args[i].ToUpperInvariant() switch</c> idiom. An out-of-range index is
    /// <see langword="false"/>, so a caller can test an optional token without first checking <see cref="Count"/>.</summary>
    /// <param name="index">The zero-based trailing-token index.</param>
    /// <param name="value">The word to compare against.</param>
    /// <returns>Whether the token exists and matches.</returns>
    public bool Is(int index, string value) => ((((uint)index) < ((uint)Count)) &&
        this[index].Equals(
        comparisonType: StringComparison.OrdinalIgnoreCase,
        other: value
    ));
    /// <summary>Joins the tokens from <paramref name="start"/> onward with single spaces — the one place a verb whose
    /// argument is free text (a path, a name, a message) or a whitespace-split inline-JSON payload rebuilds its tail.
    /// Reproduces <c>string.Join(' ', args[start..])</c>: interior whitespace runs collapse to one space, exactly as
    /// the token-array form always did.</summary>
    /// <param name="start">The zero-based trailing-token index to join from.</param>
    /// <returns>The joined text, or <see cref="string.Empty"/> when no token sits at or after <paramref name="start"/>.</returns>
    public string Tail(int start) {
        var count = Count;

        if (start >= count) {
            return string.Empty;
        }

        var length = ((count - start) - 1);

        for (var index = start; (index < count); index++) {
            length += this[index].Length;
        }

        // One allocation: the result. The scratch buffer is the stack below the common-tail size (a path, a name, a
        // short inline-JSON row), a heap array only for a genuinely long tail.
        var destination = ((length <= MaxStackTail)
            ? stackalloc char[MaxStackTail]
            : new char[length]
        );
        var offset = 0;

        for (var index = start; (index < count); index++) {
            if (index > start) {
                destination[offset++] = ' ';
            }

            var token = this[index];

            token.CopyTo(destination: destination[offset..]);

            offset += token.Length;
        }

        return new string(value: destination[..length]);
    }
    /// <summary>Parses the token at <paramref name="index"/> as a finite invariant-culture <see cref="float"/> straight
    /// from its span, through <see cref="CommandArgs.TryParseFloat(ReadOnlySpan{char}, out float)"/>.</summary>
    /// <param name="index">The zero-based trailing-token index.</param>
    /// <param name="value">The parsed value, or <c>0</c> on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public bool TryFloat(int index, out float value) => CommandArgs.TryParseFloat(
        text: this[index],
        value: out value
    );
    /// <summary>Parses the token at <paramref name="index"/> as an invariant-culture <see cref="int"/> straight from its
    /// span, through <see cref="CommandArgs.TryParseInt(ReadOnlySpan{char}, out int)"/>.</summary>
    /// <param name="index">The zero-based trailing-token index.</param>
    /// <param name="value">The parsed value, or <c>0</c> on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public bool TryInt(int index, out int value) => CommandArgs.TryParseInt(
        text: this[index],
        value: out value
    );
    /// <summary>Parses the token at <paramref name="index"/> as an invariant-culture <see cref="long"/> straight from
    /// its span, through <see cref="CommandArgs.TryParseLong(ReadOnlySpan{char}, out long)"/>.</summary>
    /// <param name="index">The zero-based trailing-token index.</param>
    /// <param name="value">The parsed value, or <c>0</c> on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public bool TryLong(int index, out long value) => CommandArgs.TryParseLong(
        text: this[index],
        value: out value
    );
    /// <summary>Parses the token at <paramref name="index"/> as an invariant-culture <see cref="ulong"/> straight
    /// from its span, through <see cref="CommandArgs.TryParseULong(ReadOnlySpan{char}, out ulong)"/>.</summary>
    /// <param name="index">The zero-based trailing-token index.</param>
    /// <param name="value">The parsed value, or <c>0</c> on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public bool TryULong(int index, out ulong value) => CommandArgs.TryParseULong(
        text: this[index],
        value: out value
    );
    /// <summary>Parses <paramref name="count"/> consecutive float arguments starting at <paramref name="start"/>
    /// (e.g. an <c>&lt;x&gt; &lt;y&gt; &lt;z&gt;</c> triple) — fails as a unit if any token is missing or unparsable,
    /// the zero-copy peer of <see cref="CommandArgs.TryParseFloats(string[], int, int, out float[])"/>.</summary>
    /// <param name="start">The zero-based trailing-token index to start from.</param>
    /// <param name="count">How many consecutive floats to parse.</param>
    /// <param name="values">The parsed values (length <paramref name="count"/>), zeroed on failure.</param>
    /// <returns>Whether every token in the range parsed.</returns>
    public bool TryFloats(int start, int count, out float[] values) {
        values = new float[count];

        if (Count < (start + count)) {
            return false;
        }

        for (var index = 0; (index < count); index++) {
            if (!TryFloat(
                index: (start + index),
                value: out values[index]
            )) {
                return false;
            }
        }

        return true;
    }
}
