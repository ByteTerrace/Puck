namespace Puck.Commands;

/// <summary>
/// One fixed-step tick's complete, deterministic input — the canonical unit a game reads at tick time and a
/// peer transmits. It is a pure function of the captured input for the tick's window, built in a total
/// deterministic order, so the same captured input yields a bit-identical snapshot on every machine. It is
/// ephemeral: built, applied, and dropped within the tick that produced it — its borrowed buffers remain valid only
/// until the producing router's next snapshot, and nothing persists it. A world
/// tape records the server's input stream instead and re-runs guests against it on replay; a snapshot is
/// never itself the recorded unit. Supersedes both the per-render-frame command collection and a game's
/// hand-rolled per-tick intent.
/// </summary>
/// <remarks>
/// Construction is INTERNAL, like <see cref="CommandEntry"/>'s and <see cref="CommandLane"/>'s: a snapshot is the
/// argument <see cref="CommandRegistry.ApplySnapshot"/> dispatches, so the <see cref="InputRouter"/>'s mixer being its
/// only builder is what makes "every applied entry came through an ingress door" a property of the type rather than a
/// convention. <see cref="Empty"/> is the one snapshot a host can obtain directly, and it dispatches nothing.
/// </remarks>
public readonly record struct CommandSnapshot {
    /// <summary>Initializes a new instance of the <see cref="CommandSnapshot"/> struct.</summary>
    /// <param name="tick">The fixed-step tick this snapshot is the input for.</param>
    /// <param name="lanes">The per-slot command lanes, ordered by <see cref="CommandLane.Slot"/>.</param>
    /// <param name="registry">The registry whose interned command-id namespace the lanes use.</param>
    internal CommandSnapshot(ulong tick, CommandBuffer<CommandLane> lanes, CommandRegistry? registry = null) {
        Lanes = lanes;
        Registry = registry;
        Tick = tick;
    }

    /// <summary>The per-slot command lanes, ordered by <see cref="CommandLane.Slot"/> for a deterministic layout.</summary>
    /// <remarks>The view remains valid until its producing router builds the next snapshot.</remarks>
    public CommandBuffer<CommandLane> Lanes { get; internal init; }

    // The registry that minted the command-id namespace carried by Lanes. Internal so provenance is neither
    // forgeable nor part of the public snapshot payload; ApplySnapshot uses reference identity to keep an id from
    // one registry from being reinterpreted as a different command in another.
    internal CommandRegistry? Registry { get; init; }

    /// <summary>The fixed-step tick this snapshot is the input for.</summary>
    public ulong Tick { get; internal init; }

    /// <summary>An empty snapshot for a tick (no active input on any slot).</summary>
    /// <param name="tick">The fixed-step tick the snapshot stands for.</param>
    /// <returns>A snapshot with no lanes.</returns>
    public static CommandSnapshot Empty(ulong tick) {
        return new CommandSnapshot(
            lanes: default,
            tick: tick
        );
    }

    /// <summary>Compares deterministic snapshot content. Registry provenance is an application guard, not payload.</summary>
    public bool Equals(CommandSnapshot other) {
        return (
            (Tick == other.Tick) &&
            Lanes.Equals(other: other.Lanes)
        );
    }

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        Tick,
        Lanes
    );

    /// <summary>Finds the lane for a logical slot, if it has any active input this tick.</summary>
    /// <param name="slot">The logical player slot to look up.</param>
    /// <param name="lane">The matching lane when found.</param>
    /// <returns><see langword="true"/> if a lane for <paramref name="slot"/> is present.</returns>
    public bool TryGetLane(int slot, out CommandLane lane) {
        if (!Lanes.IsEmpty) {
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
