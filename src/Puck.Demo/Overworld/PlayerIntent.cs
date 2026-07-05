using System.Numerics;

namespace Puck.Demo.Overworld;

/// <summary>
/// One player's input for one simulation tick — the ONLY thing the simulation consumes. It is small, serializable, and
/// source-agnostic: a local gamepad, the network, an AI, or a recording all produce the same value, so the simulation
/// is deterministic and replayable regardless of where the input came from (the netcode + replay seam).
/// </summary>
/// <param name="Move">The desired move direction on the floor plane, camera-relative, each component in [-1, 1]
/// (already dead-zoned). X = strafe, Y = forward; magnitude ≤ 1.</param>
/// <param name="JumpHeld">Whether the jump button is held this tick (drives variable jump height).</param>
/// <param name="JumpPressed">Whether jump transitioned to pressed on this tick (the edge that fills the jump buffer).</param>
/// <param name="JumpReleased">Whether jump transitioned to released on this tick (the edge that cuts a rising jump).</param>
/// <param name="InteractPressed">Whether interact transitioned to pressed on this tick (the edge that boots the console
/// stand the player is standing at, if any).</param>
/// <param name="RunHeld">Whether the run button is held this tick — the Mario hold-to-accelerate: the horizontal
/// speed target scales by the tuning's sprint multiplier while held. Defaults false, so every existing intent
/// stream (and its recorded hash) is unchanged.</param>
/// <param name="CyclePressed">Whether the cycle button transitioned to pressed on this tick — advances the selected
/// cartridge of the cabinet the player is standing at (live-swapping it when the cabinet is already running).</param>
public readonly record struct PlayerIntent(
    Vector2 Move,
    bool JumpHeld,
    bool JumpPressed,
    bool JumpReleased,
    bool InteractPressed = false,
    bool RunHeld = false,
    bool CyclePressed = false
) {
    /// <summary>An intent with no input — the neutral value for a tick a player produced nothing for.</summary>
    public static PlayerIntent None => default;
}
