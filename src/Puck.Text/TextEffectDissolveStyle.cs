namespace Puck.Text;

/// <summary>
/// The flavour of a <see cref="TextEffectKind.Dissolve"/> effect — it seeds the per-glyph stagger so a run dissolves
/// with character rather than uniformly. Purely a hash-seed selector; both styles are RNG-free and replay-safe.
/// </summary>
public enum TextEffectDissolveStyle {
    /// <summary>No style selected (the resolver treats this as <see cref="Devilish"/>).</summary>
    None,
    /// <summary>A fiery burn-in stagger.</summary>
    Devilish,
    /// <summary>A slower, uneven melt stagger.</summary>
    Sickly
}
