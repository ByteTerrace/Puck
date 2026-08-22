using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>Builds the operator-facing <c>player.assign</c> command over a live <see cref="PlayerRoster"/>. The
/// definition lives beside the roster and shared command vocabulary so command-level laws can exercise the same
/// parsing, authority, mutation, and narration path the root command module registers.</summary>
public static class PlayerAssignmentCommand {
    /// <summary>The help text shown for <c>player.assign</c>.</summary>
    public const string Description = "Moves a device between players: player.assign <keyboardN|mouseN|gamepadN|cameraN> <slot> (slot 1..4). Onto an occupied slot the device joins that team; onto an empty slot a keyboard, mouse, or gamepad creates a pending player (a profile must be chosen), while a passive camera is refused because it cannot create a player; onto its own slot a no-op. See world.devices for the tokens.";

    /// <summary>Creates the command definition backed by <paramref name="roster"/>.</summary>
    /// <param name="roster">The live local-player roster the command mutates.</param>
    /// <returns>The bindable <c>player.assign</c> definition.</returns>
    public static CommandDefinition Create(PlayerRoster roster) {
        ArgumentNullException.ThrowIfNull(roster);

        return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: PlayerCommandNames.AssignCommand,
            description: Description,
            handler: (context, args) => Handle(
                args: in args,
                context: context,
                roster: roster
            )
        );
    }

    /// <summary>Formats one device-assignment outcome for an operator command.</summary>
    /// <param name="roster">The roster whose current state successful changes echo.</param>
    /// <param name="verb">The command name responsible for the assignment.</param>
    /// <param name="outcome">The roster outcome to narrate.</param>
    /// <param name="slot">The zero-based destination slot.</param>
    /// <returns>The command result, marked as an error for every refusal.</returns>
    public static CommandResult Describe(PlayerRoster roster, string verb, AssignOutcome outcome, int slot) {
        ArgumentNullException.ThrowIfNull(roster);

        return (outcome switch {
            AssignOutcome.CreatedPending => new CommandResult(Output: $"[{verb}: player {PlayerRoster.DisplayNumber(slot: slot)} joined pending] {roster.Describe()}"),
            AssignOutcome.JoinedTeam => new CommandResult(Output: $"[{verb}: device moved to player {PlayerRoster.DisplayNumber(slot: slot)}] {roster.Describe()}"),
            AssignOutcome.NoOp => new CommandResult(Output: $"[{verb}: device already on player {PlayerRoster.DisplayNumber(slot: slot)}]"),
            AssignOutcome.DeviceClaimed => CommandResult.Error(output: $"[{verb}: this device is exclusively claimed and cannot be reassigned]"),
            AssignOutcome.TargetClaimed => CommandResult.Error(output: $"[{verb}: player {PlayerRoster.DisplayNumber(slot: slot)} is exclusively claimed — a device cannot move onto it]"),
            AssignOutcome.PassiveDeviceTargetEmpty => CommandResult.Error(output: $"[{verb}: a camera can join an existing player but cannot create player {PlayerRoster.DisplayNumber(slot: slot)}]"),
            AssignOutcome.Denied => CommandResult.Error(output: $"[{verb}: player {PlayerRoster.DisplayNumber(slot: slot)} — actor denied, see wire.errors/world.why]"),
            _ => CommandResult.Error(output: $"[{verb}: the roster is full ({PlayerRoster.MaxSlots} players)]"),
        });
    }

    private static CommandResult Handle(PlayerRoster roster, CommandContext context, in WireArgs args) {
        if (args.Count != 2) {
            return CommandResult.Error(output: "[player.assign: expected a device token and a slot — player.assign <keyboardN|mouseN|gamepadN|cameraN> <slot 1..4>]");
        }

        var deviceToken = args[0].ToString();

        if (!roster.TryResolveDeviceToken(
            device: out var device,
            token: deviceToken
        )) {
            return CommandResult.Error(output: $"[player.assign: no device '{deviceToken}' — see world.devices]");
        }

        if (
            !args.TryInt(index: 1, value: out var slot) ||
            (slot < 1) ||
            (slot > PlayerRoster.MaxSlots)
        ) {
            return CommandResult.Error(output: $"[player.assign: <slot> must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        var targetSlot = PlayerRoster.SlotFromDisplay(number: slot);

        return Describe(
            roster: roster,
            verb: PlayerCommandNames.AssignCommand,
            outcome: roster.AssignDevice(
                device: device,
                targetSlot: targetSlot,
                actingPrincipal: context.ActingPrincipal()
            ),
            slot: targetSlot
        );
    }
}
