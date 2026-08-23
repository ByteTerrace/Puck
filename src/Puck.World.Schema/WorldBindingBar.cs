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

/// <summary>The viewport edge a bar hangs from.</summary>
public enum WorldBindingBarEdge {
    /// <summary>The bottom edge; the bar is centered left-to-right and its lowest plate sits at the margin.</summary>
    Bottom,
    /// <summary>The top edge; centered left-to-right, highest plate at the margin.</summary>
    Top,
    /// <summary>The left edge; centered top-to-bottom, leftmost plate at the margin.</summary>
    Left,
    /// <summary>The right edge; centered top-to-bottom, rightmost plate at the margin.</summary>
    Right,
}
/// <summary>Where a bar hangs: a viewport edge and how far in from it. Every bank anchored to the SAME edge and
/// margin shares one frame — their plates are laid out together on one pitch grid and the nearest plate of the whole
/// group sits at the margin — so a nested crossbar is three banks on one anchor, and a strip with side columns is
/// three banks on three.</summary>
/// <param name="Edge">The viewport edge.</param>
/// <param name="Margin">The gap between that edge and the nearest plate edge of everything anchored here, as a
/// fraction of the viewport's extent ALONG that edge's axis (height for top/bottom, width for left/right) — so 0.025
/// reads as "2.5% in" on any aspect. Along the other axis the group is centered.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldBindingBarAnchor(WorldBindingBarEdge Edge = WorldBindingBarEdge.Bottom, float Margin = 0f);
/// <summary>One bank's place on a bar, in button pitches (x right, y UP) — the layout's half of a bank, beside the
/// bank row's identity/page/order/alpha half. A bank is "just a bar": it may hang from its own edge and carry its own
/// plate table, or share the layout's.</summary>
/// <param name="OffsetX">Pitches right of the anchor.</param>
/// <param name="OffsetY">Pitches above the anchor.</param>
/// <param name="Mirror">Whether <paramref name="OffsetX"/> is applied OUTWARD per plate — a plate left of the anchor
/// moves left, one right of it moves right, one on the anchor line does not move — so a two-cluster bar fans its
/// wings like a pair of mirrored hands. <see langword="false"/> translates the whole bank as one piece.</param>
/// <param name="Anchor">Where THIS bank hangs, or <see langword="null"/> to share the layout's anchor (and so its
/// frame: the offsets then nest it against the other banks there).</param>
/// <param name="Slots">This bank's own plate table, or <see langword="null"/> to share the layout's — a side column
/// and a bottom strip show the same controls in different shapes.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldBindingBarBankPlacement(
    float OffsetX = 0f,
    float OffsetY = 0f,
    bool Mirror = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBindingBarAnchor? Anchor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldBindingBarSlotPlacement>? Slots = null
);
/// <summary>One physical control's place on a bar, in button pitches from the bar's anchor — x right, y UP. The unit
/// a bank's offset shares, so an author lays a bar out on one grid.</summary>
/// <param name="Source">The physical control's input source id (a <c>slotSet</c> member).</param>
/// <param name="X">Pitches right of the anchor (negative = left).</param>
/// <param name="Y">Pitches above the anchor (negative = below).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldBindingBarSlotPlacement(string Source, float X, float Y);
/// <summary>The authored layout of one on-screen binding bar. Every field but <see cref="Scale"/> is an OPTIONAL
/// override of the engine's resolved default (the <c>Default*</c> constants below): lengths are fractions of the seat
/// viewport's height, every <c>*Ratio</c>/<c>*Lift</c>/<c>*Spacing</c> is a multiple of the scaled button size, and
/// every <c>*MinPx</c> is a device-pixel floor. <see cref="Scale"/> uniformly scales the slot cluster around its
/// bottom-center anchor.</summary>
/// <param name="Scale">The uniform cluster scale.</param>
/// <param name="ButtonSize">The unscaled slot-plate size.</param>
/// <param name="Anchor">Where the bar hangs — the edge and margin every bank without its own anchor shares.</param>
/// <param name="GlyphOffsetRatio">The badge's corner offset as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="GlyphSizeRatio">The badge's size as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="UnplacedRowLift">The row every slot NOT named in <paramref name="Slots"/> falls into: its lift above
/// the anchor, in scaled button sizes.</param>
/// <param name="UnplacedSlotSpacing">The unplaced row's slot pitch, in scaled button sizes; the row runs left to
/// right in <c>slotSet</c> order, centered on the anchor.</param>
/// <param name="Banks">Where each bank sits, by bank id, in the same pitches — a layout is a slot table AND a bank
/// table, so a crossbar's nested wings and a strip's side-by-side wings are each one self-contained layout. A bank
/// with no row here sits on the anchor.</param>
/// <param name="Slots">Where each physical control's plate sits, in button pitches from the bar's anchor (x right,
/// y UP) — the bar's SHAPE. A crossbar, a linear strip, a keyboard block: all are tables here, none is engine
/// policy. A control in <c>slotSet</c> with no row here takes the unplaced row. Absent places nothing, so every
/// slot lines up in the unplaced row.</param>
/// <param name="BadgeCorner">The corner the physical-button badge nudges toward, as a signed multiple of the glyph
/// offset: +1 up-right, -1 down-left, 0 centered on the plate.</param>
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBindingBarAnchor? Anchor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? GlyphOffsetRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? GlyphSizeRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? UnplacedRowLift = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? UnplacedSlotSpacing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldBindingBarSlotPlacement>? Slots = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, WorldBindingBarBankPlacement>? Banks = null,
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
    /// <summary>The resolved <see cref="Anchor"/> an unauthored bar takes: the bottom edge, 5% in.</summary>
    public static WorldBindingBarAnchor DefaultAnchor { get; } = new(
        Edge: WorldBindingBarEdge.Bottom,
        Margin: 0.05f
    );
    /// <summary>The resolved <see cref="BadgeCorner"/> an unauthored bar takes.</summary>
    public const float DefaultBadgeCorner = 1f;
    /// <summary>The resolved <see cref="ButtonSize"/> an unauthored bar takes (45/600).</summary>
    public const float DefaultButtonSize = (45f / 600f);
    /// <summary>The resolved <see cref="UnplacedRowLift"/> an unauthored bar takes — three pitches up, clear of a
    /// three-row cluster on the anchor.</summary>
    public const float DefaultUnplacedRowLift = 3f;
    /// <summary>The resolved <see cref="UnplacedSlotSpacing"/> an unauthored bar takes.</summary>
    public const float DefaultUnplacedSlotSpacing = 1.15f;
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

    /// <summary>Gets the resolved anchor.</summary>
    [JsonIgnore]
    public WorldBindingBarAnchor ResolvedAnchor => (Anchor ?? DefaultAnchor);
    /// <summary>Gets the resolved fixed badge-corner direction.</summary>
    [JsonIgnore]
    public float ResolvedBadgeCorner => (BadgeCorner ?? DefaultBadgeCorner);
    /// <summary>Gets the resolved slot-plate size.</summary>
    [JsonIgnore]
    public float ResolvedButtonSize => (ButtonSize ?? DefaultButtonSize);
    /// <summary>Gets the resolved cluster half-gap.</summary>
    /// <summary>Gets the resolved menu-row lift.</summary>
    /// <summary>Gets the resolved menu-row slot pitch.</summary>
    /// <summary>Gets the resolved exotics-row lift.</summary>
    [JsonIgnore]
    public float ResolvedUnplacedRowLift => (UnplacedRowLift ?? DefaultUnplacedRowLift);
    /// <summary>Gets the resolved exotics-row slot pitch.</summary>
    [JsonIgnore]
    public float ResolvedUnplacedSlotSpacing => (UnplacedSlotSpacing ?? DefaultUnplacedSlotSpacing);
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
/// any page). Where each bank sits is the live layout's <c>banks</c> table's to say; this row carries only what a
/// bank IS.</summary>
/// <param name="Id">The bank's stable id — its mutation address (unique within the authoring row).</param>
/// <param name="PageId">The <c>BindingPageDefinition.Id</c> this bank renders — validated to exist somewhere in the
/// composed binding profile.</param>
/// <param name="Order">This bank's draw order (unique within the authoring row): higher draws later, on top. Where
/// the bank sits is the active layout's <c>banks</c> table's, not this number's.</param>
/// <param name="Alpha">This bank's opacity when it is NOT the seat's currently active page.</param>
/// <param name="ActiveAlpha">This bank's opacity when it IS the seat's currently active page; <see langword="null"/>
/// draws fully opaque (1.0) while active.</param>
public sealed record WorldBindingBarBank(
    string Id,
    string PageId,
    int Order,
    float Alpha,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ActiveAlpha = null
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
/// <param name="Layout">The bar's tuning when no named layout is selected; <see langword="null"/> uses
/// <see cref="WorldBindingBarLayout.Default"/>.</param>
/// <param name="Layouts">Named alternative layouts — a crossbar, a strip, whatever an author adds next — each a
/// whole <see cref="WorldBindingBarLayout"/>. Which one is live is <paramref name="LayoutCell"/>'s to say.</param>
/// <param name="LayoutCell">A text state cell, <c>state.&lt;row&gt;.&lt;key&gt;</c>, whose value names the live entry of
/// <paramref name="Layouts"/>; a value naming none falls back to <paramref name="Layout"/>. Ordinary state: a chord,
/// a wheel sector, or the console switches the bar's whole shape by writing the cell.</param>
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, WorldBindingBarLayout>? Layouts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LayoutCell = null,
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
    /// <summary>The layout a selector value names: <see cref="Layouts"/>[<paramref name="name"/>] when it exists, else
    /// <see cref="ResolvedLayout"/>.</summary>
    /// <param name="name">The live <see cref="LayoutCell"/> value, or <see langword="null"/>.</param>
    public WorldBindingBarLayout LayoutNamed(string? name) =>
        (((name is { Length: > 0 }) && (Layouts is { } layouts) && layouts.TryGetValue(
            key: name,
            value: out var named
        ))
            ? named
            : ResolvedLayout);
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
