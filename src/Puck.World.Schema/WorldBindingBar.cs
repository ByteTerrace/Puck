using System.Text.Json.Serialization;
using Puck.Commands;

namespace Puck.World;

/// <summary>The authored layout of one on-screen binding bar. Lengths are fractions of the seat viewport's height;
/// <see cref="Scale"/> uniformly scales the slot cluster around its bottom-center anchor.</summary>
/// <param name="ButtonSize">The unscaled slot-plate size.</param>
/// <param name="CenterGap">The unscaled extra half-gap between the mirrored clusters.</param>
/// <param name="AnchorOffsetY">The anchor's lift above the viewport's bottom edge.</param>
/// <param name="GlyphOffsetRatio">The gamepad glyph's corner offset as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="GlyphSizeRatio">The gamepad glyph's size as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="Scale">The uniform cluster scale.</param>
public sealed record WorldBindingBarLayout(
    float ButtonSize,
    float CenterGap,
    float AnchorOffsetY,
    float GlyphOffsetRatio,
    float GlyphSizeRatio,
    float Scale
) {
    /// <summary>Gets the layout used when an overlay authors no binding-bar policy.</summary>
    public static WorldBindingBarLayout Default { get; } = new(
        AnchorOffsetY: (220f / 600f),
        ButtonSize: (45f / 600f),
        CenterGap: (60f / 600f),
        GlyphOffsetRatio: 0.4375f,
        GlyphSizeRatio: (24f / 45f),
        Scale: 1f
    );
}
/// <summary>The authored visibility and layout of the on-screen binding bar.</summary>
/// <param name="Enabled">Whether the bar is shown when no live override hides it.</param>
/// <param name="Layout">The bar layout; <see langword="null"/> uses <see cref="WorldBindingBarLayout.Default"/>.</param>
/// <param name="Visible">The bar's visibility condition over presentation facts, or <see langword="null"/> for always.</param>
public sealed record WorldBindingBarAuthoring(
    bool Enabled = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBindingBarLayout? Layout = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverlayPredicate? Visible = null
) {
    /// <summary>Gets the policy that preserves the binding bar's unauthored behavior.</summary>
    public static WorldBindingBarAuthoring Default { get; } = new();
    /// <summary>Gets the resolved authored layout.</summary>
    [JsonIgnore]
    public WorldBindingBarLayout ResolvedLayout => (Layout ?? WorldBindingBarLayout.Default);
}
/// <summary>One per-world binding overlay — a whole <see cref="BindingProfileDocument"/> layered over the engine
/// default beneath every seat's profile bindings, so a world can contextualize the controls (a kart world remapping a
/// lane, an RTS world adding a chorded command page) as data, never a client fork. Merged in order; the composed result
/// (default ⊕ every overlay) is what the validator compiles.</summary>
/// <param name="Id">The overlay's stable id — its mutation address (unique within the definition; carries no meaning
/// beyond identity).</param>
/// <param name="Document">The overlay binding document merged into the composed mapping.</param>
/// <param name="BindingBar">The on-screen bar policy carried with this binding layer; <see langword="null"/> preserves
/// the always-visible reference layout.</param>
public sealed record WorldBindingOverlay(
    string Id,
    BindingProfileDocument Document,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBindingBarAuthoring? BindingBar = null
);
