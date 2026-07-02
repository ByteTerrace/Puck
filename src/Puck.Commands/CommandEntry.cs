namespace Puck.Commands;

/// <summary>
/// One command's value and edge within a single tick's <see cref="CommandSnapshot"/>. The command is
/// identified by its interned <see cref="CommandId"/> (a stable ordinal, not a string) so the snapshot is
/// compact, hashable, and bit-identical across machines.
/// </summary>
/// <param name="CommandId">The interned command id (<see cref="CommandRegistry.TryGetId"/>).</param>
/// <param name="Value">The command's value for this tick.</param>
/// <param name="Phase">The edge this tick represents: <see cref="CommandPhase.Started"/> / <see cref="CommandPhase.Active"/> (held) / <see cref="CommandPhase.Completed"/>.</param>
/// <param name="Device">
/// The local device that produced this command, for output handlers that act on the originating controller
/// (e.g. rumble). This is a <em>local-only</em> annotation: it is excluded from the deterministic identity and
/// must not be hashed or serialized — the lane's slot is the cross-machine identity.
/// </param>
public readonly record struct CommandEntry(ushort CommandId, CommandValue Value, CommandPhase Phase, InputDeviceId Device = default);
