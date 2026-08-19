using System.Text.Json.Serialization;
using Puck.Commands;

namespace Puck.World;

/// <summary>The binding bar's compile-time capacity ceilings — sized to hold every world's authored bar without a
/// per-world overlay-reservation change (see <c>Puck.World.Client.WorldOverlayCapacity.FromSchema</c>).</summary>
public static class WorldBindingBarCapacity {
    /// <summary>The most stacked banks one bar authors — the ONE declaration the overlay reservation and the
    /// validator's bank ceiling both size from; raising it here reprices the reservation, nowhere else.</summary>
    public const int MaxBanks = 5;
    /// <summary>The most modifier definitions one COMPOSED binding profile carries — declared rows PLUS the
    /// compiler-synthesized entry every distinct chord/held source token becomes. The overlay feed's per-seat pip
    /// reservation is sized by this, and the validator refuses a composed profile past it — the compose path
    /// clamps to its destination, so an unvalidated overflow would drop pips silently.</summary>
    public const int MaxModifiers = 16;
}

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
/// <summary>One stacked binding-bar bank: the page it renders and where it renders relative to the bar's shared
/// anchor. Several banks of the SAME slot set render simultaneously, each showing what that bank's OWN page binds —
/// the WoW-addon original's "five banks of one compass" idea (resting, LT, RT, LT&gt;RT, RT&gt;LT are the natural
/// five, though a bank may name any page).</summary>
/// <param name="Id">The bank's stable id — its mutation address (unique within the authoring row).</param>
/// <param name="PageId">The <c>BindingPageDefinition.Id</c> this bank renders — validated to exist somewhere in the
/// composed binding profile.</param>
/// <param name="OffsetX">This bank's horizontal displacement from the bar's shared anchor, in region-height units
/// (the same unit <see cref="WorldBindingBarLayout"/>'s lengths use).</param>
/// <param name="OffsetY">This bank's vertical displacement, region-height units, Y DOWN (positive moves toward the
/// bottom of the seat's viewport).</param>
/// <param name="Alpha">This bank's opacity when it is NOT the seat's currently active page.</param>
/// <param name="ActiveAlpha">This bank's opacity when it IS the seat's currently active page; <see langword="null"/>
/// draws fully opaque (1.0) while active.</param>
public sealed record WorldBindingBarBank(
    string Id,
    string PageId,
    float OffsetX,
    float OffsetY,
    float Alpha,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ActiveAlpha = null
);
/// <summary>The authored visibility, layout, and slot vocabulary of the on-screen binding bar. Absence of a whole
/// <see cref="WorldBindingBarAuthoring"/> row (no identity, no world authoring) draws no bar at all — see
/// <see cref="Absent"/> — every field below, including the tuning <see cref="WorldBindingBarLayout"/> once authoring
/// exists, is document data; nothing here falls back to a baked C# default.</summary>
/// <param name="Enabled">Whether the bar is shown when no live override hides it.</param>
/// <param name="Text">Whether the bar draws the ATLAS TEXT it composes — a badge whose icon row carries a
/// <c>Label</c> (LB/RB, LT/RT, LS/RS, the menu trio, the exotics), the active page's name under the modifier pips,
/// and the chord-hint lines above them. <see langword="false"/> drops every label-content badge outright and leaves
/// a purely pictographic bar: every plate, the glyph-content badges (the d-pad arrows and the face-position marks),
/// the bound actions' icons, and the modifier pips all still draw.</param>
/// <param name="SlotSet">The physical buttons this bar shows, in authored order (an exotic button's left-to-right
/// position in its row follows this order) — every name validated against the full <c>GamepadButtons</c> catalog by
/// name, unique, so the catalog itself bounds the count (the reservation sizes from
/// <c>Puck.Input.Devices.GamepadButtonCatalog.Count</c>).</param>
/// <param name="Banks">The stacked banks — at least one, at most <see cref="WorldBindingBarCapacity.MaxBanks"/>,
/// unique ids, each naming a page the composed binding profile actually declares.</param>
/// <param name="HideUnbound">Whether a slot with no bound act on its bank's page should not render at all, rather
/// than drawing the DISABLED tier-0 plate. A player's own <c>BindingBarPreferences.HideUnbound</c> overrides this.</param>
/// <param name="MultiSeatAlpha">The opacity every joined seat's bar renders at while two or more seats are joined —
/// the split-screen quieting lever, multiplied into each bank's own alpha; 1 keeps multi-seat bars fully
/// opaque.</param>
/// <param name="Layout">The bar's tuning; <see langword="null"/> uses <see cref="WorldBindingBarLayout.Default"/>.</param>
/// <param name="Visible">The bar's visibility condition over presentation facts, or <see langword="null"/> for always.</param>
public sealed record WorldBindingBarAuthoring(
    IReadOnlyList<string> SlotSet,
    IReadOnlyList<WorldBindingBarBank> Banks,
    bool Enabled = true,
    bool Text = true,
    bool HideUnbound = false,
    float MultiSeatAlpha = 1f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBindingBarLayout? Layout = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverlayPredicate? Visible = null
) {
    /// <summary>Gets the policy applied when NEITHER an identity nor the world authors a binding-bar row — absence
    /// draws no bar at all, never a baked-in look.</summary>
    public static WorldBindingBarAuthoring Absent { get; } = new(
        Banks: [],
        Enabled: false,
        SlotSet: [],
        Text: false
    );
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
