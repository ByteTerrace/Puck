using System.Text;

namespace Puck.Text;

/// <summary>
/// One segment of a marked-up string, from <see cref="TextEnrichmentTags.EnumerateSanitizableSegments"/>: either a
/// visible rune or a recognized tag. Dropping the <see cref="TextEnrichmentSegmentKind.Tag"/> segments and keeping the
/// runes recovers the plain, unenriched text.
/// </summary>
/// <param name="Kind">Whether this segment is a visible rune or a tag.</param>
/// <param name="Rune">The visible rune, when <see cref="Kind"/> is <see cref="TextEnrichmentSegmentKind.VisibleRune"/>; otherwise the default.</param>
/// <param name="Text">The segment's text (the rune's string, or the tag's raw delimiters).</param>
public readonly record struct TextEnrichmentSegment(TextEnrichmentSegmentKind Kind, Rune Rune, string Text) {
    /// <summary>Creates a tag segment carrying its raw delimited text.</summary>
    /// <param name="text">The tag's raw text (including its control-char delimiters).</param>
    /// <returns>A <see cref="TextEnrichmentSegmentKind.Tag"/> segment.</returns>
    public static TextEnrichmentSegment Tag(string text) =>
        new(Kind: TextEnrichmentSegmentKind.Tag, Rune: default, Text: text);

    /// <summary>Creates a visible-rune segment.</summary>
    /// <param name="rune">The visible rune.</param>
    /// <returns>A <see cref="TextEnrichmentSegmentKind.VisibleRune"/> segment.</returns>
    public static TextEnrichmentSegment VisibleRune(Rune rune) =>
        new(Kind: TextEnrichmentSegmentKind.VisibleRune, Rune: rune, Text: rune.ToString());
}
