namespace Puck.World.Client;

/// <summary>
/// The live per-seat binding-bar visibility override — the presentation service the <c>binding-bar</c> session lever
/// writes and the root's bar-policy resolver reads, the same live-state-beside-the-document shape
/// <see cref="WorldRenderSettings"/> carries for the render levers.
/// </summary>
/// <remarks>Nothing writes this outside <c>WorldSessionLeverSink</c>'s registered setter, so a forced bar always
/// crossed the server's <c>Mutate</c> check over <c>section:bindings</c> first.</remarks>
public sealed class WorldBindingBarVisibility {
    private readonly bool?[] m_overrides = new bool?[PlayerRoster.MaxSlots];

    /// <summary>Gets a value indicating whether any seat currently forces its bar.</summary>
    public bool Engaged {
        get {
            foreach (var slot in m_overrides) {
                if (slot is not null) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Gets one seat's forced visibility, or <see langword="null"/> for authored behavior.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    /// <returns>The override.</returns>
    public bool? Override(int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            slot,
            PlayerRoster.MaxSlots
        );

        return m_overrides[slot];
    }
    /// <summary>Sets or clears one seat's forced visibility.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    /// <param name="visible">The forced visibility, or <see langword="null"/> to return to authored behavior.</param>
    public void SetOverride(int slot, bool? visible) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            slot,
            PlayerRoster.MaxSlots
        );
        m_overrides[slot] = visible;
    }
}
