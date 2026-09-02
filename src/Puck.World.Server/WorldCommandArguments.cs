using Puck.Commands;

namespace Puck.World;

/// <summary>The ONE reconstruction of a verb's free-text tail from the SUBMITTED LINE. A verb whose last argument is
/// prose, a path, or an inline JSON payload cannot read it back out of <see cref="WireArgs"/> — the console tokenizer
/// has already collapsed its interior whitespace — so it re-reads <see cref="CommandContext.Text"/> and strips the
/// leading address tokens itself. The address is only ever "how many tokens precede the tail", which is why every such
/// verb differs from every other by one number and shares every other line.</summary>
public static class WorldCommandArguments {
    /// <summary>Strips the verb token alone.</summary>
    /// <param name="context">The invoking context, whose <see cref="CommandContext.Text"/> carries the raw line.</param>
    /// <param name="args">The tokenized arguments, the fallback when no raw line exists (a bound or programmatic call).</param>
    /// <returns>The text after the verb, or <see cref="string.Empty"/> when the line carries none.</returns>
    public static string Raw(CommandContext context, in WireArgs args) => RawAfter(
        args: in args,
        context: context,
        tokens: 1
    );
    /// <summary>Strips <paramref name="tokens"/> leading whitespace-separated tokens — the verb plus however many
    /// address tokens the verb spells before its tail (<c>world.row.set</c> strips verb + path,
    /// <c>world.state.cell.set</c> strips verb + row + key, <c>identity.deliver</c> strips five).</summary>
    /// <remarks>
    /// The split is <see cref="char.IsWhiteSpace(char)"/>, matching the registry's own wire tokenizer exactly: a
    /// narrower set (space and tab) would return an empty tail for a vertical-tab-separated line the tokenizer had
    /// already split correctly, so the verb would answer with its usage refusal for a line it accepted.
    /// <para>A tail that is EXACTLY one double-quoted token has that one surrounding pair removed. Such a line went
    /// through System.CommandLine's splitter, which strips the pair before the handler's <see cref="WireArgs"/> ever
    /// sees it, so leaving it on here would hand a quoted-and-unquoted verb two different answers for the same line
    /// (<c>world.hud.template "{world.tick} ticks"</c> would render its own quotes into the HUD). The strip is
    /// deliberately narrow — a tail carrying any further <c>"</c> is returned verbatim — because the whole reason this
    /// reads the raw line is that interior spacing must survive, and a general unquote would have to re-tokenize the
    /// tail to find the runs, collapsing exactly what was being preserved.</para>
    /// </remarks>
    /// <param name="context">The invoking context, whose <see cref="CommandContext.Text"/> carries the raw line.</param>
    /// <param name="args">The tokenized arguments, the fallback when no raw line exists (a bound or programmatic call).</param>
    /// <param name="tokens">How many leading tokens to strip, the verb included.</param>
    /// <returns>The text after those tokens, or <see cref="string.Empty"/> when the line carries fewer.</returns>
    public static string RawAfter(CommandContext context, in WireArgs args, int tokens) {
        if (context.Text is { } text) {
            var span = text.AsSpan().TrimStart();

            for (var skip = 0; (skip < tokens); skip++) {
                var separator = IndexOfWhiteSpace(span: span);

                if (separator < 0) {
                    return string.Empty;
                }

                span = span[(separator + 1)..].TrimStart();
            }

            return Unwrap(tail: span.Trim());
        }

        return args.Tail(start: (tokens - 1));
    }
    /// <summary>Strips <paramref name="leadingTokens"/> leading tokens exactly as <see cref="RawAfter"/> does, then
    /// strips <paramref name="trailingTokens"/> whitespace-delimited tokens off the END — the shape a verb whose
    /// free-text tail sits BEFORE a fixed positional suffix needs (<c>identity.hud &lt;panel-json&gt; [player]</c>,
    /// <c>world.load &lt;path&gt; [force]</c>).</summary>
    /// <remarks>The trailing split is <see cref="char.IsWhiteSpace(char)"/> for the same reason the leading one is: a
    /// narrower space-and-tab scan finds no separator in a vertical-tab-separated line the registry's own tokenizer
    /// had already split correctly, so the suffix stays glued to the tail and the verb tries to parse
    /// <c>{"id":"hp"}\v3</c> as its payload.
    /// <para>A line carrying fewer tokens than <paramref name="trailingTokens"/> names has no tail at all and answers
    /// <see cref="string.Empty"/>, matching <see cref="RawAfter"/>'s answer for an address longer than its line.</para></remarks>
    /// <param name="context">The invoking context, whose <see cref="CommandContext.Text"/> carries the raw line.</param>
    /// <param name="args">The tokenized arguments, the fallback when no raw line exists (a bound or programmatic call).</param>
    /// <param name="leadingTokens">How many leading tokens to strip, the verb included.</param>
    /// <param name="trailingTokens">How many trailing positional tokens to strip off the end.</param>
    /// <returns>The text between those two token runs, or <see cref="string.Empty"/> when the line carries fewer.</returns>
    public static string RawBetween(CommandContext context, in WireArgs args, int leadingTokens, int trailingTokens) {
        var raw = RawAfter(
            args: in args,
            context: context,
            tokens: leadingTokens
        );

        if (trailingTokens <= 0) {
            return raw;
        }

        var span = raw.AsSpan();

        for (var strip = 0; (strip < trailingTokens); strip++) {
            span = span.TrimEnd();

            var separator = LastIndexOfWhiteSpace(span: span);

            if (separator < 0) {
                return string.Empty;
            }

            span = span[..separator];
        }

        return Unwrap(tail: span.Trim());
    }
    /// <summary>Strips <paramref name="leadingTokens"/> leading tokens exactly as <see cref="RawAfter"/> does, then —
    /// only when the line's LAST token is <paramref name="keyword"/> — that token too: the shape a verb whose
    /// free-text tail is followed by an OPTIONAL literal word needs (<c>world.load &lt;path&gt; [force]</c>).</summary>
    /// <remarks>The keyword is recognized off the registry's own tokenization (<see cref="WireArgs.Is"/>) rather than
    /// by scanning the raw line, so the two ends of the reconstruction can never disagree about where a token begins —
    /// a hand-rolled <c>anyOf: [' ', '\t']</c> scan absorbed the keyword into the tail for a line the tokenizer had
    /// already split (<c>world.load &lt;path&gt;\vforce</c> loaded a file whose name ended in <c>\vforce</c>).
    /// <para>The keyword is only recognized when a token PRECEDES it: a line that is the bare word and nothing else
    /// carries no tail, and the grammars that use this all spell a REQUIRED tail before the optional word, so
    /// <c>world.load force</c> names a file called <c>force</c> rather than flagging an absent path.</para></remarks>
    /// <param name="context">The invoking context, whose <see cref="CommandContext.Text"/> carries the raw line.</param>
    /// <param name="args">The tokenized arguments, which decide whether the keyword is present.</param>
    /// <param name="leadingTokens">How many leading tokens to strip, the verb included.</param>
    /// <param name="keyword">The optional trailing word, compared case-insensitively.</param>
    /// <param name="present">Whether that word was the line's last token.</param>
    /// <returns>The text between the leading tokens and the keyword, or <see cref="string.Empty"/> when the line
    /// carries none.</returns>
    public static string RawBeforeKeyword(CommandContext context, in WireArgs args, int leadingTokens, string keyword, out bool present) {
        present = ((args.Count >= 2) && args.Is(
            index: (args.Count - 1),
            value: keyword
        ));

        return RawBetween(
            args: in args,
            context: context,
            leadingTokens: leadingTokens,
            trailingTokens: (present
                ? 1
                : 0)
        );
    }

    // The first whitespace character by CATEGORY, the rule CommandRegistry's wire tokenizer splits a submitted line
    // on. Scanned rather than looked up because char.IsWhiteSpace is a Unicode category test, not a listed set.
    private static int IndexOfWhiteSpace(ReadOnlySpan<char> span) {
        for (var index = 0; (index < span.Length); index++) {
            if (char.IsWhiteSpace(c: span[index])) {
                return index;
            }
        }

        return -1;
    }
    // The LAST whitespace character by CATEGORY — IndexOfWhiteSpace's mirror, and the split a verb whose free-text
    // tail sits BEFORE a fixed positional suffix needs. Scanned for the same reason, and it matters for the same
    // reason: `anyOf: [' ', '\t']` finds NO separator in a vertical-tab-separated line, so the suffix the verb was
    // told to strip stays attached to a payload the registry's tokenizer had already split correctly.
    private static int LastIndexOfWhiteSpace(ReadOnlySpan<char> span) {
        for (var index = (span.Length - 1); (index >= 0); index--) {
            if (char.IsWhiteSpace(c: span[index])) {
                return index;
            }
        }

        return -1;
    }
    // Removes ONE surrounding double-quote pair from a tail that is a single quoted token. A tail carrying any further
    // '"' is more than one token (`"a" "b"`) or an escaped run this method has no business re-tokenizing, and is
    // returned as written.
    private static string Unwrap(ReadOnlySpan<char> tail) {
        if (
            (tail.Length >= 2) &&
            (tail[0] == '"') &&
            (tail[^1] == '"') &&
            (tail[1..^1].IndexOf(value: '"') < 0)
        ) {
            return tail[1..^1].ToString();
        }

        return tail.ToString();
    }
}
