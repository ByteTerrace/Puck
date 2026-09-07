namespace Puck.World.Browser.Engine;

/// <summary>One validator message split into its leading path token (when the message spells one) and the remaining
/// text — the shape <c>Parse</c>/<c>ParseFragment</c>'s <c>errors[]</c> marshals as JSON.</summary>
/// <param name="Path">The message's leading dotted/indexed path token, or <see langword="null"/> when the message
/// opens with ordinary prose instead — most of <see cref="Puck.World.WorldDefinitionValidator"/>'s messages still do
/// (see <c>ValidatorMessagePathRatchetTests</c>).</param>
/// <param name="Message">The message, with a split-off path token's own leading text removed.</param>
public readonly record struct BrowserErrorPath(string? Path, string Message);

/// <summary>Splits a validator message's leading path token from its prose — a heuristic reading of the shape most
/// <see cref="Puck.World.WorldDefinitionValidator"/> messages already carry (<c>"bodies.localSeats -1 is outside
/// 0..4."</c>), not a change to how those messages are worded. A message that opens with plain prose
/// (<c>"an addon requires a name."</c>) carries no path; splitting stays honest about that rather than guessing one.</summary>
public static class BrowserErrorPaths {
    /// <summary>Splits <paramref name="message"/>'s leading path token, when it has one.</summary>
    /// <param name="message">One collected validator message.</param>
    /// <returns>The split path and remaining message.</returns>
    public static BrowserErrorPath Split(string message) {
        ArgumentNullException.ThrowIfNull(argument: message);

        var end = ScanPathToken(message: message);

        if (end <= 0) {
            return new BrowserErrorPath(Path: null, Message: message);
        }

        var path = message[..end];
        var rest = message[end..].TrimStart(trimChar: ' ');

        return new BrowserErrorPath(Path: path, Message: (rest.Length > 0 ? rest : message));
    }
    /// <summary>Splits and re-prefixes every collected message for a fragment composed under a host document,
    /// stripping the <c>&lt;alias&gt;_</c> namespace <see cref="Puck.World.WorldModuleNamespace"/> applied so the
    /// fragment author sees their own bare row names — the same names they authored, never the composed form.</summary>
    /// <param name="messages">The collected validator messages against the composed document.</param>
    /// <param name="alias">The alias the fragment composed under.</param>
    /// <returns>Each message split and stripped of its alias prefix, in order.</returns>
    public static IReadOnlyList<BrowserErrorPath> SplitFragment(IEnumerable<string> messages, string alias) {
        ArgumentNullException.ThrowIfNull(argument: messages);
        ArgumentException.ThrowIfNullOrEmpty(argument: alias);

        var prefix = $"{alias}_";
        var result = new List<BrowserErrorPath>();

        foreach (var message in messages) {
            var stripped = message.Replace(oldValue: prefix, newValue: string.Empty, comparisonType: StringComparison.Ordinal);

            result.Add(item: Split(message: stripped));
        }

        return result;
    }

    // A path token is one or more dotted/bracketed segments of ordinary identifier characters
    // (letters/digits/underscore, an optional [n] index) — e.g. "bodies.localSeats" or "screens[0].index" — ending at
    // the first space, quote, or end of string. A single bare word ("addon", "an") is not treated as a path: every
    // real path in this validator carries at least one '.' or one '[' (see WorldDefinitionValidator's own message
    // shapes), so a lone leading word is prose, not a path.
    private static int ScanPathToken(string message) {
        var sawSeparator = false;
        var index = 0;

        while (index < message.Length) {
            var c = message[index];

            if (char.IsAsciiLetterOrDigit(c: c) || (c is '_')) {
                index++;

                continue;
            }

            if (c is '.') {
                sawSeparator = true;
                index++;

                continue;
            }

            if (c is '[') {
                var close = message.IndexOf(value: ']', startIndex: index);

                if (close < 0) {
                    break;
                }

                sawSeparator = true;
                index = (close + 1);

                continue;
            }

            break;
        }

        return (sawSeparator ? index : 0);
    }
}
