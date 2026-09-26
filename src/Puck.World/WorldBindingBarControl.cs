using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>One seat's resolved binding-bar policy and current visibility.</summary>
/// <param name="Authoring">The resolved authored policy.</param>
/// <param name="Source">Where the resolved policy came from.</param>
/// <param name="Override">The live visibility override, or <see langword="null"/> for authored behavior.</param>
/// <param name="Hidden">Whether the bar is currently hidden.</param>
/// <param name="Reason">Why the current visibility resolved.</param>
/// <param name="EffectiveHideUnbound"><see cref="Authoring"/>'s <c>HideUnbound</c>, overridden by the seat's own
/// stored <see cref="BindingBarPreferences.HideUnbound"/> when set.</param>
/// <param name="Stacked">Whether every placed bank renders (<see langword="true"/>) or one swapping bar
/// (<see langword="false"/>) — <see cref="WorldBindingBarAuthoring.ModelCell"/>'s cell reads
/// <see cref="WorldBindingBarAuthoring.SingleModel"/>. Read per status like the layout.</param>
/// <param name="EffectiveScale">The seat's own stored <see cref="BindingBarPreferences.Scale"/> when set to a finite
/// positive value, else 1 — the runtime multiplier on the layout's button size.</param>
/// <param name="Layout">The live layout: the named entry <see cref="WorldBindingBarAuthoring.LayoutCell"/>'s cell
/// selects this frame, else the authoring row's own <c>layout</c>. Read per status, so a state write re-shapes the
/// bar on the next frame.</param>
/// <param name="Compiled">The live layout compiled — frames and normalized plates — built once per layout
/// instance.</param>
/// <param name="Presence">The authored <see cref="WorldBindingBarAuthoring.Visible"/> condition's presence, 0..1 —
/// the bar's opacity multiplier, so a fading <c>recently</c> predicate fades the bar rather than cutting it. 1
/// under a live override or with no condition.</param>
internal readonly record struct WorldBindingBarStatus(
    WorldBindingBarAuthoring Authoring,
    string Source,
    bool? Override,
    bool Hidden,
    string Reason,
    bool EffectiveHideUnbound,
    bool Stacked,
    float EffectiveScale,
    float Presence,
    WorldBindingBarLayout Layout,
    CompiledBindingBarLayout Compiled
);
/// <summary>Resolves the world binding-bar floor with each seat identity's preference, its authored
/// <see cref="WorldBindingBarAuthoring.Visible"/> condition, the seat's own stored LOOK preferences
/// (<see cref="BindingProfileDocument.BindingBar"/>), and the live override.</summary>
/// <remarks>Read-only over the live override: the only writer is the <c>binding-bar</c> session lever's registered
/// setter, so a forced bar has crossed the server's <c>Mutate</c> check over <c>section:bindings</c>.</remarks>
internal sealed class WorldBindingBarControl {
    // The compiled form of each seat's live layout, rebuilt only when the layout instance changes (a document
    // swap or a layout-cell flip) — the one derivation a status call may hand out without allocating.
    private readonly WorldBindingBarLayout?[] m_compiledSource = new WorldBindingBarLayout?[PlayerRoster.MaxSlots];
    private readonly CompiledBindingBarLayout[] m_compiled = new CompiledBindingBarLayout[PlayerRoster.MaxSlots];

    private readonly WorldSeatBindings m_bindings;
    private readonly WorldOverlayFacts m_facts;
    private readonly PlayerRoster m_roster;
    private readonly WorldBindingBarVisibility m_visibility;

    /// <summary>Initializes a binding-bar policy resolver.</summary>
    public WorldBindingBarControl(PlayerRoster roster, WorldOverlayFacts facts, WorldSeatBindings bindings, WorldBindingBarVisibility visibility) {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(visibility);
        m_roster = roster;
        m_facts = facts;
        m_bindings = bindings;
        m_visibility = visibility;
    }

    private CompiledBindingBarLayout CompiledFor(int slot, WorldBindingBarLayout layout) {
        if (!ReferenceEquals(
            objA: m_compiledSource[slot],
            objB: layout
        )) {
            m_compiled[slot] = layout.Compile();
            m_compiledSource[slot] = layout;
        }

        return m_compiled[slot];
    }
    // A layout or model cell's text through the seat's routed mirror, or null when the token is absent, is no binding,
    // no registration records it, or its cell holds no text.
    private static string? CellText(WorldStateMirror? mirror, string? token) {
        var slot = (mirror?.SlotOf(
            conversion: WorldStateConversion.Number,
            token: token
        ) ?? -1);

        return (((slot >= 0) && (mirror!.Sample(slot: slot).Value is { Kind: CellKind.Text } text))
            ? text.AsText
            : null
        );
    }

    /// <summary>Gets one seat's resolved policy and current visibility.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    public WorldBindingBarStatus Status(int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            slot,
            PlayerRoster.MaxSlots
        );

        // The bar follows the seat's route, like its pages and wheels: its policy is the identity's, else the routed
        // world's, and its cells read the routed authority's mirror.
        m_bindings.GetRoutedState(
            definition: out var routed,
            slot: slot,
            state: out var mirror
        );

        var authoring = WorldBindingBarAuthoring.Resolve(
            identity: m_roster.ProfileAt(slot: slot)?.Document,
            source: out var source,
            world: routed
        );
        var liveOverride = m_visibility.Override(slot: slot);
        bool hidden;
        string reason;

        if (liveOverride is false) {
            hidden = true;
            reason = "forced-off";
        } else if (liveOverride is true) {
            hidden = false;
            reason = "shown";
        } else if (!authoring.Enabled) {
            hidden = true;
            reason = "authored-off";
        } else if (m_facts.Evaluate(
            predicate: authoring.Visible,
            slot: slot
        )) {
            hidden = false;
            reason = "shown";
        } else {
            hidden = true;
            reason = "visible-false";
        }

        var preferences = m_bindings.ProfileBindings(slot: slot)?.BindingBar;
        // Which layout is live is a state cell's answer, read through the state mirror: the bar's shape is data the
        // player can flip.
        var layout = authoring.LayoutNamed(name: CellText(
            mirror: mirror,
            token: authoring.LayoutCell
        ));
        var stacked = !string.Equals(
            a: CellText(
                mirror: mirror,
                token: authoring.ModelCell
            ),
            b: WorldBindingBarAuthoring.SingleModel,
            comparisonType: StringComparison.Ordinal
        );
        var effectiveScale = 1f;
        var presence = ((hidden || (liveOverride is true))
            ? (hidden
                ? 0f
                : 1f)
            : m_facts.Presence(
                predicate: authoring.Visible,
                slot: slot
            )
        );

        if (
            (preferences?.Scale is { } scale) &&
            float.IsFinite(f: scale) &&
            (scale > 0f)
        ) {
            effectiveScale = scale;
        }

        return new WorldBindingBarStatus(
            Authoring: authoring,
            EffectiveHideUnbound: (preferences?.HideUnbound ?? authoring.HideUnbound),
            EffectiveScale: effectiveScale,
            Hidden: hidden,
            Override: liveOverride,
            Reason: reason,
            Source: source,
            Stacked: stacked,
            Presence: presence,
            Layout: layout,
            Compiled: CompiledFor(
                layout: layout,
                slot: slot
            )
        );
    }
}
