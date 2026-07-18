namespace Puck.Text;

/// <summary>The kind of a <see cref="TextEnrichmentSegment"/> produced when walking a marked-up string for sanitization.</summary>
public enum TextEnrichmentSegmentKind {
    /// <summary>A visible rune that survives tag stripping.</summary>
    VisibleRune,
    /// <summary>A recognized enrichment tag (removable to recover plain text).</summary>
    Tag
}
