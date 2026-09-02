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
    /// The split is <see cref="char.IsWhiteSpace(char)"/> OUTSIDE a double-quoted run, which is what both tokenizers a
    /// submitted line can travel agree on. The whitespace rule is by category, matching the registry's own wire
    /// tokenizer exactly: a narrower set (space and tab) would return an empty tail for a vertical-tab-separated line
    /// the tokenizer had already split correctly, so the verb would answer with its usage refusal for a line it
    /// accepted. The quote rule matches System.CommandLine's splitter, which every quoted line falls through to: a
    /// leading address token spelled <c>"my path"</c> is ONE token there, and a quote-blind scan would strip half of it
    /// and leave the other half glued to the front of the tail.
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
                var separator = EndOfToken(span: span);

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
    /// <remarks>The trailing split is the same one the leading split uses, and for the same reason — the two ends have
    /// to agree with each other and with the tokenizer that COUNTED the tokens. A narrower space-and-tab scan finds no
    /// separator in a vertical-tab-separated line the registry's own tokenizer had already split correctly, so the
    /// suffix stays glued to the tail and the verb tries to parse <c>{"id":"hp"}\v3</c> as its payload; and a
    /// quote-blind scan disagrees with the parser that produced <paramref name="args"/> about where the trailing token
    /// begins, so <c>verb "{a}" "x y"</c> — two tokens by that count — was cut at the space INSIDE the trailing one and
    /// answered <c>"{a}" "x</c>.
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

            var start = LastTokenStart(span: span);

            if (start < 0) {
                return string.Empty;
            }

            span = span[..start];
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
        // The floor is "a token precedes the keyword", and what that costs depends on the address the verb spells:
        // args excludes the verb, so leadingTokens - 1 of them are address, and one more has to be there for the
        // keyword to be a flag rather than the tail itself. A hard-coded 2 is only that expression at leadingTokens 1
        // — the one caller today — and read `verb <address> force` as a flagged line with an EMPTY tail for anything
        // deeper, which is the opposite of the rule stated below.
        present = ((args.Count >= (leadingTokens + 1)) && args.Is(
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

    // THE ONE TOKEN RULE this reconstruction reads by, so both of its ends and the tokenizer that counted the line's
    // tokens agree about where a token begins. A token ends at the first whitespace OUTSIDE a double-quoted run:
    // whitespace by CATEGORY (scanned rather than looked up, because char.IsWhiteSpace is a Unicode category test and
    // not a listed set — the rule CommandRegistry's wire tokenizer splits on), and the quote toggle because a quoted
    // line reaches the handler through System.CommandLine's splitter, for which `"x y"` is one token.
    //
    // Answers the separator's index, or -1 when the token runs to the end of the span and nothing follows it.
    private static int EndOfToken(ReadOnlySpan<char> span) {
        var quoted = false;

        for (var index = 0; (index < span.Length); index++) {
            var character = span[index];

            if (character == '"') {
                quoted = !quoted;
            } else if (
                !quoted &&
                char.IsWhiteSpace(c: character)
            ) {
                return index;
            }
        }

        return -1;
    }
    // Where the span's LAST token begins, under EndOfToken's rule — the split a verb whose free-text tail sits BEFORE a
    // fixed positional suffix needs. Answers -1 when the span holds fewer than two tokens: the whole span is then the
    // suffix and there is no tail left in front of it, which is what an address longer than its line answers too.
    private static int LastTokenStart(ReadOnlySpan<char> span) {
        var count = 0;
        var index = 0;
        var start = -1;

        while (index < span.Length) {
            while (
                (index < span.Length) &&
                char.IsWhiteSpace(c: span[index])
            ) {
                index++;
            }

            if (index >= span.Length) {
                break;
            }

            var separator = EndOfToken(span: span[index..]);

            count++;
            start = index;
            index = ((separator < 0)
                ? span.Length
                : (index + separator));
        }

        return ((count >= 2)
            ? start
            : -1);
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
