using Puck.Commands;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>One seat's resolved binding-bar policy and current visibility.</summary>
/// <param name="Authoring">The resolved authored policy.</param>
/// <param name="Source">Where the resolved policy came from.</param>
/// <param name="Override">The live visibility override, or <see langword="null"/> for authored behavior.</param>
/// <param name="Hidden">Whether the bar is currently hidden.</param>
/// <param name="Reason">Why the current visibility resolved.</param>
/// <param name="IdleTicks">The seat's current rest duration in simulation ticks.</param>
/// <param name="HideAfterRestTicks">The compiled rest timeout.</param>
internal readonly record struct WorldBindingBarStatus(
    WorldBindingBarAuthoring Authoring,
    string Source,
    bool? Override,
    bool Hidden,
    string Reason,
    ulong IdleTicks,
    CompiledTickDuration HideAfterRestTicks
);

/// <summary>Resolves the world binding-bar floor with each seat identity's preference and live override.</summary>
internal sealed class WorldBindingBarControl {
    private readonly WorldClient m_client;
    private readonly WorldBindingBarAuthoring?[] m_lastAuthoring = new WorldBindingBarAuthoring?[PlayerRoster.MaxSlots];
    private readonly Func<InputRouter> m_router;
    private readonly PlayerRoster m_roster;
    private readonly ulong[] m_restOriginTicks = new ulong[PlayerRoster.MaxSlots];
    private readonly WorldServer m_server;
    private readonly bool?[] m_visibilityOverrides = new bool?[PlayerRoster.MaxSlots];
    private readonly bool[] m_wasResolved = new bool[PlayerRoster.MaxSlots];

    /// <summary>Initializes a binding-bar policy resolver.</summary>
    public WorldBindingBarControl(WorldClient client, PlayerRoster roster, Func<InputRouter> router, WorldServer server) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(server);

        m_client = client;
        m_roster = roster;
        m_router = router;
        m_server = server;
    }

    /// <summary>Gets one seat's resolved policy and current visibility.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    public WorldBindingBarStatus Status(int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, PlayerRoster.MaxSlots);

        var nextInputTick = m_server.NextInputTick;
        var completedTick = ((nextInputTick == 0UL) ? 0UL : (nextInputTick - 1UL));
        var (authoring, source) = ResolveAuthoring(slot: slot);

        if (!m_wasResolved[slot] || !Equals(objA: m_lastAuthoring[slot], objB: authoring)) {
            m_wasResolved[slot] = true;
            m_lastAuthoring[slot] = authoring;
            m_restOriginTicks[slot] = completedTick;
        }

        if (m_router().TryGetLastInputTick(slot: slot, tick: out var inputTick) && (inputTick > m_restOriginTicks[slot])) {
            m_restOriginTicks[slot] = inputTick;
        }

        var idleTicks = (completedTick - Math.Min(val1: completedTick, val2: m_restOriginTicks[slot]));
        var timeout = WorldSimulationTickConversion.CompiledDuration(
            seconds: authoring.HideAfterRestSeconds,
            ratePerSecond: (uint)m_client.Definition.SimulationRateHz
        );
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
        } else if (timeout.IsNever || timeout.IsZero || (idleTicks < (ulong)timeout.Ticks)) {
            hidden = false;
            reason = "shown";
        } else {
            hidden = true;
            reason = "rested-out";
        }

        return new WorldBindingBarStatus(
            Authoring: authoring,
            Source: source,
            Override: liveOverride,
            Hidden: hidden,
            Reason: reason,
            IdleTicks: idleTicks,
            HideAfterRestTicks: timeout
        );
    }

    /// <summary>Sets or clears one seat's live visibility override.</summary>
    /// <param name="slot">The 0-based local seat.</param>
    /// <param name="visible">The forced visibility, or <see langword="null"/> to return to authored behavior.</param>
    public void SetOverride(int slot, bool? visible) {
        _ = Status(slot: slot);
        m_visibilityOverrides[slot] = visible;
        var nextInputTick = m_server.NextInputTick;
        m_restOriginTicks[slot] = ((nextInputTick == 0UL) ? 0UL : (nextInputTick - 1UL));
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
}
