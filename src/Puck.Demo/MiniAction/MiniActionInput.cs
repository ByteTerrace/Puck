using Puck.Commands;
using Puck.Input;

namespace Puck.Demo.MiniAction;

/// <summary>
/// MiniAction's command vocabulary and default controller mapping. The simulation reads these two commands per
/// tick from the input router's snapshot — gameplay input is command-driven like everything else, so it routes
/// through the same deterministic capture/snapshot path rather than a direct gamepad drain.
/// </summary>
internal static class MiniActionInput {
    /// <summary>The camera-relative move command (left stick), an <see cref="CommandValueKind.Axis2D"/>.</summary>
    public const string MoveCommand = "miniaction.move";
    /// <summary>The jump command (South face button), a <see cref="CommandValueKind.Digital"/>.</summary>
    public const string JumpCommand = "miniaction.jump";

    /// <summary>The default mapping, shared by every slot (per-player remaps would layer over this).</summary>
    public static IInputBindings DefaultBindings { get; } = new SharedInputBindings(
        bindings: InputBindingTable.Build(definitions: [
            new InputBindingDefinition(Command: MoveCommand, Source: InputSources.Gamepad.LeftStick),
            new InputBindingDefinition(Command: JumpCommand, Source: InputSources.Gamepad.ButtonSouth),
        ])
    );
}

/// <summary>Registers MiniAction's commands so a <see cref="CommandRegistry"/> interns them (move/jump). The handlers are no-ops — the simulation reads the values from the per-tick snapshot, not via dispatch.</summary>
internal sealed class MiniActionCommandModule : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            description: "Camera-relative move for the local player (left stick).",
            handler: static _ => CommandResult.None,
            name: MiniActionInput.MoveCommand,
            valueKind: CommandValueKind.Axis2D
        );
        yield return CommandDefinition.Verb(
            description: "Jump for the local player (South face button).",
            handler: static _ => CommandResult.None,
            name: MiniActionInput.JumpCommand,
            valueKind: CommandValueKind.Digital
        );
    }
}
