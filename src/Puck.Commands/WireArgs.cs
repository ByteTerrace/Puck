using System.Globalization;

namespace Puck.Commands;

/// <summary>
/// The zero-copy argument view a <see cref="CommandDefinition.WithWireArgs"/> handler receives: the trailing tokens of a
/// submitted console line, each addressable as a <see cref="ReadOnlySpan{Char}"/> that slices straight into the original
/// line (or the fallback token array) with no per-token substring. This is the wire format's argument primitive — the
/// thing that lets the stdin hot path tokenize, look up, and dispatch a <c>verb arg arg…</c> line without materializing a
/// single heap string (see <c>CommandRegistry.Submit</c>'s fast path).
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
/// <see cref="Echo"/> carries the registry's per-dispatch acknowledgement decision so a handler can SKIP building a
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

    /// <summary>An empty argument list — the explicit "this call site supplies no tokens" value for a helper that takes
    /// <see cref="WireArgs"/> but is being reached from a path that has none.</summary>
    public static WireArgs Empty => default;

    /// <summary>The number of trailing tokens (the verb token is not counted).</summary>
    public int Count => (m_array?.Length ?? m_ranges.Length);

    /// <summary>
    /// Gets the trailing token at <paramref name="index"/> as a span slicing directly into the underlying line or token
    /// array — no substring is allocated.
    /// </summary>
    /// <param name="index">The zero-based trailing-token index, in <c>[0, <see cref="Count"/>)</c>.</param>
    /// <returns>The token's characters.</returns>
    public ReadOnlySpan<char> this[int index] => ((m_array is { } array)
        ? array[index].AsSpan()
        : m_line[m_ranges[index]]);

    /// <summary>
    /// Whether a SUCCESS echo produced by this dispatch will actually be surfaced (acks on, or a query verb). A wire
    /// handler should gate its success-string construction on this — <c>args.Echo ? new CommandResult(...) : CommandResult.None</c>
    /// — so that in quiet mode the string is never even built. Errors ignore this flag: a handler always builds its error
    /// string and marks the result <see cref="CommandResult.IsError"/>, and errors are never suppressed.
    /// </summary>
    public bool Echo { get; }

    /// <summary>Whether the token at <paramref name="index"/> equals <paramref name="value"/> case-insensitively — the
    /// allocation-free replacement for the <c>args[i].ToUpperInvariant() switch</c> idiom. An out-of-range index is
    /// <see langword="false"/>, so a caller can test an optional token without first checking <see cref="Count"/>.</summary>
    /// <param name="index">The zero-based trailing-token index.</param>
    /// <param name="value">The word to compare against.</param>
    /// <returns>Whether the token exists and matches.</returns>
    public bool Is(int index, string value) => (((uint)index < (uint)Count) &&
        this[index].Equals(other: value, comparisonType: StringComparison.OrdinalIgnoreCase));

    /// <summary>Joins the tokens from <paramref name="start"/> onward with single spaces — the ONE place a verb whose
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

        var length = (count - start - 1);

        for (var index = start; (index < count); index++) {
            length += this[index].Length;
        }

        // One allocation: the result. The scratch buffer is the stack below the common-tail size (a path, a name, a
        // short inline-JSON row), a heap array only for a genuinely long tail.
        var destination = ((length <= MaxStackTail) ? stackalloc char[MaxStackTail] : new char[length]);
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
    /// from its span (no thousands separators, NaN, or infinities), matching <see cref="CommandArgs.TryParseFloat"/>.</summary>
    /// <param name="index">The zero-based trailing-token index.</param>
    /// <param name="value">The parsed value, or <c>0</c> on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public bool TryFloat(int index, out float value) =>
        (float.TryParse(s: this[index], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out value) &&
        float.IsFinite(f: value));

    /// <summary>Parses the token at <paramref name="index"/> as an invariant-culture <see cref="int"/> straight from its
    /// span (plain digits with an optional sign), matching <see cref="CommandArgs.TryParseInt"/>.</summary>
    /// <param name="index">The zero-based trailing-token index.</param>
    /// <param name="value">The parsed value, or <c>0</c> on failure.</param>
    /// <returns>Whether the token parsed.</returns>
    public bool TryInt(int index, out int value) =>
        int.TryParse(s: this[index], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out value);
}
