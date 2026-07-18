namespace Puck.Text;

/// <summary>
/// How a <see cref="TextEffectParameter"/> folds a late-bound <see cref="TextEnrichmentVariable"/> onto its base
/// value. Selected by the value string's binding sigil at parse time (see <see cref="TextEnrichmentTags"/>): a
/// trailing <c>+</c> is <see cref="Additive"/>, a trailing <c>*</c> is <see cref="Multiplicative"/>, and an empty
/// base is <see cref="Replacement"/>.
/// </summary>
public enum TextEffectParameterBindingMode {
    /// <summary>The bound variable multiplies the base value (the default).</summary>
    Multiplicative,
    /// <summary>The bound variable replaces the base value outright.</summary>
    Replacement,
    /// <summary>The bound variable is added to the base value.</summary>
    Additive
}
