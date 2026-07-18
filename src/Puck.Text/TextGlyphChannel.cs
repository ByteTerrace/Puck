using System.Numerics;

namespace Puck.Text;

/// <summary>
/// The per-glyph enrichment channel: the transform/colour modulation one <see cref="TextEffect"/> produces for one
/// glyph at one content tick. It is deliberately tier-agnostic — every text tier (overlay, decal, world glyphs, the
/// diegetic terminal) consumes the same struct without knowing effect internals, so enrichment composes WITH
/// <see cref="TextLayout"/> rather than forking per tier. A tier applies whichever fields it can honour: a 1:1 CPU blit
/// uses <see cref="Offset"/>/<see cref="Coverage"/>/<see cref="Tint"/>/<see cref="WeightBias"/>, while a geometry or
/// decal tier additionally honours <see cref="Scale"/>.
/// </summary>
/// <param name="Offset">A per-glyph positional nudge, in the layout's scaled units (y-up, matching the placement plane).</param>
/// <param name="Scale">A centered scale multiplier (<c>1</c> = identity).</param>
/// <param name="Coverage">A coverage/reveal multiplier in <c>[0,1]</c> (<c>1</c> = fully shown, <c>0</c> = hidden).</param>
/// <param name="WeightBias">A signed emphasis bias applied to the glyph's coverage/edge (<c>0</c> = identity, positive = heavier).</param>
/// <param name="Tint">An absolute RGBA tint (each channel in <c>[0,1]</c>) applied when <see cref="HasTint"/> is set.</param>
/// <param name="HasTint">Whether <see cref="Tint"/> overrides the base text colour for this glyph.</param>
public readonly record struct TextGlyphChannel(
    Vector2 Offset,
    float Scale,
    float Coverage,
    float WeightBias,
    Vector4 Tint,
    bool HasTint
) {
    /// <summary>The identity channel: no offset, unit scale, full coverage, no weight bias, no tint.</summary>
    public static readonly TextGlyphChannel Identity = new(Coverage: 1.0f, HasTint: false, Offset: Vector2.Zero, Scale: 1.0f, Tint: Vector4.Zero, WeightBias: 0.0f);

    /// <summary>
    /// Resolves an effect into a per-glyph channel at a deterministic content tick. This is the one place time enters
    /// the enrichment model, and it enters as a caller-supplied <b>content tick</b> — never the wall clock, never RNG —
    /// so the whole animation is a pure function of tick and replays identically. Static kinds
    /// (colour, weight) and the reveal typewriter always resolve; the motion kinds are gated by
    /// <paramref name="motionEnabled"/> and settle to their rest state when motion is off (the reduced-motion contract:
    /// keep the semantic layer, drop continuous motion), with reveals completing instantly.
    /// </summary>
    /// <param name="effect">The effect assigned to the glyph (typically <see cref="TextGlyphPlacement.Effect"/>).</param>
    /// <param name="contentTick">The deterministic content tick (a monotonically advancing frame/step count).</param>
    /// <param name="ticksPerSecond">Ticks per content second, used to express the Hz-based motion frequencies; values &lt;= 0 treat one tick as one second.</param>
    /// <param name="glyphPhase">The glyph's per-glyph phase source (its baseline X in layout units — the travelling-wave stagger).</param>
    /// <param name="glyphIndex">The glyph's ordinal within the run (drives reveal stagger and dissolve seeding).</param>
    /// <param name="motionEnabled">Whether motion-class effects animate; when <see langword="false"/> they resolve to rest and reveals complete.</param>
    /// <param name="variables">Optional content-time channels for late-bound parameters.</param>
    /// <returns>The per-glyph channel to apply at layout consumption time.</returns>
    public static TextGlyphChannel Resolve(
        TextEffect effect,
        long contentTick,
        float ticksPerSecond,
        float glyphPhase,
        int glyphIndex = 0,
        bool motionEnabled = true,
        IReadOnlyList<TextEnrichmentVariable>? variables = null
    ) {
        if (effect.Kind == TextEffectKind.None) {
            return Identity;
        }

        // The animation clock: content seconds derived purely from the caller's content tick (deterministic).
        var seconds = ((ticksPerSecond > 0.0f) ? (contentTick / ticksPerSecond) : contentTick);

        // Static, always-applied enrichment (the delight layer that reduced-motion keeps).
        switch (effect.Kind) {
            case TextEffectKind.Color: {
                    return Identity with { Tint = (effect.TintColor ?? Vector4.One), HasTint = true };
                }
            case TextEffectKind.Weight: {
                    return Identity with { WeightBias = effect.AmplitudePixels.Evaluate(variables: variables) };
                }
            case TextEffectKind.Reveal: {
                    var duration = MathF.Max(x: 0.0001f, y: effect.DurationSeconds.Evaluate(variables: variables));
                    var stagger = MathF.Max(x: 0.0f, y: effect.Phase.Evaluate(variables: variables));
                    // Deterministic typewriter (rollback-safe): each glyph materializes on a staggered schedule.
                    var localProgress = Math.Clamp(value: ((seconds - (glyphIndex * stagger)) / duration), max: 1.0f, min: 0.0f);

                    return Identity with { Coverage = (motionEnabled ? localProgress : 1.0f) };
                }
            default: {
                    break;
                }
        }

        // Motion class — opt-outable. Off = settle to rest (identity).
        if (!motionEnabled) {
            return Identity;
        }

        var amplitude = effect.AmplitudePixels.Evaluate(variables: variables);
        var frequency = effect.FrequencyHz.Evaluate(variables: variables);
        var effectPhase = effect.Phase.Evaluate(variables: variables);
        var phase = (effectPhase + (glyphPhase * 0.17f));
        var cycle = ((seconds * MathF.Max(x: 0.0f, y: frequency)) + phase);
        var wave = MathF.Sin(x: (MathF.Tau * cycle));
        var secondaryWave = MathF.Cos(x: (MathF.Tau * (cycle * 1.37f)));

        return effect.Kind switch {
            TextEffectKind.Shake => Identity with { Offset = new Vector2(x: (amplitude * wave), y: (amplitude * secondaryWave)) },
            TextEffectKind.Wave => Identity with { Offset = new Vector2(x: 0.0f, y: (amplitude * wave)) },
            TextEffectKind.Pulse => Identity with { Scale = MathF.Max(x: 0.01f, y: (1.0f + (amplitude * wave))) },
            TextEffectKind.Jitter => ResolveJitter(amplitude: amplitude, cycle: cycle),
            TextEffectKind.Dissolve => ResolveDissolve(effect: effect, seconds: seconds, glyphPhase: glyphPhase, glyphIndex: glyphIndex, variables: variables),
            _ => Identity
        };
    }

    private static TextGlyphChannel ResolveJitter(float amplitude, float cycle) {
        var x = ((HashWave(value: cycle, salt: 12.9898f) * 2.0f) - 1.0f);
        var y = ((HashWave(value: cycle, salt: 78.233f) * 2.0f) - 1.0f);

        return Identity with { Offset = new Vector2(x: (amplitude * x), y: (amplitude * y)) };
    }
    private static TextGlyphChannel ResolveDissolve(TextEffect effect, float seconds, float glyphPhase, int glyphIndex, IReadOnlyList<TextEnrichmentVariable>? variables) {
        var duration = MathF.Max(x: 0.001f, y: effect.DurationSeconds.Evaluate(variables: variables));
        var effectPhase = effect.Phase.Evaluate(variables: variables);
        var progress = ((seconds % duration) / duration);
        var style = ((effect.DissolveStyle == TextEffectDissolveStyle.None) ? TextEffectDissolveStyle.Devilish : effect.DissolveStyle);
        var seed = CreateGlyphSeed(effectPhase: effectPhase, glyphPhase: glyphPhase, glyphIndex: glyphIndex);
        var stagger = ((style == TextEffectDissolveStyle.Sickly)
            ? Fract(value: (((seed * 0.53f) + (glyphIndex * 0.071f)) + (glyphPhase * 0.0037f)))
            : Fract(value: (((seed * 0.41f) + (glyphIndex * 0.113f)) + (glyphPhase * 0.0029f))));
        var localProgress = ((style == TextEffectDissolveStyle.Sickly)
            ? Math.Clamp(value: (progress + ((stagger - 0.35f) * 0.34f)), max: 1.0f, min: 0.0f)
            : Math.Clamp(value: (progress + ((stagger - 0.5f) * 0.24f)), max: 1.0f, min: 0.0f));

        return Identity with { Coverage = localProgress };
    }
    private static float CreateGlyphSeed(float effectPhase, float glyphPhase, int glyphIndex) =>
        HashWave(value: (((glyphIndex * 0.173f) + (glyphPhase * 0.019f)) + effectPhase), salt: 91.733f);
    private static float Fract(float value) =>
        (value - MathF.Floor(x: value));
    private static float HashWave(float value, float salt) =>
        Fract(value: (MathF.Sin(x: ((value + salt) * 43758.5453f)) * 143.759f));
}
