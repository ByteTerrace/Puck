using System.Text;

namespace Puck.Text;

/// <summary>
/// One visible rune paired with the enrichment effect in force at its position — the output of
/// <see cref="TextEnrichmentTags.EnumerateRichTextRunes"/> and the input to the enrichment-aware
/// <see cref="TextLayout.Layout(FontAtlas, IEnumerable{TextEffectRune}, float, float?)"/> overload. Layout carries the
/// <see cref="Effect"/> onto each placement so a downstream tier can resolve its per-glyph channel.
/// </summary>
/// <param name="Rune">The visible Unicode scalar.</param>
/// <param name="Effect">The effect in force at this rune (<see cref="TextEffect.None"/> when unenriched).</param>
public readonly record struct TextEffectRune(Rune Rune, TextEffect Effect);
