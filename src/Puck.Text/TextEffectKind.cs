namespace Puck.Text;

/// <summary>
/// The kind of per-glyph enrichment a <see cref="TextEffect"/> applies. Kinds split into three families that the
/// delight doctrine (DELIGHT ≠ MOTION) treats differently: <b>motion</b> kinds animate a glyph's transform and are
/// opt-outable for accessibility; <b>static</b> kinds (<see cref="Color"/>, <see cref="Weight"/>) carry the semantic,
/// event-driven emphasis that is the default delight; and <see cref="Reveal"/> is a deterministic typewriter that
/// paces content in rather than moving it. <see cref="TextEffect.IsMotion"/> classifies a kind.
/// </summary>
public enum TextEffectKind {
    /// <summary>No effect; the glyph renders unmodified (the identity channel).</summary>
    None,
    /// <summary>Motion: a two-axis sine shudder (decorrelated X and Y offsets).</summary>
    Shake,
    /// <summary>Motion: a travelling vertical sine wave rolling across the run.</summary>
    Wave,
    /// <summary>Motion: a rhythmic centered scale pulse.</summary>
    Pulse,
    /// <summary>Motion: a per-cycle hash-random two-axis jitter.</summary>
    Jitter,
    /// <summary>Motion: a staggered erode/materialize coverage sweep over a duration.</summary>
    Dissolve,
    /// <summary>Static: an absolute colour tint applied to the glyph — the semantic-emphasis workhorse.</summary>
    Color,
    /// <summary>Static: an emphasis weight bias (a fake-bold coverage/edge shift).</summary>
    Weight,
    /// <summary>Reveal: a deterministic per-glyph typewriter materialize, a pure function of the content tick.</summary>
    Reveal
}
