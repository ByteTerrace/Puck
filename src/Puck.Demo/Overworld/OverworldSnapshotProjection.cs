using System.Numerics;
using Puck.Commands;

namespace Puck.Demo.Overworld;

/// <summary>
/// Projects a tick's <see cref="CommandSnapshot"/> into the per-slot <see cref="PlayerIntent"/> row the simulation
/// consumes — the single place that maps the engine's command lanes onto the overworld's move/jump/interact intent. The
/// live router path and the replay/determinism path both project through here, so a recorded snapshot reproduces the
/// exact intent the live capture produced.
/// </summary>
internal static class OverworldSnapshotProjection {
    /// <summary>Projects every slot's lane in the snapshot into a fixed-width intent row.</summary>
    /// <param name="snapshot">The tick's command snapshot.</param>
    /// <param name="moveId">The interned id of the move command (ignored when <paramref name="hasMove"/> is false).</param>
    /// <param name="hasMove">Whether the move command is interned in the registry.</param>
    /// <param name="jumpId">The interned id of the jump command (ignored when <paramref name="hasJump"/> is false).</param>
    /// <param name="hasJump">Whether the jump command is interned in the registry.</param>
    /// <param name="interactId">The interned id of the interact command (ignored when <paramref name="hasInteract"/> is false).</param>
    /// <param name="hasInteract">Whether the interact command is interned in the registry.</param>
    /// <returns>One intent per slot, in slot order (length <see cref="OverworldWorld.MaxPlayers"/>).</returns>
    public static PlayerIntent[] ToIntents(in CommandSnapshot snapshot, ushort moveId, bool hasMove, ushort jumpId, bool hasJump, ushort interactId, bool hasInteract, ushort cycleId = 0, bool hasCycle = false) {
        var row = new PlayerIntent[OverworldWorld.MaxPlayers];

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            if (snapshot.TryGetLane(slot: slot, out var lane)) {
                row[slot] = FromLane(lane: lane, moveId: moveId, hasMove: hasMove, jumpId: jumpId, hasJump: hasJump, interactId: interactId, hasInteract: hasInteract, cycleId: cycleId, hasCycle: hasCycle);
            }
        }

        return row;
    }

    /// <summary>Projects a single slot's lane into its intent for the tick.</summary>
    /// <param name="lane">The slot's command lane.</param>
    /// <param name="moveId">The interned id of the move command (ignored when <paramref name="hasMove"/> is false).</param>
    /// <param name="hasMove">Whether the move command is interned in the registry.</param>
    /// <param name="jumpId">The interned id of the jump command (ignored when <paramref name="hasJump"/> is false).</param>
    /// <param name="hasJump">Whether the jump command is interned in the registry.</param>
    /// <param name="interactId">The interned id of the interact command (ignored when <paramref name="hasInteract"/> is false).</param>
    /// <param name="hasInteract">Whether the interact command is interned in the registry.</param>
    /// <returns>The slot's intent for the tick.</returns>
    public static PlayerIntent FromLane(CommandLane lane, ushort moveId, bool hasMove, ushort jumpId, bool hasJump, ushort interactId, bool hasInteract, ushort cycleId = 0, bool hasCycle = false) {
        var move = Vector2.Zero;
        var jumpHeld = false;
        var jumpPressed = false;
        var jumpReleased = false;
        var interactPressed = false;
        var cyclePressed = false;

        if (hasMove && lane.TryGetEntry(commandId: moveId, entry: out var moveEntry)) {
            var stick = moveEntry.Value.AsAxis2D;

            // The fixed chase camera looks toward -Z, so stick-up (forward) maps to world -Z.
            move = new Vector2(x: stick.X, y: -stick.Y);
        }

        if (hasJump && lane.TryGetEntry(commandId: jumpId, entry: out var jumpEntry)) {
            jumpHeld = (jumpEntry.Phase is CommandPhase.Started or CommandPhase.Active);
            jumpPressed = (jumpEntry.Phase == CommandPhase.Started);
            jumpReleased = (jumpEntry.Phase == CommandPhase.Completed);
        }

        if (hasInteract && lane.TryGetEntry(commandId: interactId, entry: out var interactEntry)) {
            interactPressed = (interactEntry.Phase == CommandPhase.Started);
        }

        if (hasCycle && lane.TryGetEntry(commandId: cycleId, entry: out var cycleEntry)) {
            cyclePressed = (cycleEntry.Phase == CommandPhase.Started);
        }

        return new PlayerIntent(
            CyclePressed: cyclePressed,
            InteractPressed: interactPressed,
            JumpHeld: jumpHeld,
            JumpPressed: jumpPressed,
            JumpReleased: jumpReleased,
            Move: move
        );
    }
}
