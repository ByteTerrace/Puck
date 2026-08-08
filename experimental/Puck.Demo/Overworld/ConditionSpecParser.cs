using System.Globalization;
using Puck.Scene;

namespace Puck.Demo.Overworld;

/// <summary>
/// Parses the <c>condition.set</c> console verb's spec strings into the Scene win/reveal condition records — the parse
/// half of "the recursion" (a live re-forge of a cabinet's exit/victory gate). Extracted from
/// <see cref="OverworldFrameSource"/> so the spec-parsing types (NumberStyles / Guid / the exit-op scan) live HERE, not
/// on the frame source at its analyzer coupling ceiling. Both parsers are FORGIVING (a malformed spec returns
/// <see langword="false"/> so the caller can echo a usage line; nothing is ever thrown at frame time) and validate the
/// same well-formedness the document validator enforces, through the records' PUBLIC surface (their <c>Validate</c> is
/// internal to Puck.Scene). The cross-cabinet XOR consistency is DELIBERATELY not checked — a live edit may leave a meta
/// group transiently invalid (it simply won't fire, and the node warns), the re-validation policy the recursion needs.
/// </summary>
internal static class ConditionSpecParser {
    /// <summary>Parses an exit spec of the form <c>0xADDR&lt;op&gt;value</c> (e.g. <c>0xC004&gt;=1</c>) into a
    /// <see cref="BrickExitCondition"/>, validated against the document rules (a parseable WRAM address in
    /// [0xC000, 0xDFFF], a supported op, a byte-range value).</summary>
    /// <param name="spec">The exit spec string.</param>
    /// <param name="condition">The parsed condition on success.</param>
    /// <returns>Whether the spec parsed and validated.</returns>
    public static bool TryParseExit(string? spec, out BrickExitCondition condition) {
        condition = new BrickExitCondition();

        if (string.IsNullOrWhiteSpace(value: spec)) {
            return false;
        }

        // Find the op: the longest supported operator that appears after the address. Scanning the longest match first
        // means a single-char > / < never shadows the two-char >= / <= / == / != when both would match.
        var trimmed = spec.Trim();
        string? op = null;
        var opAt = -1;

        foreach (var candidate in BrickExitCondition.SupportedOps) {
            var at = trimmed.IndexOf(value: candidate, comparisonType: StringComparison.Ordinal);

            if ((at > 0) && ((op is null) || (candidate.Length > op.Length))) {
                op = candidate;
                opAt = at;
            }
        }

        if (op is null) {
            return false;
        }

        var address = trimmed[..opAt].Trim();
        var valueText = trimmed[(opAt + op.Length)..].Trim();

        if (!int.TryParse(s: valueText, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var value)) {
            return false;
        }

        condition = new BrickExitCondition {
            Address = address,
            Op = op,
            Value = value,
        };

        return (condition.TryParseAddress(address: out var parsed)
            && (parsed >= 0xC000) && (parsed <= 0xDFFF)
            && BrickExitCondition.SupportedOps.Contains(value: op, comparer: StringComparer.Ordinal)
            && (value >= 0) && (value <= 255));
    }

    /// <summary>Parses a victory spec: <c>solo target=&lt;guid&gt;</c> or <c>meta target=&lt;guid&gt; share=&lt;guid&gt;
    /// [group=&lt;g&gt;]</c>. Tokens after the mode word are order-free <c>key=value</c> pairs. Validates the
    /// mode/target/share well-formedness (mode supported, target a GUID, a meta needs a parseable share; a solo forbids
    /// share/group).</summary>
    /// <param name="mode">The victory shape word (<c>solo</c> or <c>meta</c>).</param>
    /// <param name="tokens">The tokens after the mode word.</param>
    /// <param name="condition">The parsed condition on success.</param>
    /// <returns>Whether the spec parsed and validated.</returns>
    public static bool TryParseVictory(string? mode, string[] tokens, out BrickVictoryCondition condition) {
        condition = new BrickVictoryCondition();

        if (!BrickVictoryCondition.SupportedModes.Contains(value: (mode ?? ""), comparer: StringComparer.OrdinalIgnoreCase)) {
            return false;
        }

        string? target = null;
        string? share = null;
        string? group = null;

        foreach (var token in tokens) {
            var eq = token.IndexOf(value: '=');

            if (eq <= 0) {
                return false;
            }

            var key = token[..eq];
            var val = token[(eq + 1)..];

            if (string.Equals(a: key, b: "target", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                target = val;
            } else if (string.Equals(a: key, b: "share", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                share = val;
            } else if (string.Equals(a: key, b: "group", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                group = val;
            } else {
                return false;
            }
        }

        if ((target is null) || !Guid.TryParse(input: target, result: out _)) {
            return false;
        }

        var isMeta = string.Equals(a: mode, b: "meta", comparisonType: StringComparison.OrdinalIgnoreCase);

        if (isMeta) {
            // A meta victory REQUIRES a parseable share (the value its game converges on).
            if ((share is null) || !Guid.TryParse(input: share, result: out _)) {
                return false;
            }
        } else if ((share is not null) || (group is not null)) {
            // A solo victory FORBIDS share/group (BrickVictoryCondition.Validate errors on either) — reject rather than
            // silently drop, so the live editor accepts exactly what the document loader accepts.
            return false;
        }

        condition = new BrickVictoryCondition {
            Group = (isMeta ? group : null),
            Mode = (isMeta ? "meta" : "solo"),
            Share = (isMeta ? share : null),
            Target = target,
        };

        return true;
    }
}
