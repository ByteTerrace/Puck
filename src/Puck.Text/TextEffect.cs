using System.Numerics;

namespace Puck.Text;

/// <summary>
/// A resolved enrichment effect assigned to a span of runes by <see cref="TextEnrichmentTags"/>. It is a value bundle
/// of the effect kind plus its (possibly late-bound) numeric parameters; <see cref="TextGlyphChannel.Resolve"/> turns
/// one of these plus a content tick into a per-glyph transform/colour channel. The parameters are reused across kinds:
/// <see cref="AmplitudePixels"/> is the shudder/wave amplitude or the weight amount, <see cref="FrequencyHz"/> the
/// cycles per second, <see cref="Phase"/> the phase offset or reveal stagger, and <see cref="DurationSeconds"/> the
/// dissolve/reveal duration.
/// </summary>
/// <param name="Kind">The effect kind.</param>
/// <param name="AmplitudePixels">The motion amplitude (offset/pulse) or, for <see cref="TextEffectKind.Weight"/>, the emphasis amount.</param>
/// <param name="FrequencyHz">The motion frequency, in cycles per content second.</param>
/// <param name="Phase">The phase offset, or the per-glyph stagger for <see cref="TextEffectKind.Reveal"/>.</param>
/// <param name="DurationSeconds">The dissolve/reveal duration, in content seconds.</param>
/// <param name="DissolveStyle">The dissolve flavour, when <see cref="Kind"/> is <see cref="TextEffectKind.Dissolve"/>.</param>
/// <param name="TintColor">The absolute RGBA tint for a <see cref="TextEffectKind.Color"/> effect (each channel in <c>[0,1]</c>), or <see langword="null"/> for none.</param>
public readonly record struct TextEffect(
    TextEffectKind Kind,
    TextEffectParameter AmplitudePixels,
    TextEffectParameter FrequencyHz,
    TextEffectParameter Phase,
    TextEffectParameter DurationSeconds,
    TextEffectDissolveStyle DissolveStyle = TextEffectDissolveStyle.None,
    Vector4? TintColor = null
) {
    /// <summary>The inert effect — <see cref="TextEffectKind.None"/> — that resolves to the identity channel.</summary>
    public static readonly TextEffect None = new(TextEffectKind.None, new(0.0f), new(0.0f), new(0.0f), new(1.0f));

    /// <summary>Whether this effect animates a glyph's transform over time and is therefore gated by the reduced-motion
    /// setting. Static kinds (<see cref="TextEffectKind.Color"/>, <see cref="TextEffectKind.Weight"/>) and the reveal
    /// typewriter are not motion; the four transform kinds and <see cref="TextEffectKind.Dissolve"/> are.</summary>
    public bool IsMotion => (Kind is
        TextEffectKind.Shake or
        TextEffectKind.Wave or
        TextEffectKind.Pulse or
        TextEffectKind.Jitter or
        TextEffectKind.Dissolve);
}
