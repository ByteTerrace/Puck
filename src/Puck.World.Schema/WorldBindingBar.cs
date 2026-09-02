using System.Numerics;
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
/// <summary>Where a bar hangs: a viewport edge and how far in from it. Every bank anchored to the same edge and
/// inset shares one frame — their plates are laid out together on one pitch grid and the nearest plate of the whole
/// group sits at the inset — so a nested crossbar is five banks on one anchor, and a strip with side columns is
/// three groups on three.</summary>
/// <param name="Edge">The viewport edge.</param>
/// <param name="Inset">The gap between that edge and the nearest plate edge of everything anchored here, in button
/// pitches — the same ruler every plate position uses. Along the other axis the group is centered.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldBindingBarAnchor(BindingBarEdge Edge = BindingBarEdge.Bottom, float Inset = 0f);
/// <summary>One physical control's plate on a bank, in button pitches from the bank's anchor — x right, y UP.</summary>
/// <param name="Source">The physical control's input source id (a <c>slotSet</c> member).</param>
/// <param name="X">Pitches right of the anchor (negative = left).</param>
/// <param name="Y">Pitches above the anchor (negative = below).</param>
/// <param name="Badge">Where the plate's physical-button badge sits, <c>[x, y]</c> as signed multiples of the
/// layout's glyph offset: +1 right / up, −1 left / down, 0 centered. A plate in a cluster points its badge outward —
/// a d-pad's up button badges up, its left button left — so the badge never lands on a neighbour.
/// <see langword="null"/> is the up-right corner, <c>[1, 1]</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldBindingBarSlotPlacement(
    string Source,
    float X,
    float Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<float>? Badge = null
) {
    /// <summary>The badge multiple an unauthored <see cref="Badge"/> takes: the up-right corner.</summary>
    public static Vector2 DefaultBadge { get; } = new(
        x: 1f,
        y: 1f
    );
    /// <summary>Gets this placement as the overlay reads it: the pitch position and the resolved badge multiples.</summary>
    [JsonIgnore]
    public BindingPlatePlacement Plate => new(
        Position: new Vector2(
            x: X,
            y: Y
        ),
        Badge: ((Badge is { Count: 2 } badge)
            ? new Vector2(
                x: badge[0],
                y: badge[1]
            )
            : DefaultBadge)
    );
}
/// <summary>One placed table: a named table of the layout, moved by <see cref="At"/>.</summary>
/// <param name="Table">The key of a table in the layout's <c>tables</c>.</param>
/// <param name="At">The displacement, <c>[x, y]</c> in button pitches (x right, y up); <see langword="null"/> is
/// none.</param>
/// <param name="Badge">A badge direction for every plate of this piece, overriding the table rows'; see
/// <see cref="WorldBindingBarSlotPlacement.Badge"/>. <see langword="null"/> keeps each row's own.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldBindingBarPiece(
    string Table,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<float>? At = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<float>? Badge = null
);
/// <summary>One bank's place on a layout — a bank is a bar: where it hangs and the pieces it is made of. A control
/// in the slot set that no piece places is not shown on this bank; a source placed by two pieces takes the later
/// one.</summary>
/// <param name="Pieces">The placed tables, in order.</param>
/// <param name="Anchor">Where this bank hangs, or <see langword="null"/> to share the layout's anchor (and so its
/// frame: the pieces then nest against the other banks there).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldBindingBarBankPlacement(
    IReadOnlyList<WorldBindingBarPiece> Pieces,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBindingBarAnchor? Anchor = null
);
/// <summary>The authored layout of one on-screen binding bar: its tables, its banks, and its size tuning. Every
/// tuning field is an optional override of the engine's resolved default (the <c>Default*</c> constants below):
/// lengths are fractions of the seat viewport's height, every <c>*Ratio</c> is a multiple of the button size, and
/// every <c>*MinPx</c> is a device-pixel floor.</summary>
/// <param name="Tables">The plate tables by name — a cross, a strip, a column — each authored once and placed by
/// the banks' pieces as many times as the layout needs.</param>
/// <param name="Banks">Where each bank sits and what it shows, by bank id. A bank with no row here is not drawn in
/// this layout.</param>
/// <param name="Anchor">Where the bar's own anchor (the modifier row, page label, chord hints) hangs, and the anchor
/// every bank without its own shares.</param>
/// <param name="ButtonSize">The slot-plate size at most: the writer shrinks it so every anchor group fits its seat
/// region, and a seat's stored bar scale multiplies it.</param>
/// <param name="GlyphOffsetRatio">The badge's offset as a fraction of <paramref name="ButtonSize"/> — each plate's
/// badge takes its own signed multiples of it (<see cref="WorldBindingBarSlotPlacement.Badge"/>).</param>
/// <param name="GlyphSizeRatio">The badge's size as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="ModifierHalfRatio">The modifier indicator's plate half-extent, in button sizes.</param>
/// <param name="ModifierSpacingRatio">The modifier indicators' pitch, in button sizes.</param>
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, IReadOnlyList<WorldBindingBarSlotPlacement>>? Tables = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, WorldBindingBarBankPlacement>? Banks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBindingBarAnchor? Anchor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ButtonSize = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? GlyphOffsetRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? GlyphSizeRatio = null,
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
    /// <summary>The resolved <see cref="Anchor"/> an unauthored bar takes: the bottom edge, half a plate in.</summary>
    public static WorldBindingBarAnchor DefaultAnchor { get; } = new(
        Edge: BindingBarEdge.Bottom,
        Inset: 0.5f
    );

    /// <summary>Compiles this layout: each bank's pieces become one plate table (a later piece wins a source it
    /// re-places, a piece-level badge overrides its rows, a piece naming no table contributes nothing), and the
    /// banks are grouped into frames by anchor with their pitches normalized — see
    /// <see cref="CompiledBindingBarLayout.Build"/>. Pure; call once per document.</summary>
    public CompiledBindingBarLayout Compile() {
        var banks = new List<(string, BindingBarEdge, float, IReadOnlyDictionary<string, BindingPlatePlacement>)>();

        foreach (var (id, bank) in (Banks ?? new Dictionary<string, WorldBindingBarBankPlacement>())) {
            if (bank is null) {
                continue;
            }

            var plates = new Dictionary<string, BindingPlatePlacement>(comparer: StringComparer.Ordinal);

            foreach (var piece in (bank.Pieces ?? [])) {
                if ((piece?.Table is not { Length: > 0 } tableName) || (Tables is null) || !Tables.TryGetValue(
                    key: tableName,
                    value: out var rows
                ) || (rows is null)) {
                    continue;
                }

                var at = ((piece.At is { Count: 2 } authoredAt)
                    ? new Vector2(
                        x: authoredAt[0],
                        y: authoredAt[1]
                    )
                    : Vector2.Zero
                );
                var badge = ((piece.Badge is { Count: 2 } authoredBadge)
                    ? new Vector2(
                        x: authoredBadge[0],
                        y: authoredBadge[1]
                    )
                    : (Vector2?)null
                );

                foreach (var row in rows) {
                    if (row?.Source is { Length: > 0 } source) {
                        var plate = row.Plate;

                        plates[source] = plate with {
                            Position = (plate.Position + at),
                            Badge = (badge ?? plate.Badge),
                        };
                    }
                }
            }

            var anchor = (bank.Anchor ?? ResolvedAnchor);

            banks.Add(item: (id, anchor.Edge, anchor.Inset, plates));
        }

        return CompiledBindingBarLayout.Build(banks: banks);
    }

    /// <summary>The resolved <see cref="ButtonSize"/> an unauthored bar takes (45/600).</summary>
    public const float DefaultButtonSize = (45f / 600f);
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

    /// <summary>Gets the layout a bar whose selector names no authored layout draws: every tuning default and no
    /// banks — nothing placed, so nothing drawn, which is the honest reading of "no layout".</summary>
    public static WorldBindingBarLayout Default { get; } = new();

    /// <summary>Gets the resolved anchor.</summary>
    [JsonIgnore]
    public WorldBindingBarAnchor ResolvedAnchor => (Anchor ?? DefaultAnchor);
    /// <summary>Gets the resolved slot-plate size.</summary>
    [JsonIgnore]
    public float ResolvedButtonSize => (ButtonSize ?? DefaultButtonSize);
    /// <summary>Gets the resolved badge offset ratio.</summary>
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
/// <summary>One stacked binding-bar bank: what it is — the page it renders and its opacities. Several banks of the
/// same slot set render simultaneously, each showing what that bank's OWN page binds. Where a bank sits, and which
/// plates it shows, is each layout's <c>banks</c> table's to say; draw order is this list's order (later draws on
/// top).</summary>
/// <param name="Id">The bank's stable id — its mutation address and its key in every layout's bank table.</param>
/// <param name="PageId">The <c>BindingPageDefinition.Id</c> this bank renders — validated to exist somewhere in the
/// composed binding profile.</param>
/// <param name="Alpha">This bank's opacity when it is NOT the seat's currently active page.</param>
/// <param name="ActiveAlpha">This bank's opacity when it IS the seat's currently active page; <see langword="null"/>
/// draws fully opaque (1.0) while active.</param>
public sealed record WorldBindingBarBank(
    string Id,
    string PageId,
    float Alpha,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ActiveAlpha = null
);
/// <summary>The authored visibility, layouts, and slot vocabulary of the on-screen binding bar. Absence of a whole
/// <see cref="WorldBindingBarAuthoring"/> row (no identity, no world authoring) draws no bar at all — see
/// <see cref="Absent"/>.</summary>
/// <param name="Enabled">Whether the bar is shown when no live override hides it.</param>
/// <param name="Text">Whether the bar draws the atlas text it composes — a badge whose icon row carries a
/// <c>Label</c> (LB/RB, LT/RT, LS/RS, the menu trio, the exotics), the active page's name under the modifier
/// indicators, and the chord-hint lines above them. <see langword="false"/> drops every label-content badge outright
/// and leaves a purely pictographic bar: every plate, the glyph-content badges (the d-pad arrows and the face-position
/// marks), the bound actions' icons, and the modifier indicators all still draw.</param>
/// <param name="Modifiers">Whether the bar draws the modifier indicator row on its anchor line — one plate per
/// modifier the composed profile carries (declared rows plus every chord/held token the compiler synthesizes), lit
/// while held. <see langword="false"/> drops the row and leaves the slot clusters alone; the chord hints and page
/// label are <paramref name="Text"/>'s, not this one's.</param>
/// <param name="SlotSet">The physical controls this bar shows, by input source id (<c>gamepad.buttonSouth</c>,
/// <c>mouse.button1</c>, …) — the same vocabulary a binding entry's <c>sources</c> speak, every id validated against
/// the engine's input-source catalog, unique, at most <see cref="WorldBindingBarCapacity.MaxSlots"/>. A layout's bank
/// places these; one it does not place is not shown on that bank.</param>
/// <param name="Banks">The stacked banks — at least one, at most <see cref="WorldBindingBarCapacity.MaxBanks"/>,
/// unique ids, each naming a page the composed binding profile actually declares. List order is draw order.</param>
/// <param name="HideUnbound">Whether a slot with no bound act on its bank's page should not render at all, rather
/// than drawing the disabled tier-0 plate. A player's own <c>BindingBarPreferences.HideUnbound</c> overrides this.</param>
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
/// <param name="Layouts">The bar's layouts by name — a crossbar, a strip, whatever an author adds next — each a whole
/// <see cref="WorldBindingBarLayout"/>.</param>
/// <param name="Layout">The name of the layout drawn when <paramref name="LayoutCell"/> is absent or names none.</param>
/// <param name="LayoutCell">A text state cell, <c>state.&lt;row&gt;.&lt;key&gt;</c>, whose value names the live entry of
/// <paramref name="Layouts"/>; a value naming none falls back to <paramref name="Layout"/>. Ordinary state: a chord,
/// a wheel sector, or the console switches the bar's whole shape by writing the cell.</param>
/// <param name="ModelCell">A text state cell, <c>state.&lt;row&gt;.&lt;key&gt;</c>, whose value picks the bar's model:
/// <c>single</c> draws one bar — the active page's bank, in the first authored bank's place, swapping in place as a
/// chord is held or released; any other value (or no cell) draws every bank the live layout places, the active one
/// at full alpha and the rest as wings. Ordinary state, flipped the same way <paramref name="LayoutCell"/> is.</param>
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Layout = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LayoutCell = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ModelCell = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverlayPredicate? Visible = null
) {
    /// <summary>The <see cref="ModelCell"/> value that draws one swapping bar; every other value stacks.</summary>
    public const string SingleModel = "single";

    /// <summary>Gets the policy applied when NEITHER an identity nor the world authors a binding-bar row — absence
    /// draws no bar at all, never a baked-in look.</summary>
    public static WorldBindingBarAuthoring Absent { get; } = new(
        Banks: [],
        Enabled: false,
        SlotSet: [],
        Text: false
    );

    /// <summary>The layout a selector value names: <see cref="Layouts"/>[<paramref name="name"/>] when it exists,
    /// else <see cref="Layouts"/>[<see cref="Layout"/>], else <see cref="WorldBindingBarLayout.Default"/> (no banks —
    /// nothing drawn).</summary>
    /// <param name="name">The live <see cref="LayoutCell"/> value, or <see langword="null"/>.</param>
    public WorldBindingBarLayout LayoutNamed(string? name) {
        if (Layouts is not { } layouts) {
            return WorldBindingBarLayout.Default;
        }

        if ((name is { Length: > 0 }) && layouts.TryGetValue(
            key: name,
            value: out var named
        ) && (named is not null)) {
            return named;
        }

        return (((Layout is { Length: > 0 }) && layouts.TryGetValue(
            key: Layout,
            value: out var fallback
        ) && (fallback is not null))
            ? fallback
            : WorldBindingBarLayout.Default);
    }
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
