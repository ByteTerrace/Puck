using System.Collections.Immutable;

namespace Puck.Commands;

/// <summary>
/// One fixed-step tick's complete, deterministic input — the canonical unit a game reads at tick time, a
/// recorder writes, and a peer transmits. It is a pure function of the captured input for the tick's window,
/// built in a total deterministic order, so the same captured input yields a bit-identical snapshot on every
/// machine. Supersedes both the per-render-frame command collection and a game's hand-rolled per-tick intent.
/// </summary>
/// <param name="Tick">The fixed-step tick this snapshot is the input for.</param>
/// <param name="Lanes">The per-slot command lanes, ordered by <see cref="CommandLane.Slot"/> for a deterministic layout.</param>
public readonly record struct CommandSnapshot(ulong Tick, ImmutableArray<CommandLane> Lanes) {
    /// <summary>An empty snapshot for a tick (no active input on any slot).</summary>
    public static CommandSnapshot Empty(ulong tick) {
        return new CommandSnapshot(Lanes: [], Tick: tick);
    }

    /// <summary>Finds the lane for a logical slot, if it has any active input this tick.</summary>
    /// <param name="slot">The logical player slot to look up.</param>
    /// <param name="lane">The matching lane when found.</param>
    /// <returns><see langword="true"/> if a lane for <paramref name="slot"/> is present.</returns>
    public bool TryGetLane(int slot, out CommandLane lane) {
        if (!Lanes.IsDefaultOrEmpty) {
            for (var index = 0; (index < Lanes.Length); index++) {
                if (Lanes[index].Slot == slot) {
                    lane = Lanes[index];

                    return true;
                }
            }
        }

        lane = default;

        return false;
    }
}
