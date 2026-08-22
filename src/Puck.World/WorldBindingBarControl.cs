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
/// <param name="Stacked">Whether every authored bank renders (<see langword="true"/>) or only the seat's active
/// bank (<see langword="false"/>) — the seat's stored <see cref="BindingBarPreferences.Stacked"/>, defaulting to
/// <see langword="true"/> (stacked, the authored look) when unset.</param>
/// <param name="EffectiveScale"><see cref="Authoring"/>'s resolved layout scale, overridden by the seat's own
/// stored <see cref="BindingBarPreferences.Scale"/> when set to a finite positive value.</param>
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
    float Presence
);
/// <summary>Resolves the world binding-bar floor with each seat identity's preference, its authored
/// <see cref="WorldBindingBarAuthoring.Visible"/> condition, the seat's own stored LOOK preferences
/// (<see cref="BindingProfileDocument.BindingBar"/>), and the live override.</summary>
/// <remarks>Read-only over the live override: the only writer is the <c>binding-bar</c> session lever's registered
/// setter, so a forced bar has crossed the server's <c>Mutate</c> check over <c>section:bindings</c>.</remarks>
internal sealed class WorldBindingBarControl {
    private readonly WorldSeatBindings m_bindings;
    private readonly WorldClient m_client;
    private readonly WorldOverlayFacts m_facts;
    private readonly PlayerRoster m_roster;
    private readonly WorldBindingBarVisibility m_visibility;

    /// <summary>Initializes a binding-bar policy resolver.</summary>
    public WorldBindingBarControl(WorldClient client, PlayerRoster roster, WorldOverlayFacts facts, WorldSeatBindings bindings, WorldBindingBarVisibility visibility) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(visibility);
        m_client = client;
        m_roster = roster;
        m_facts = facts;
        m_bindings = bindings;
        m_visibility = visibility;
    }

    private (WorldBindingBarAuthoring Authoring, string Source) ResolveAuthoring(int slot) {
        if (m_roster.ProfileAt(slot: slot)?.Document?.BindingOverlays.FirstOrDefault()?.BindingBar is { } profile) {
            return (profile, "identity");
        }

        if (m_client.Definition.BindingOverlays.FirstOrDefault()?.BindingBar is { } world) {
            return (world, "world");
        }

        return (WorldBindingBarAuthoring.Absent, "default");
    }

    /// <summary>Gets one seat's resolved policy and current visibility.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    public WorldBindingBarStatus Status(int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            slot,
            PlayerRoster.MaxSlots
        );

        var (authoring, source) = ResolveAuthoring(slot: slot);
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
        var effectiveScale = authoring.ResolvedLayout.Scale;
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
            Stacked: (preferences?.Stacked ?? true),
            Presence: presence
        );
    }
}
