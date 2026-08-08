using Puck.Commands;
using Puck.Input;

namespace Puck.Demo.Overworld;

/// <summary>
/// The overworld's command vocabulary and default controller mapping. The simulation reads these three commands per
/// tick from the input router's snapshot — gameplay input is command-driven like everything else, so it routes
/// through the same deterministic capture/snapshot path rather than a direct gamepad drain.
/// </summary>
internal static class OverworldInput {
    /// <summary>The camera-relative move command (left stick), an <see cref="CommandValueKind.Axis2D"/>.</summary>
    public const string MoveCommand = "overworld.move";
    /// <summary>The jump command (South face button), a <see cref="CommandValueKind.Digital"/>.</summary>
    public const string JumpCommand = "overworld.jump";
    /// <summary>The interact command (North face button — East is the GB joypad B, so interacting never leaks into a
    /// running game) — boots the console stand the player is standing at, a
    /// <see cref="CommandValueKind.Digital"/>.</summary>
    public const string InteractCommand = "overworld.interact";
    /// <summary>The disengage command (Left bumper): releases the console the player is currently seated at / driving
    /// and returns them to free room movement. Bound to a bumper deliberately — it is NOT a GB joypad line, so it
    /// cannot be swallowed by (or leak into) the machine the player is driving. A <see cref="CommandValueKind.Digital"/>.</summary>
    public const string LeaveCommand = "overworld.leave";
    /// <summary>The cycle command (Right bumper): advances the cartridge selected at the cabinet the player stands at,
    /// among the three carts (custom / camera / showcase) — live-swapping it if the cabinet is already running. A bumper
    /// (not a GB line) so it never leaks into the machine. A <see cref="CommandValueKind.Digital"/>.</summary>
    public const string CycleCommand = "overworld.cycle";

    /// <summary>The default mapping, shared by every slot (per-player remaps would layer over this).</summary>
    public static IInputBindings DefaultBindings { get; } = new SharedInputBindings(
        bindings: InputBindingTable.Build(definitions: [
            new InputBindingDefinition(Command: MoveCommand, Source: InputSources.Gamepad.LeftStick),
            new InputBindingDefinition(Command: JumpCommand, Source: InputSources.Gamepad.ButtonSouth),
            new InputBindingDefinition(Command: InteractCommand, Source: InputSources.Gamepad.ButtonNorth),
            new InputBindingDefinition(Command: LeaveCommand, Source: InputSources.Gamepad.LeftShoulder),
            new InputBindingDefinition(Command: CycleCommand, Source: InputSources.Gamepad.RightShoulder),
        ])
    );
}

/// <summary>Registers the overworld's commands so a <see cref="CommandRegistry"/> interns them (move/jump/interact). The handlers are no-ops — the simulation reads the values from the per-tick snapshot, not via dispatch.</summary>
internal sealed class OverworldCommandModule : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            description: "Camera-relative move for the local player (left stick).",
            handler: static _ => CommandResult.None,
            name: OverworldInput.MoveCommand,
            valueKind: CommandValueKind.Axis2D
        );
        yield return CommandDefinition.Verb(
            description: "Jump for the local player (South face button).",
            handler: static _ => CommandResult.None,
            name: OverworldInput.JumpCommand,
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Interact for the local player (North face button): boots the console stand the player is standing at.",
            handler: static _ => CommandResult.None,
            name: OverworldInput.InteractCommand,
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Disengage for the local player (Left bumper): leaves the console the player is seated at and returns to free room movement.",
            handler: static _ => CommandResult.None,
            name: OverworldInput.LeaveCommand,
            valueKind: CommandValueKind.Digital
        );
        yield return CommandDefinition.Verb(
            description: "Cycle the selected cartridge (Right bumper) at the cabinet the player stands at: custom -> camera -> showcase.",
            handler: static _ => CommandResult.None,
            name: OverworldInput.CycleCommand,
            valueKind: CommandValueKind.Digital
        );
    }
}
