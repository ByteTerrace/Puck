using Puck.World.Client;

namespace Puck.World;

/// <summary>One seat's resolved binding-bar policy and current visibility.</summary>
/// <param name="Authoring">The resolved authored policy.</param>
/// <param name="Source">Where the resolved policy came from.</param>
/// <param name="Override">The live visibility override, or <see langword="null"/> for authored behavior.</param>
/// <param name="Hidden">Whether the bar is currently hidden.</param>
/// <param name="Reason">Why the current visibility resolved.</param>
internal readonly record struct WorldBindingBarStatus(
    WorldBindingBarAuthoring Authoring,
    string Source,
    bool? Override,
    bool Hidden,
    string Reason
);
/// <summary>Resolves the world binding-bar floor with each seat identity's preference, its authored
/// <see cref="WorldBindingBarAuthoring.Visible"/> condition, and the live override.</summary>
internal sealed class WorldBindingBarControl {
    private readonly WorldClient m_client;
    private readonly WorldOverlayFacts m_facts;
    private readonly PlayerRoster m_roster;
    private readonly bool?[] m_visibilityOverrides = new bool?[PlayerRoster.MaxSlots];

    /// <summary>Initializes a binding-bar policy resolver.</summary>
    public WorldBindingBarControl(WorldClient client, PlayerRoster roster, WorldOverlayFacts facts) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(facts);
        m_client = client;
        m_roster = roster;
        m_facts = facts;
    }

    private (WorldBindingBarAuthoring Authoring, string Source) ResolveAuthoring(int slot) {
        if (m_roster.ProfileAt(slot: slot)?.Document?.BindingOverlays.FirstOrDefault()?.BindingBar is { } profile) {
            return (profile, "identity");
        }

        if (m_client.Definition.BindingOverlays.FirstOrDefault()?.BindingBar is { } world) {
            return (world, "world");
        }

        return (WorldBindingBarAuthoring.Default, "default");
    }

    /// <summary>Sets or clears one seat's live visibility override.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    /// <param name="visible">The forced visibility, or <see langword="null"/> to return to authored behavior.</param>
    public void SetOverride(int slot, bool? visible) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            slot,
            PlayerRoster.MaxSlots
        );
        m_visibilityOverrides[slot] = visible;
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
        var liveOverride = m_visibilityOverrides[slot];
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

        return new WorldBindingBarStatus(
            Authoring: authoring,
            Hidden: hidden,
            Override: liveOverride,
            Reason: reason,
            Source: source
        );
    }
}
