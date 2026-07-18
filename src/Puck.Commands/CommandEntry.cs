namespace Puck.Commands;

/// <summary>
/// One command's value and edge within a single tick's <see cref="CommandSnapshot"/>. The command is
/// identified by its interned <see cref="CommandId"/> (a stable ordinal, not a string) so the snapshot is
/// compact, hashable, and bit-identical across machines.
/// </summary>
/// <param name="CommandId">The interned command id (<see cref="CommandRegistry.TryGetId"/>).</param>
/// <param name="Value">The command's value for this tick.</param>
/// <param name="Phase">The edge this tick represents: <see cref="CommandPhase.Started"/> / <see cref="CommandPhase.Active"/> (held) / <see cref="CommandPhase.Completed"/>.</param>
/// <param name="Dispatch">Whether this entry's handler fires when the snapshot is applied. Held digitals reassert with
/// this false, while continuous analog routes re-dispatch; bindings that explicitly activate on release carry true on
/// their completed edge.</param>
/// <param name="Text">The original text line for a simulation-routed console command. Null for physical input. This is
/// deterministic snapshot payload and is serialized; it lets argument-bearing verbs execute at their assigned tick.</param>
/// <param name="Device">
/// The local device that produced this command, for output handlers that act on the originating controller
/// (e.g. rumble). This is a <em>local-only</em> annotation: it is excluded from the deterministic identity and
/// must not be hashed or serialized — the lane's slot is the cross-machine identity.
/// </param>
/// <param name="AssignedSlot">Whether the physical signal that produced this entry created its device-to-slot
/// assignment. Unlike <paramref name="Device"/>, this is deterministic snapshot semantics and is serialized so a
/// first-seat gesture is consumed identically during replay.</param>
public readonly record struct CommandEntry(
    ushort CommandId,
    CommandValue Value,
    CommandPhase Phase,
    bool Dispatch = true,
    string? Text = null,
    InputDeviceId Device = default,
    bool AssignedSlot = false
) {
    /// <summary>Whether applying this local live entry releases <see cref="TextCommandSource"/>'s deferred-mutation
    /// drain barrier. Local-only like <see cref="Device"/>; recordings reconstruct it as <see langword="false"/>.</summary>
    internal bool CompletesTextSubmission { get; init; }
}
