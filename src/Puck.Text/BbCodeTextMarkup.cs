using System.Text;

namespace Puck.Text;

/// <summary>
/// The human-authorable front-end for the enrichment grammar: a BBCode-style syntax (<c>[wave]…[/wave]</c>,
/// <c>[color=#ff5555]word[/color]</c>, <c>[weight=0.6]bold[/weight]</c>) that <see cref="Compile"/> lowers to the
/// robust control-char stream <see cref="TextEnrichmentTags"/> parses. Authors and content files type BBCode; the C0
/// control chars stay an implementation detail. Unknown or malformed brackets pass through verbatim, so ordinary text
/// containing <c>[</c> and <c>]</c> is safe.
/// <para>
/// Tag names resolve through <see cref="TextEnrichmentTags"/>'s single recognized-name table, so the front-end never
/// drifts from the parser. A start tag may carry a primary value (<c>[color=#f00]</c>) plus space-separated
/// <c>key=value</c> parameters (<c>[wave amplitude=4 frequency=1.5]</c>).
/// </para>
/// </summary>
public static class BbCodeTextMarkup {
    /// <summary>Compiles BBCode-style markup into the control-char stream <see cref="TextEnrichmentTags"/> parses.</summary>
    /// <param name="markup">The BBCode-style source.</param>
    /// <returns>The equivalent control-char stream; unrecognized brackets are preserved verbatim.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="markup"/> is <see langword="null"/>.</exception>
    public static string Compile(string markup) {
        ArgumentNullException.ThrowIfNull(argument: markup);

        var builder = new StringBuilder(capacity: markup.Length);
        var index = 0;

        while (index < markup.Length) {
            var character = markup[index];

            if (character != '[') {
                _ = builder.Append(value: character);
                index++;

                continue;
            }

            var closeIndex = markup.IndexOf(value: ']', startIndex: (index + 1));

            if (closeIndex < 0) {
                // No closing bracket — the rest is literal text.
                _ = builder.Append(value: markup[index..]);

                break;
            }

            var inner = markup[(index + 1)..closeIndex];

            if (TryCompileTag(inner: inner, compiled: out var compiled)) {
                _ = builder.Append(value: compiled);
            } else {
                // Not a recognized tag — pass the bracketed span through unchanged.
                _ = builder.Append(value: markup[index..(closeIndex + 1)]);
            }

            index = (closeIndex + 1);
        }

        return builder.ToString();
    }

    /// <summary>Compiles markup and enumerates its visible runes paired with the effect in force at each.</summary>
    /// <param name="markup">The BBCode-style source.</param>
    /// <returns>The enriched runes, ready for the enrichment-aware <see cref="TextLayout"/> overload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="markup"/> is <see langword="null"/>.</exception>
    public static IEnumerable<TextEffectRune> EnrichRunes(string markup) =>
        TextEnrichmentTags.EnumerateRichTextRunes(text: Compile(markup: markup));

    /// <summary>Strips all markup, returning just the visible text.</summary>
    /// <param name="markup">The BBCode-style source.</param>
    /// <returns>The plain text with every tag removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="markup"/> is <see langword="null"/>.</exception>
    public static string StripToPlainText(string markup) {
        var builder = new StringBuilder(capacity: markup.Length);

        foreach (var rune in TextEnrichmentTags.EnumerateVisibleRunes(text: Compile(markup: markup))) {
            _ = builder.Append(value: rune);
        }

        return builder.ToString();
    }

    private static bool TryCompileTag(string inner, out string compiled) {
        compiled = string.Empty;

        var trimmed = inner.Trim();

        if (trimmed.Length == 0) {
            return false;
        }

        // End tag: [/name].
        if (trimmed[0] == '/') {
            var endName = trimmed[1..].Trim().ToLowerInvariant();

            if (endName == "reset") {
                return false; // reset has no end form; leave literal.
            }

            var endKind = TextEnrichmentTags.ParseEffectKind(name: endName);

            if (endKind == TextEffectKind.None) {
                return false;
            }

            compiled = TextEnrichmentTags.CreateEndTag(kind: endKind);

            return true;
        }

        var tokens = trimmed.Split(separator: (char[]?)null, options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0) {
            return false;
        }

        var head = tokens[0];
        string? primaryValue = null;
        var equalsIndex = head.IndexOf(value: '=');

        if (equalsIndex >= 0) {
            primaryValue = head[(equalsIndex + 1)..];
            head = head[..equalsIndex];
        }

        var name = head.Trim().ToLowerInvariant();

        if (name == "reset") {
            compiled = TextEnrichmentTags.CreateResetTag();

            return true;
        }

        var kind = TextEnrichmentTags.ParseEffectKind(name: name);

        if (kind == TextEffectKind.None) {
            return false;
        }

        var parameters = new List<TextEnrichmentTagParameter>();

        if (!string.IsNullOrEmpty(value: primaryValue)) {
            parameters.Add(item: new TextEnrichmentTagParameter(Name: PrimaryParameterName(kind: kind), Value: primaryValue));
        }

        for (var index = 1; (index < tokens.Length); index++) {
            var token = tokens[index];
            var splitIndex = token.IndexOf(value: '=');

            if ((splitIndex <= 0) || (splitIndex == (token.Length - 1))) {
                continue;
            }

            parameters.Add(item: new TextEnrichmentTagParameter(Name: token[..splitIndex], Value: token[(splitIndex + 1)..]));
        }

        compiled = TextEnrichmentTags.CreateStartTag(kind: kind, parameters: [.. parameters]);

        return true;
    }

    // The parameter a bare [name=value] primary maps to, by effect kind.
    private static string PrimaryParameterName(TextEffectKind kind) =>
        kind switch {
            TextEffectKind.Color => "color",
            TextEffectKind.Weight => "amount",
            TextEffectKind.Reveal => "stagger",
            _ => "amplitude"
        };
}
