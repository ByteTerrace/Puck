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
    /// <param name="context">The invoking context, whose <see cref="CommandContext.Text"/> carries the raw line.</param>
    /// <param name="args">The tokenized arguments, the fallback when no raw line exists (a bound or programmatic call).</param>
    /// <param name="tokens">How many leading tokens to strip, the verb included.</param>
    /// <returns>The text after those tokens, or <see cref="string.Empty"/> when the line carries fewer.</returns>
    public static string RawAfter(CommandContext context, in WireArgs args, int tokens) {
        if (context.Text is { } text) {
            var span = text.AsSpan().TrimStart();

            for (var skip = 0; (skip < tokens); skip++) {
                var separator = span.IndexOfAny(
                    value0: ' ',
                    value1: '\t'
                );

                if (separator < 0) {
                    return string.Empty;
                }

                span = span[(separator + 1)..].TrimStart();
            }

            return span.Trim().ToString();
        }

        return args.Tail(start: (tokens - 1));
    }
}
