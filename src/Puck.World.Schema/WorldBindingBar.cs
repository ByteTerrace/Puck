using System.Text.Json.Serialization;
using Puck.Commands;

namespace Puck.World;

/// <summary>The binding bar's compile-time capacity ceilings — sized to hold every world's authored bar without a
/// per-world overlay-reservation change (see <c>Puck.World.Client.WorldOverlayCapacity.FromSchema</c>).</summary>
public static class WorldBindingBarCapacity {
    /// <summary>The most stacked banks one bar authors — the ONE declaration the overlay reservation and the
    /// validator's bank ceiling both size from; raising it here reprices the reservation, nowhere else.</summary>
    public const int MaxBanks = 5;
    /// <summary>The most modifier indicators one COMPOSED binding profile carries — declared rows PLUS the
    /// compiler-synthesized entry every distinct chord/held source token becomes. This one number sizes the whole
    /// modifier path: the overlay feed's per-seat modifier array, the overlay channel's lease reservation (crossed as
    /// <c>Puck.Overlays.OverlayCapacity.BindingBarMaxModifiers</c> through
    /// <c>Puck.World.Client.WorldOverlayCapacity.FromSchema</c>), the document validator's boot-time count
    /// (<c>CompiledBindingProfile.Modifiers.Count ≤ MaxModifiers</c>), and the runtime compose door. Raising it here
    /// reprices every one of them together.</summary>
    public const int MaxModifiers = 16;
    /// <summary>The most slots one bar's authored slot set names — the ONE declaration the overlay reservation
    /// (crossed as <c>Puck.Overlays.OverlayCapacity.BindingBarMaxSlotsPerBank</c> through
    /// <c>Puck.World.Client.WorldOverlayCapacity.FromSchema</c>), the feed's per-seat slot array, and the validator's
    /// slot-set ceiling all size from. The slot set names INPUT SOURCES, not gamepad flags, so no device enum bounds
    /// it any more.</summary>
    public const int MaxSlots = 32;
}

/// <summary>The authored layout of one on-screen binding bar. Every field but <see cref="Scale"/> is an OPTIONAL
/// override of the engine's resolved default (the <c>Default*</c> constants below): lengths are fractions of the seat
/// viewport's height, every <c>*Ratio</c>/<c>*Lift</c>/<c>*Spacing</c> is a multiple of the scaled button size, and
/// every <c>*MinPx</c> is a device-pixel floor. <see cref="Scale"/> uniformly scales the slot cluster around its
/// bottom-center anchor.</summary>
/// <param name="Scale">The uniform cluster scale.</param>
/// <param name="ButtonSize">The unscaled slot-plate size.</param>
/// <param name="CenterGap">The unscaled extra half-gap between the mirrored clusters.</param>
/// <param name="AnchorOffsetY">The anchor's lift above the viewport's bottom edge.</param>
/// <param name="GlyphOffsetRatio">The badge's corner offset as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="GlyphSizeRatio">The badge's size as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="CenterRowLift">The menu row's lift above the anchor, in scaled button sizes.</param>
/// <param name="CenterSlotSpacing">The menu row's slot pitch, in scaled button sizes.</param>
/// <param name="ExoticRowLift">The exotics row's lift above the anchor, in scaled button sizes.</param>
/// <param name="ExoticSlotSpacing">The exotics row's slot pitch, in scaled button sizes.</param>
/// <param name="BadgeCorner">The fixed corner direction a menu/exotics slot's badge nudges toward (the compass
/// categories take their direction from the pad pictogram geometry instead).</param>
/// <param name="ModifierHalfRatio">The modifier indicator's plate half-extent, in scaled button sizes.</param>
/// <param name="ModifierSpacingRatio">The modifier indicators' pitch, in scaled button sizes.</param>
/// <param name="ModifierGlyphRatio">The modifier badge's half-extent, as a fraction of the modifier plate half.</param>
/// <param name="LabelCellRatio">The page label's glyph-cell height, as a fraction of the modifier plate half.</param>
/// <param name="LabelCellMinPx">The page label's glyph-cell floor, px.</param>
/// <param name="LabelGapRatio">The page label's drop below the anchor, as a fraction of the modifier plate half.</param>
/// <param name="HintCellRatio">A chord-hint line's glyph-cell height, as a fraction of the modifier plate half.</param>
/// <param name="HintCellMinPx">A chord-hint line's glyph-cell floor, px.</param>
/// <param name="HintLineStepRatio">The chord-hint line pitch, as a fraction of the hint cell height.</param>
/// <param name="HintBaseGapRatio">The hint stack's lift above the anchor, as a fraction of the modifier plate half.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldBindingBarLayout(
    float Scale = 1f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ButtonSize = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? CenterGap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? AnchorOffsetY = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? GlyphOffsetRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? GlyphSizeRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? CenterRowLift = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? CenterSlotSpacing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ExoticRowLift = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ExoticSlotSpacing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? BadgeCorner = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ModifierHalfRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ModifierSpacingRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ModifierGlyphRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? LabelCellRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? LabelCellMinPx = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? LabelGapRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? HintCellRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? HintCellMinPx = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? HintLineStepRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? HintBaseGapRatio = null
) {
    /// <summary>The resolved <see cref="AnchorOffsetY"/> an unauthored bar takes (220/600).</summary>
    public const float DefaultAnchorOffsetY = (220f / 600f);
    /// <summary>The resolved <see cref="BadgeCorner"/> an unauthored bar takes.</summary>
    public const float DefaultBadgeCorner = 1f;
    /// <summary>The resolved <see cref="ButtonSize"/> an unauthored bar takes (45/600).</summary>
    public const float DefaultButtonSize = (45f / 600f);
    /// <summary>The resolved <see cref="CenterGap"/> an unauthored bar takes (60/600).</summary>
    public const float DefaultCenterGap = (60f / 600f);
    /// <summary>The resolved <see cref="CenterRowLift"/> an unauthored bar takes.</summary>
    public const float DefaultCenterRowLift = 1.9f;
    /// <summary>The resolved <see cref="CenterSlotSpacing"/> an unauthored bar takes.</summary>
    public const float DefaultCenterSlotSpacing = 1.15f;
    /// <summary>The resolved <see cref="ExoticRowLift"/> an unauthored bar takes — clear of the menu row.</summary>
    public const float DefaultExoticRowLift = (DefaultCenterRowLift + 1.7f);
    /// <summary>The resolved <see cref="ExoticSlotSpacing"/> an unauthored bar takes.</summary>
    public const float DefaultExoticSlotSpacing = 1.15f;
    /// <summary>The resolved <see cref="GlyphOffsetRatio"/> an unauthored bar takes.</summary>
    public const float DefaultGlyphOffsetRatio = 0.4375f;
    /// <summary>The resolved <see cref="GlyphSizeRatio"/> an unauthored bar takes (24/45).</summary>
    public const float DefaultGlyphSizeRatio = (24f / 45f);
    /// <summary>The resolved <see cref="HintBaseGapRatio"/> an unauthored bar takes.</summary>
    public const float DefaultHintBaseGapRatio = 2.2f;
    /// <summary>The resolved <see cref="HintCellMinPx"/> an unauthored bar takes.</summary>
    public const float DefaultHintCellMinPx = 10f;
    /// <summary>The resolved <see cref="HintCellRatio"/> an unauthored bar takes.</summary>
    public const float DefaultHintCellRatio = 1.6f;
    /// <summary>The resolved <see cref="HintLineStepRatio"/> an unauthored bar takes.</summary>
    public const float DefaultHintLineStepRatio = 1.3f;
    /// <summary>The resolved <see cref="LabelCellMinPx"/> an unauthored bar takes.</summary>
    public const float DefaultLabelCellMinPx = 12f;
    /// <summary>The resolved <see cref="LabelCellRatio"/> an unauthored bar takes.</summary>
    public const float DefaultLabelCellRatio = 1.9f;
    /// <summary>The resolved <see cref="LabelGapRatio"/> an unauthored bar takes.</summary>
    public const float DefaultLabelGapRatio = 1.4f;
    /// <summary>The resolved <see cref="ModifierGlyphRatio"/> an unauthored bar takes.</summary>
    public const float DefaultModifierGlyphRatio = 0.8f;
    /// <summary>The resolved <see cref="ModifierHalfRatio"/> an unauthored bar takes.</summary>
    public const float DefaultModifierHalfRatio = 0.35f;
    /// <summary>The resolved <see cref="ModifierSpacingRatio"/> an unauthored bar takes.</summary>
    public const float DefaultModifierSpacingRatio = 1.1f;

    /// <summary>Gets the tuning an authored binding-bar row uses when it omits its own <c>layout</c> — every override
    /// absent, so every field resolves to its <c>Default*</c> constant (a wholly absent bar row draws nothing; see
    /// <see cref="WorldBindingBarAuthoring.Absent"/>, whose disabled state hides it before this tuning is read).</summary>
    public static WorldBindingBarLayout Default { get; } = new();

    /// <summary>Gets the resolved anchor lift.</summary>
    [JsonIgnore]
    public float ResolvedAnchorOffsetY => (AnchorOffsetY ?? DefaultAnchorOffsetY);
    /// <summary>Gets the resolved fixed badge-corner direction.</summary>
    [JsonIgnore]
    public float ResolvedBadgeCorner => (BadgeCorner ?? DefaultBadgeCorner);
    /// <summary>Gets the resolved slot-plate size.</summary>
    [JsonIgnore]
    public float ResolvedButtonSize => (ButtonSize ?? DefaultButtonSize);
    /// <summary>Gets the resolved cluster half-gap.</summary>
    [JsonIgnore]
    public float ResolvedCenterGap => (CenterGap ?? DefaultCenterGap);
    /// <summary>Gets the resolved menu-row lift.</summary>
    [JsonIgnore]
    public float ResolvedCenterRowLift => (CenterRowLift ?? DefaultCenterRowLift);
    /// <summary>Gets the resolved menu-row slot pitch.</summary>
    [JsonIgnore]
    public float ResolvedCenterSlotSpacing => (CenterSlotSpacing ?? DefaultCenterSlotSpacing);
    /// <summary>Gets the resolved exotics-row lift.</summary>
    [JsonIgnore]
    public float ResolvedExoticRowLift => (ExoticRowLift ?? DefaultExoticRowLift);
    /// <summary>Gets the resolved exotics-row slot pitch.</summary>
    [JsonIgnore]
    public float ResolvedExoticSlotSpacing => (ExoticSlotSpacing ?? DefaultExoticSlotSpacing);
    /// <summary>Gets the resolved badge corner offset ratio.</summary>
    [JsonIgnore]
    public float ResolvedGlyphOffsetRatio => (GlyphOffsetRatio ?? DefaultGlyphOffsetRatio);
    /// <summary>Gets the resolved badge size ratio.</summary>
    [JsonIgnore]
    public float ResolvedGlyphSizeRatio => (GlyphSizeRatio ?? DefaultGlyphSizeRatio);
    /// <summary>Gets the resolved hint-stack lift.</summary>
    [JsonIgnore]
    public float ResolvedHintBaseGapRatio => (HintBaseGapRatio ?? DefaultHintBaseGapRatio);
    /// <summary>Gets the resolved hint glyph-cell floor.</summary>
    [JsonIgnore]
    public float ResolvedHintCellMinPx => (HintCellMinPx ?? DefaultHintCellMinPx);
    /// <summary>Gets the resolved hint glyph-cell ratio.</summary>
    [JsonIgnore]
    public float ResolvedHintCellRatio => (HintCellRatio ?? DefaultHintCellRatio);
    /// <summary>Gets the resolved hint line pitch.</summary>
    [JsonIgnore]
    public float ResolvedHintLineStepRatio => (HintLineStepRatio ?? DefaultHintLineStepRatio);
    /// <summary>Gets the resolved page-label glyph-cell floor.</summary>
    [JsonIgnore]
    public float ResolvedLabelCellMinPx => (LabelCellMinPx ?? DefaultLabelCellMinPx);
    /// <summary>Gets the resolved page-label glyph-cell ratio.</summary>
    [JsonIgnore]
    public float ResolvedLabelCellRatio => (LabelCellRatio ?? DefaultLabelCellRatio);
    /// <summary>Gets the resolved page-label drop.</summary>
    [JsonIgnore]
    public float ResolvedLabelGapRatio => (LabelGapRatio ?? DefaultLabelGapRatio);
    /// <summary>Gets the resolved modifier badge ratio.</summary>
    [JsonIgnore]
    public float ResolvedModifierGlyphRatio => (ModifierGlyphRatio ?? DefaultModifierGlyphRatio);
    /// <summary>Gets the resolved modifier plate half-extent ratio.</summary>
    [JsonIgnore]
    public float ResolvedModifierHalfRatio => (ModifierHalfRatio ?? DefaultModifierHalfRatio);
    /// <summary>Gets the resolved modifier pitch.</summary>
    [JsonIgnore]
    public float ResolvedModifierSpacingRatio => (ModifierSpacingRatio ?? DefaultModifierSpacingRatio);
}
/// <summary>One stacked binding-bar bank: the page it renders and its position in the stack. Several banks of the
/// SAME slot set render simultaneously, each showing what that bank's OWN page binds — the WoW-addon original's
/// "five banks of one compass" idea (resting, LT, RT, LT&gt;RT, RT&gt;LT are the natural five, though a bank may name
/// any page). The engine ARRANGES the stack from <paramref name="Order"/> alone, pitching it off the authored theme's
/// spacing grid; the two offsets exist only for a world that wants to place one bank by hand.</summary>
/// <param name="Id">The bank's stable id — its mutation address (unique within the authoring row).</param>
/// <param name="PageId">The <c>BindingPageDefinition.Id</c> this bank renders — validated to exist somewhere in the
/// composed binding profile.</param>
/// <param name="Order">This bank's place in the derived stack (unique within the authoring row): 0 sits on the bar's
/// shared anchor, and each higher order fans one step further out and up.</param>
/// <param name="Alpha">This bank's opacity when it is NOT the seat's currently active page.</param>
/// <param name="ActiveAlpha">This bank's opacity when it IS the seat's currently active page; <see langword="null"/>
/// draws fully opaque (1.0) while active.</param>
/// <param name="OffsetX">Overrides the derived horizontal displacement from the bar's shared anchor, in region-height
/// units; <see langword="null"/> takes the derived arrangement. Authoring one offset overrides BOTH axes' derivation
/// only for the axis it names.</param>
/// <param name="OffsetY">Overrides the derived vertical displacement, region-height units, Y DOWN (positive moves
/// toward the bottom of the seat's viewport); <see langword="null"/> takes the derived arrangement.</param>
public sealed record WorldBindingBarBank(
    string Id,
    string PageId,
    int Order,
    float Alpha,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ActiveAlpha = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? OffsetX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? OffsetY = null
);
/// <summary>The authored visibility, layout, and slot vocabulary of the on-screen binding bar. Absence of a whole
/// <see cref="WorldBindingBarAuthoring"/> row (no identity, no world authoring) draws no bar at all — see
/// <see cref="Absent"/>. Every field below is document data with one exception: once an authoring row exists but omits
/// its own <see cref="Layout"/>, the tuning falls back to <see cref="WorldBindingBarLayout.Default"/> — the sole baked
/// C# default in the bar model.</summary>
/// <param name="Enabled">Whether the bar is shown when no live override hides it.</param>
/// <param name="Text">Whether the bar draws the ATLAS TEXT it composes — a badge whose icon row carries a
/// <c>Label</c> (LB/RB, LT/RT, LS/RS, the menu trio, the exotics), the active page's name under the modifier
/// indicators, and the chord-hint lines above them. <see langword="false"/> drops every label-content badge outright
/// and leaves a purely pictographic bar: every plate, the glyph-content badges (the d-pad arrows and the face-position
/// marks), the bound actions' icons, and the modifier indicators all still draw.</param>
/// <param name="Modifiers">Whether the bar draws the modifier indicator row on its anchor line — one plate per
/// modifier the composed profile carries (declared rows plus every chord/held token the compiler synthesizes), lit
/// while held. <see langword="false"/> drops the row and leaves the slot clusters alone; the chord hints and page
/// label are <paramref name="Text"/>'s, not this one's.</param>
/// <param name="SlotSet">The physical controls this bar shows, by INPUT SOURCE ID (<c>gamepad.buttonSouth</c>,
/// <c>mouse.button1</c>, …) in authored order (an exotic slot's left-to-right position in its row follows this
/// order) — the same vocabulary a binding entry's <c>sources</c> speak, every id validated against the engine's
/// input-source catalog, unique, at most <see cref="WorldBindingBarCapacity.MaxSlots"/>.</param>
/// <param name="Banks">The stacked banks — at least one, at most <see cref="WorldBindingBarCapacity.MaxBanks"/>,
/// unique ids, each naming a page the composed binding profile actually declares.</param>
/// <param name="HideUnbound">Whether a slot with no bound act on its bank's page should not render at all, rather
/// than drawing the DISABLED tier-0 plate. A player's own <c>BindingBarPreferences.HideUnbound</c> overrides this.</param>
/// <param name="MultiSeatAlpha">The opacity every joined seat's bar renders at while two or more seats are joined —
/// the split-screen quieting lever, multiplied into each bank's own alpha; 1 keeps multi-seat bars fully
/// opaque.</param>
/// <param name="IconRow">The state row a slot's icon resolves through, spelled <c>state.&lt;row&gt;</c> — the row's
/// CELL KEY is the bound row's <c>id</c> when it authors one, else its action (<c>command</c> or <c>channel</c>;
/// a dotted command name therefore needs an <c>id</c>, since a cell key holds no dot), and its value is
/// an <c>icons</c> name. The engine holds no action-to-icon vocabulary of its own: the association is ordinary
/// authored state, so it is written, read, and MUTATED like any other state (<c>world.state.cell.set</c>) — a spell
/// minted at runtime gets its icon by writing a cell, never by reshaping the document. <see langword="null"/>
/// resolves no slot icons at all; a key the row does not carry simply draws no icon.</param>
/// <param name="Layout">The bar's tuning; <see langword="null"/> uses <see cref="WorldBindingBarLayout.Default"/>.</param>
/// <param name="Visible">The bar's visibility condition over presentation facts, or <see langword="null"/> for always.</param>
public sealed record WorldBindingBarAuthoring(
    IReadOnlyList<string> SlotSet,
    IReadOnlyList<WorldBindingBarBank> Banks,
    bool Enabled = true,
    bool Text = true,
    bool Modifiers = true,
    bool HideUnbound = false,
    float MultiSeatAlpha = 1f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IconRow = null,
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
/// <param name="BindingBar">The on-screen bar policy carried with this binding layer; <see langword="null"/> carries no
/// policy on this layer, so bar resolution falls through to the world-authored policy, and to
/// <see cref="WorldBindingBarAuthoring.Absent"/> (no bar drawn) when neither an identity nor the world authors one.</param>
public sealed record WorldBindingOverlay(
    string Id,
    BindingProfileDocument Document,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBindingBarAuthoring? BindingBar = null
);
